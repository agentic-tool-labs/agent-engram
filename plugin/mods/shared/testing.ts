import type { On } from 'claude-code'
import { SHARED_INITIAL } from './client'
import type { ModIo } from './client'
import type { SharedState } from './state'

export type ProcessResult = { exitCode: number; stdout: string; stderr?: string }
export type FetchCall = { url: string; op: string; headers: Record<string, string>; body: unknown }
export type FetchReply = { status: number; text?: string; json?: unknown }
export type FetchHandler = (call: FetchCall) => FetchReply | Promise<FetchReply>

/** The one routing table both helpers answer from. */
export type RoutingTable = {
  /** What `resolve-engram.sh` prints; absent or empty means "not installed". */
  binary?: string
  /** Keyed by the CLI arguments joined with a space, e.g. `'status --json'`. */
  cli?: Readonly<Record<string, ProcessResult | ((argv: readonly string[]) => ProcessResult)>>
  /** Keyed by op; an op with no handler answers 404, i.e. "unsupported". */
  ops?: Readonly<Record<string, FetchHandler>>
  sessionId?: string
}

export type Router = {
  processCalls: { argv: readonly string[]; timeoutMs?: number }[]
  fetchCalls: FetchCall[]
  process(argv: readonly string[], timeoutMs?: number): Required<ProcessResult>
  fetch(url: string, init: { headers?: Record<string, string>; body?: string }): Promise<{ status: number; ok: boolean; text: string }>
  sessionId(): string
}

export function createRouter(table: RoutingTable): Router {
  const router: Router = {
    processCalls: [],
    fetchCalls: [],
    process(argv, timeoutMs) {
      router.processCalls.push({ argv, timeoutMs })
      if (argv.length === 1 && argv[0]!.endsWith('/hooks/resolve-engram.sh')) {
        return { exitCode: 0, stdout: table.binary ? table.binary + '\n' : '', stderr: '' }
      }
      const row = table.cli?.[argv.slice(1).join(' ')]
      const answer = typeof row === 'function' ? row(argv) : row
      return { exitCode: answer?.exitCode ?? 127, stdout: answer?.stdout ?? '', stderr: answer?.stderr ?? '' }
    },
    async fetch(url, init) {
      const call: FetchCall = {
        url,
        op: new URL(url).pathname.replace(/^\/mod\/v1\//, ''),
        headers: init.headers ?? {},
        body: init.body === undefined ? undefined : JSON.parse(init.body),
      }
      router.fetchCalls.push(call)
      const handler = table.ops?.[call.op]
      const reply = handler === undefined ? { status: 404 } : await handler(call)
      const text = reply.text ?? (reply.json === undefined ? '' : JSON.stringify(reply.json))
      return { status: reply.status, ok: reply.status >= 200 && reply.status < 300, text }
    },
    sessionId: () => table.sessionId ?? 'claude-session-1',
  }
  return router
}

/**
 * Bottom hooks on the test `on` (beneath every plugin) answering `process.run`, `http.fetch` and
 * `session.id` from the table, for mod tests that drive their real hooks through engine events.
 * An op hook answers `{ value }`, not the bare result.
 */
export function installFakeEngine(on: On, table: RoutingTable = {}): Router {
  const router = createRouter(table)
  on('process.run', (_$, e) => {
    const r = router.process(e.argv, e.init?.timeoutMs)
    return { value: { ...r, isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('http.fetch', async (_$, e) => {
    const r = await router.fetch(e.url, { headers: e.init?.headers, body: e.init?.body })
    return { value: { ...r, headers: {} } }
  })
  on('session.id', () => ({ value: router.sessionId() }))
  return router
}

export type FakeClock = {
  now(): number
  /** Moves time on, firing each due sleep in order, then lets queued work run. */
  advance(ms: number): Promise<void>
  /** Lets queued work run without moving time. */
  settle(): Promise<void>
}

export type FakeModIo = {
  io: ModIo
  router: Router
  clock: FakeClock
  shared(): SharedState
}

/** A `ModIo` over the table, with a clock the test moves and an in-memory shared atom. */
export function fakeModIo(table: RoutingTable, options: { pluginRoot?: string; startAt?: number } = {}): FakeModIo {
  const router = createRouter(table)
  let time = options.startAt ?? 1_000_000
  let shared: SharedState = { ...SHARED_INITIAL }
  const timers: { due: number; fire: () => void }[] = []

  const settle = async () => {
    for (let i = 0; i < 25; i++) await Promise.resolve()
  }
  const clock: FakeClock = {
    now: () => time,
    settle,
    async advance(ms) {
      const target = time + ms
      for (;;) {
        const next = timers.filter((t) => t.due <= target).sort((a, b) => a.due - b.due)[0]
        if (next === undefined) break
        time = Math.max(time, next.due)
        timers.splice(timers.indexOf(next), 1)
        next.fire()
        await settle()
      }
      time = target
      await settle()
    },
  }

  const io: ModIo = {
    run: async (argv, opts) => {
      const r = router.process(argv, opts?.timeoutMs)
      return { exitCode: r.exitCode, stdout: r.stdout }
    },
    fetch: (url, init) => router.fetch(url, init),
    sleep: (ms, opts) =>
      new Promise<void>((resolve, reject) => {
        const timer = { due: time + ms, fire: resolve }
        timers.push(timer)
        opts?.signal?.addEventListener('abort', () => {
          const i = timers.indexOf(timer)
          if (i >= 0) timers.splice(i, 1)
          reject(new Error('aborted'))
        })
      }),
    now: async () => time,
    sessionId: async () => router.sessionId(),
    pluginRoot: options.pluginRoot ?? '/fake/plugin',
    readShared: async () => shared,
    updateShared: async (fn) => (shared = fn(shared)),
  }

  return { io, router, clock, shared: () => shared }
}
