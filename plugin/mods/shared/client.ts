import type { SharedState } from './state'
import type { ApiFailure, ApiResult, ModOp, ModOps } from './types'

/**
 * The engine, as closures. The hooks scanner follows `$` only into a function declared in the
 * hook's own file, so each hooks file builds one of these from its `$` and hands it to the
 * client; the binding must be pure forwards (see binding.template.ts).
 */
export type ModIo = {
  run(argv: readonly string[], opts?: { timeoutMs?: number }): Promise<{ exitCode: number; stdout: string; stderr: string }>
  fetch(
    url: string,
    init: { method: string; headers: Record<string, string>; body: string },
  ): Promise<{ status: number; text: string }>
  sleep(ms: number, opts?: { signal?: AbortSignal }): Promise<void>
  now(): Promise<number>
  sessionId(): Promise<string>
  pluginRoot: string
  readShared(): Promise<SharedState>
  updateShared(fn: (s: SharedState) => SharedState): Promise<unknown>
}

export const SHARED_INITIAL: SharedState = { binary: null, port: null, noPortAt: null, unsupportedAt: null }

export const ENGRAM_TOOLS = {
  recall: 'mcp__plugin_engram_engram__engram_recall',
  remember: 'mcp__plugin_engram_engram__engram_remember',
  revise: 'mcp__plugin_engram_engram__engram_revise',
  forget: 'mcp__plugin_engram_engram__engram_forget',
  expand: 'mcp__plugin_engram_engram__engram_expand',
} as const

export const EDIT_TOOLS = ['Edit', 'Write'] as const

const NO_PORT_BACKOFF_MS = 60_000
const UNSUPPORTED_BACKOFF_MS = 300_000
const DEFAULT_CLI_TIMEOUT_MS = 5_000
const DEFAULT_API_TIMEOUT_MS = 1_000
// A hook's own budget is 10 s and the sleep is spent from it.
const MAX_API_TIMEOUT_MS = 2_000

export async function engramBinary(io: ModIo): Promise<string | undefined> {
  try {
    const cached = (await io.readShared()).binary
    if (cached !== null) return cached.path ?? undefined
    let path: string | null = null
    try {
      const run = await io.run([io.pluginRoot + '/hooks/resolve-engram.sh'])
      const printed = run.exitCode === 0 ? run.stdout.trim() : ''
      path = printed === '' ? null : printed
    } catch {
      path = null
    }
    await io.updateShared((s) => ({ ...s, binary: { path } }))
    return path ?? undefined
  } catch {
    return undefined
  }
}

export async function engramCli(
  io: ModIo,
  args: readonly string[],
  opts: { timeoutMs?: number } = {},
): Promise<{ exitCode: number; stdout: string; stderr: string } | undefined> {
  try {
    const binary = await engramBinary(io)
    if (binary === undefined) return undefined
    const run = await io.run([binary, ...args], { timeoutMs: opts.timeoutMs ?? DEFAULT_CLI_TIMEOUT_MS })
    return { exitCode: run.exitCode, stdout: run.stdout, stderr: run.stderr }
  } catch {
    return undefined
  }
}

type Attempt = { kind: 'reply'; result: ApiResult<unknown> } | { kind: 'unreachable'; detail: string }

// A promise is not state, so the in-flight map is module memory; it lives until the fetch settles.
const inflight = new Map<string, Promise<Attempt>>()

const fail = (reason: ApiFailure['reason'], detail?: string): ApiFailure =>
  detail === undefined ? { ok: false, reason } : { ok: false, reason, detail }

function canonical(value: unknown): string {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']'
  if (typeof value === 'object' && value !== null) {
    const record = value as Record<string, unknown>
    const keys = Object.keys(record)
      .filter((k) => record[k] !== undefined)
      .sort()
    return '{' + keys.map((k) => JSON.stringify(k) + ':' + canonical(record[k])).join(',') + '}'
  }
  return JSON.stringify(value) ?? 'null'
}

function classify(status: number, text: string): ApiResult<unknown> {
  let body: unknown
  try {
    body = JSON.parse(text)
  } catch {
    body = undefined
  }
  const record = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {}
  const detail = typeof record.detail === 'string' ? record.detail : undefined
  if (status >= 200 && status < 300) {
    return body === undefined ? fail('error', 'response was not JSON') : { ok: true, value: body }
  }
  if (status === 404) return record.error === 'not_found' ? fail('not-found', detail) : fail('unsupported')
  if (status === 503) return fail('not-initialised', detail)
  if (status === 400) return fail('bad-request', detail)
  return fail('error', detail ?? `HTTP ${status}`)
}

