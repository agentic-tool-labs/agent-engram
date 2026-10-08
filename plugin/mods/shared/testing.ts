import type { On } from 'claude-code'

export const MCP_TRIPWIRE_MESSAGE = 'mods must not call Engram over MCP'

export type FakeProcessResult = { exitCode: number; stdout: string; stderr?: string }
export type FakeProcessCall = { argv: readonly string[]; timeoutMs?: number }

/** Answer for `resolve-engram.sh` and `engram <args>`; a function sees the argv, a value is fixed. */
export type FakeProcessTable = {
  /** What `resolve-engram.sh` prints; absent or empty means "not installed". */
  binary?: string
  /** Keyed by the CLI arguments joined with a space, e.g. `'status --json'`. */
  cli?: Readonly<Record<string, FakeProcessResult | ((argv: readonly string[]) => FakeProcessResult)>>
}

export type FakeFetchCall = { url: string; op: string; headers: Record<string, string>; body: unknown }
export type FakeFetchReply = { status: number; text?: string; json?: unknown }
export type FakeFetchHandler = (call: FakeFetchCall) => FakeFetchReply | Promise<FakeFetchReply>

const done = (exitCode: number, stdout: string, stderr = '') => ({
  exitCode,
  stdout,
  stderr,
  isStdoutTruncated: false,
  isStderrTruncated: false,
})

export type FakeEngine = {
  processCalls: FakeProcessCall[]
  fetchCalls: FakeFetchCall[]
}

/**
 * Stands in for the engine beneath every plugin: `process.run` answered from `table`,
 * `http.fetch` routed per `/mod/v1/<op>` to `ops` (404 when an op has no handler), and a
 * bottom `mcp.call` hook that throws so a mod reaching Engram over MCP fails its suite.
 */
export function installFakeEngine(
  on: On,
  table: FakeProcessTable = {},
  ops: Readonly<Record<string, FakeFetchHandler>> = {},
): FakeEngine {
  const fake: FakeEngine = { processCalls: [], fetchCalls: [] }

  on('process.run', (_$, e) => {
    const argv = e.argv
    fake.processCalls.push({ argv, timeoutMs: e.init?.timeoutMs })
    if (argv.length === 1 && argv[0]!.endsWith('/hooks/resolve-engram.sh')) {
      return { value: done(0, table.binary ? table.binary + '\n' : '') }
    }
    const row = table.cli?.[argv.slice(1).join(' ')]
    const answer = typeof row === 'function' ? row(argv) : row
    return { value: done(answer?.exitCode ?? 127, answer?.stdout ?? '', answer?.stderr ?? '') }
  })

  on('http.fetch', async (_$, e) => {
    const headers = e.init?.headers ?? {}
    const call: FakeFetchCall = {
      url: e.url,
      op: new URL(e.url).pathname.replace(/^\/mod\/v1\//, ''),
      headers,
      body: e.init?.body === undefined ? undefined : JSON.parse(e.init.body),
    }
    fake.fetchCalls.push(call)
    const handler = ops[call.op]
    const reply = handler === undefined ? { status: 404 } : await handler(call)
    const text = reply.text ?? (reply.json === undefined ? '' : JSON.stringify(reply.json))
    return { value: { status: reply.status, ok: reply.status >= 200 && reply.status < 300, headers: {}, text } }
  })

  on('mcp.call', () => {
    throw new Error(MCP_TRIPWIRE_MESSAGE)
  })

  return fake
}
