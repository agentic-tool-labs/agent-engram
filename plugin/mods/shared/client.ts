import type { EngineInterface } from 'claude-code'
import { atom, read, update } from 'claude-code'
import type { ApiFailure, ApiResult, ModOp, ModOps } from './types'

export type Env = Pick<EngineInterface, 'plugin' | 'process' | 'http' | 'clock' | 'session' | 'state'>

const sharedState = atom({ plugin: 'engram', key: 'shared' } as const, {
  binary: undefined,
  sessionId: undefined,
  port: null,
  statusAt: 0,
  unsupported: false,
})

const STATUS_RETRY_MS = 60_000
const DEFAULT_CLI_TIMEOUT_MS = 5_000
const DEFAULT_API_TIMEOUT_MS = 1_000
// A hook's own budget is 10 s and `$.clock.sleep` is spent from it.
const MAX_API_TIMEOUT_MS = 2_000

export const ENGRAM_TOOLS = {
  recall: 'mcp__plugin_engram_engram__engram_recall',
  remember: 'mcp__plugin_engram_engram__engram_remember',
  revise: 'mcp__plugin_engram_engram__engram_revise',
  forget: 'mcp__plugin_engram_engram__engram_forget',
  expand: 'mcp__plugin_engram_engram__engram_expand',
} as const

export const EDIT_TOOLS = ['Edit', 'Write', 'MultiEdit'] as const

export async function engramBinary($: Env): Promise<string | undefined> {
  try {
    const cached = (await read($, sharedState)).binary
    if (cached !== undefined) return cached ?? undefined
    let found: string | null = null
    try {
      const run = await $.process.run([$.plugin.root + '/hooks/resolve-engram.sh'])
      const path = run.exitCode === 0 ? run.stdout.trim() : ''
      found = path === '' ? null : path
    } catch {
      found = null
    }
    await update($, sharedState, (s) => ({ ...s, binary: found }))
    return found ?? undefined
  } catch {
    return undefined
  }
}

export async function engramCli(
  $: Env,
  args: readonly string[],
  opts: { timeoutMs?: number } = {},
): Promise<{ exitCode: number; stdout: string } | undefined> {
  try {
    const binary = await engramBinary($)
    if (binary === undefined) return undefined
    const run = await $.process.run([binary, ...args], { timeoutMs: opts.timeoutMs ?? DEFAULT_CLI_TIMEOUT_MS })
    return { exitCode: run.exitCode, stdout: run.stdout }
  } catch {
    return undefined
  }
}

export async function sessionId($: Env): Promise<string> {
  const cached = (await read($, sharedState)).sessionId
  if (cached !== undefined) return cached
  const id = await $.session.id()
  await update($, sharedState, (s) => ({ ...s, sessionId: id }))
  return id
}

type Attempt = { kind: 'reply'; result: ApiResult<unknown> } | { kind: 'unreachable'; detail: string }

const inflight = new Map<string, Promise<Attempt>>()

const fail = (reason: ApiFailure['reason'], detail?: string): ApiFailure =>
  detail === undefined ? { ok: false, reason } : { ok: false, reason, detail }

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

/** Port of the running server from `engram status --json`, or why there is none. */
async function resolveBase($: Env): Promise<{ base: string } | ApiFailure> {
  const state = await read($, sharedState)
  if (state.unsupported) return fail('unsupported')
  if (state.port !== null) return { base: `http://127.0.0.1:${state.port}` }
  const now = await $.clock.now()
  if (state.statusAt !== 0 && now - state.statusAt < STATUS_RETRY_MS) return fail('server-down')

  const cli = await engramCli($, ['status', '--json'])
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
  await update($, sharedState, (s) => ({ ...s, port, statusAt: now }))
  return port === null ? fail('server-down') : { base: `http://127.0.0.1:${port}` }
}

async function post($: Env, url: string, mod: string, body: string): Promise<Attempt> {
  try {
    const res = await $.http.fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Engram-Mod': mod },
      body,
    })
    return { kind: 'reply', result: classify(res.status, res.text) }
  } catch (err) {
    return { kind: 'unreachable', detail: String(err) }
  }
}

export type ModApiOptions = { mod: string; timeoutMs?: number; signal?: AbortSignal; key?: string }

export function modApi<O extends ModOp>(
  $: Env,
  op: O,
  body: Omit<ModOps[O]['request'], 'mod'>,
  opts: ModApiOptions,
): Promise<ApiResult<ModOps[O]['response']>>
export function modApi<T>($: Env, op: string, body: object, opts: ModApiOptions): Promise<ApiResult<T>>
export async function modApi($: Env, op: string, body: object, opts: ModApiOptions): Promise<ApiResult<unknown>> {
  try {
    const base = await resolveBase($)
    if (!('base' in base)) return base

    const timeoutMs = Math.min(Math.max(opts.timeoutMs ?? DEFAULT_API_TIMEOUT_MS, 1), MAX_API_TIMEOUT_MS)
    const payload = JSON.stringify({ ...body, mod: opts.mod })
    const key = `${op}\u0000${opts.key ?? payload}`

    let request = inflight.get(key)
    if (request === undefined) {
      request = post($, `${base.base}/mod/v1/${op}`, opts.mod, payload).finally(() => inflight.delete(key))
      inflight.set(key, request)
    }

    // $.http.fetch cannot be cancelled, so the wait is a race; the loser's answer is dropped.
    const guard = new AbortController()
    const stop = () => guard.abort()
    opts.signal?.addEventListener('abort', stop)
    const timedOut = $.clock.sleep(timeoutMs, { signal: guard.signal }).then(
      () => 'timeout' as const,
      () => 'timeout' as const,
    )
    const outcome = await Promise.race([request, timedOut]).finally(() => {
      opts.signal?.removeEventListener('abort', stop)
      guard.abort()
    })

    if (outcome === 'timeout') return fail('timeout')
    if (outcome.kind === 'unreachable') {
      await update($, sharedState, (s) => ({ ...s, port: null }))
      return fail('server-down', outcome.detail)
    }
    if (!outcome.result.ok && outcome.result.reason === 'unsupported') {
      await update($, sharedState, (s) => ({ ...s, unsupported: true }))
    }
    return outcome.result
  } catch (err) {
    return fail('error', String(err))
  }
}