let resolving: Promise<{ base: string } | ApiFailure> | undefined

/** The server's base URL from `engram status --json`; concurrent callers share one lookup. */
function resolveBase(io: ModIo): Promise<{ base: string } | ApiFailure> {
  resolving ??= lookUpBase(io).finally(() => {
    resolving = undefined
  })
  return resolving
}

async function lookUpBase(io: ModIo): Promise<{ base: string } | ApiFailure> {
  const state = await io.readShared()
  const now = await io.now()
  if (state.unsupportedAt !== null && now - state.unsupportedAt < UNSUPPORTED_BACKOFF_MS) return fail('unsupported')
  if (state.port !== null) return { base: `http://127.0.0.1:${state.port}` }
  if (state.noPortAt !== null && now - state.noPortAt < NO_PORT_BACKOFF_MS) return fail('server-down')

  // The exit code is 1 unless the server is Running, so stdout is parsed regardless of it.
  const cli = await engramCli(io, ['status', '--json'])
  let port: number | null = null
  if (cli !== undefined) {
    try {
      const status = JSON.parse(cli.stdout) as { Server?: unknown; Port?: unknown }
      if ((status.Server === 'Running' || status.Server === 'VersionMismatch') && typeof status.Port === 'number') {
        port = status.Port
      }
    } catch {
      port = null
    }
  }
  await io.updateShared((s) => ({ ...s, port, noPortAt: port === null ? now : null }))
  return port === null ? fail('server-down') : { base: `http://127.0.0.1:${port}` }
}

async function post(io: ModIo, url: string, mod: string, body: string): Promise<Attempt> {
  try {
    const res = await io.fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Engram-Mod': mod },
      body,
    })
    return { kind: 'reply', result: classify(res.status, res.text) }
  } catch (err) {
    return { kind: 'unreachable', detail: String(err) }
  }
}

export type ModApiOptions = { mod: string; timeoutMs?: number; signal?: AbortSignal }

export function modApi<O extends ModOp>(
  io: ModIo,
  op: O,
  body: Omit<ModOps[O]['request'], 'mod'>,
  opts: ModApiOptions,
): Promise<ApiResult<ModOps[O]['response']>>
export function modApi<T>(io: ModIo, op: string, body: object, opts: ModApiOptions): Promise<ApiResult<T>>
export async function modApi(io: ModIo, op: string, body: object, opts: ModApiOptions): Promise<ApiResult<unknown>> {
  try {
    if (opts.signal?.aborted) return fail('timeout')
    const base = await resolveBase(io)
    if (!('base' in base)) return base

    const timeoutMs = Math.min(Math.max(opts.timeoutMs ?? DEFAULT_API_TIMEOUT_MS, 1), MAX_API_TIMEOUT_MS)
    const payload = { ...body, mod: opts.mod }
    const key = `${op}\u0000${canonical(payload)}`

    let request = inflight.get(key)
    if (request === undefined) {
      const sent = post(io, `${base.base}/mod/v1/${op}`, opts.mod, JSON.stringify(payload)).finally(() => {
        if (inflight.get(key) === sent) inflight.delete(key)
      })
      inflight.set(key, sent)
      request = sent
    }

    // The fetch cannot be cancelled, so each caller races it against its own timer; a loser's
    // answer is dropped and applied to nothing.
    const guard = new AbortController()
    const stop = () => guard.abort()
    opts.signal?.addEventListener('abort', stop)
    const timedOut = io.sleep(timeoutMs, { signal: guard.signal }).then(
      () => 'timeout' as const,
      () => 'timeout' as const,
    )
    const outcome = await Promise.race([request, timedOut]).finally(() => {
      opts.signal?.removeEventListener('abort', stop)
      guard.abort()
    })

    if (outcome === 'timeout') return fail('timeout')
    if (outcome.kind === 'unreachable') {
      const now = await io.now()
      await io.updateShared((s) => ({ ...s, port: null, noPortAt: now }))
      return fail('server-down', outcome.detail)
    }
    if (!outcome.result.ok && outcome.result.reason === 'unsupported') {
      const at = await io.now()
      await io.updateShared((s) => ({ ...s, unsupportedAt: at }))
    }
    return outcome.result
  } catch (err) {
    return fail('error', String(err))
  }
}
