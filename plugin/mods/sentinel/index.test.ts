import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'
import type { On } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { FetchHandler, ProcessResult } from '../shared/testing'
import type { PathFact } from '../shared/types'

const BIN = '/fake/bin/engram'
const FILE = '/repo/src/a.ts'
const running: ProcessResult = { exitCode: 0, stdout: JSON.stringify({ Home: '/h', Server: 'Running', Port: 7433 }) }
const stopped: ProcessResult = { exitCode: 1, stdout: JSON.stringify({ Home: '/h', Server: 'NotRunning' }) }

const fact = (n: number, body: string, extra: Partial<PathFact> = {}): PathFact => ({
  handle: `f${n}`,
  id: n,
  subject_path: 'src/a.ts#invariant-x',
  predicate: 'invariant',
  body,
  scope: 'repo',
  regenerable: false,
  valid_from: 1,
  ...extra,
})
const TWO = [fact(12, 'keep the list sorted'), fact(13, 'never log secrets')]
const answer = (facts: PathFact[], entity: string | null = 'src/a.ts'): FetchHandler => () => ({
  status: 200,
  json: { entity_path: entity, repo: '/repo', facts },
})

type Rig = ReturnType<typeof rig>

// Everything beneath the plugin: the Engram CLI and mod API, the clock, the tool itself, the
// session's directory, and the UI calls the mod makes (recorded).
function rig(
  on: On,
  opts: {
    path?: FetchHandler
    cli?: Record<string, ProcessResult | ((argv: readonly string[]) => ProcessResult)>
    status?: () => ProcessResult
    binary?: string
    toolContext?: readonly string[]
    toolDeny?: string
  } = {},
) {
  const router = installFakeEngine(on, {
    binary: 'binary' in opts ? opts.binary : BIN,
    cli: { 'status --json': () => (opts.status ?? (() => running))(), ...opts.cli },
    ops: opts.path === undefined ? {} : { 'path-facts': opts.path },
  })
  const clock = mock.clock(on)
  const ran: Record<string, unknown>[] = []
  const toasts: string[] = []
  const panes: { id: string; title?: string }[] = []
  on('session.cwd', () => ({ value: '/repo' }))
  on('tool.call', (_$, e) => {
    ran.push({ ...e })
    if (opts.toolDeny !== undefined) return { deny: opts.toolDeny }
    return { ref: 1, result: {}, text: 'ok', ...(opts.toolContext === undefined ? {} : { context: opts.toolContext }) }
  })
  on('ui.toast', (_$, e) => {
    toasts.push(e.text)
    return { value: undefined }
  })
  on('ui.open', (_$, e) => {
    panes.push(e)
    return { value: { isPlaced: true as const } }
  })
  on('command.register', (_$, e) => ({ value: { command: e.name } }))
  return { router, clock, ran, toasts, panes }
}

const edit = (file_path: string, extra: object = {}) =>
  ({ tool: 'Edit', file_path, old_string: 'a', new_string: 'b', ...extra }) as never
const run = ($: Parameters<TestBody>[0], command: string, args: string) =>
  $.command.run({ command, args, origin: { kind: 'composer' }, presentation: { isFullscreen: false, columns: 80 } })
const contextOf = (r: unknown) => (r as { context?: readonly string[] }).context
const denyOf = (r: unknown) => (r as { deny?: string }).deny
const BLOCK = 'Invariants recorded for src/a.ts:\n- [f12] keep the list sorted\n- [f13] never log secrets'
const pathFactsCalls = (r: Rig) => r.router.fetchCalls.filter((c) => c.op === 'path-facts')

test('inform: the edit runs untouched and the invariants are appended once, with one toast', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  const out = await $.tool.call(edit(FILE))
  expect(denyOf(out)).toBeUndefined()
  expect(contextOf(out)).toEqual([BLOCK])
  expect(r.ran.length).toBe(1)
  expect(r.ran[0]).toMatchObject({ tool: 'Edit', file_path: FILE, old_string: 'a', new_string: 'b' })
  expect(r.toasts).toEqual(['2 invariant(s) recorded for src/a.ts'])
  const [call] = pathFactsCalls(r)
  expect(call!.body).toEqual({ path: FILE, predicate: 'invariant', mod: 'sentinel' })
  expect(call!.headers['X-Engram-Mod']).toBe('sentinel')
})

test('a second edit of the same file makes no API call and adds nothing', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call(edit(FILE))
  const out = await $.tool.call(edit(FILE))
  expect(contextOf(out)).toBeUndefined()
  expect(pathFactsCalls(r).length).toBe(1)
  expect(r.toasts.length).toBe(1)
})

test('a file with no invariants costs one call, then is silent all session', async ($, on) => {
  const r = rig(on, { path: answer([]) })
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(pathFactsCalls(r).length).toBe(1)
  expect(r.toasts.length).toBe(0)
})

test('a different file is a different once', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call(edit(FILE))
  const out = await $.tool.call(edit('/repo/src/b.ts'))
  expect(contextOf(out)![0]).toMatch('Invariants recorded for src/b.ts:')
  expect(pathFactsCalls(r).length).toBe(2)
})

test('existing context from the tool is kept, the block follows it', async ($, on) => {
  rig(on, { path: answer(TWO), toolContext: ['earlier'] })
  expect(contextOf(await $.tool.call(edit(FILE)))).toEqual(['earlier', BLOCK])
})

test('a call the tool side denied gets nothing added', async ($, on) => {
  rig(on, { path: answer(TWO), toolDeny: 'no' })
  const out = await $.tool.call(edit(FILE))
  expect(denyOf(out)).toBe('no')
  expect(contextOf(out)).toBeUndefined()
})

test('Write is matched too', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call({ tool: 'Write', file_path: FILE, content: 'x' } as never)
  expect(pathFactsCalls(r).length).toBe(1)
})

test('Read and Bash are not matched: no API call', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call({ tool: 'Read', file_path: FILE } as never)
  await $.tool.call({ tool: 'Bash', command: 'ls' } as never)
  expect(r.router.fetchCalls.length).toBe(0)
  expect(r.router.processCalls.length).toBe(0)
})

test('NotebookEdit is not matched', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call({ tool: 'NotebookEdit', notebook_path: FILE, new_source: 'x' } as never)
  expect(r.router.fetchCalls.length).toBe(0)
})

test('deny-once: the first edit is denied with the block, the next proceeds, then silence', { options: { sentinel_mode: 'deny-once' } }, async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  const first = await $.tool.call(edit(FILE))
  expect(denyOf(first)).toBe(BLOCK + '\nRe-issue the edit if it respects these.')
  expect(r.ran.length).toBe(0)
  const second = await $.tool.call(edit(FILE))
  expect(denyOf(second)).toBeUndefined()
  expect(contextOf(second)).toBeUndefined()
  expect(r.ran.length).toBe(1)
  expect(pathFactsCalls(r).length).toBe(1)
  expect(r.toasts.length).toBe(0)
})

test('deny-once on a file with no invariants never denies', { options: { sentinel_mode: 'deny-once' } }, async ($, on) => {
  const r = rig(on, { path: answer([]) })
  expect(denyOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(r.ran.length).toBe(1)
})

test('off: no API call, no CLI call', { options: { sentinel_mode: 'off' } }, async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(r.router.fetchCalls.length).toBe(0)
  expect(r.router.processCalls.length).toBe(0)
})

test('an unrecognised mode behaves as the default, inform', { options: { sentinel_mode: 'loud' } as never }, async ($, on) => {
  rig(on, { path: answer(TWO) })
  expect(contextOf(await $.tool.call(edit(FILE)))).toEqual([BLOCK])
})

test('a relative file_path is resolved against the session directory; ".." is folded', async ($, on) => {
  const r = rig(on, { path: answer([]) })
  await $.tool.call(edit('src/a.ts'))
  await $.tool.call(edit('src/../lib/./b.ts'))
  expect(pathFactsCalls(r).map((c) => (c.body as { path: string }).path)).toEqual(['/repo/src/a.ts', '/repo/lib/b.ts'])
})

test('a missing file_path: untouched, no API call', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  const out = await $.tool.call({ tool: 'Edit', old_string: 'a', new_string: 'b' } as never)
  expect(denyOf(out)).toBeUndefined()
  expect(r.ran.length).toBe(1)
  expect(r.router.fetchCalls.length).toBe(0)
})

test('a subagent has its own once per file', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  await $.tool.call(edit(FILE))
  const sub = await $.tool.call(edit(FILE, { agentId: 'agent-1' }))
  expect(contextOf(sub)).toEqual([BLOCK])
  expect(contextOf(await $.tool.call(edit(FILE, { agentId: 'agent-1' })))).toBeUndefined()
  expect(pathFactsCalls(r).length).toBe(2)
})

test('two concurrent edits of one file announce exactly once', async ($, on) => {
  const r = rig(on, { path: answer(TWO) })
  const [a, b] = await Promise.all([$.tool.call(edit(FILE)), $.tool.call(edit(FILE))])
  expect([contextOf(a), contextOf(b)].filter((c) => c !== undefined).length).toBe(1)
  expect(r.ran.length).toBe(2)
  expect(r.toasts.length).toBe(1)
})

test('server down: the edit proceeds, the path is retried only after 60 s', async ($, on) => {
  let up = false
  const r = rig(on, { path: answer(TWO), status: () => (up ? running : stopped) })
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(r.ran.length).toBe(1)
  up = true
  await r.clock.advance(30_000)
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(r.router.fetchCalls.length).toBe(0)
  await r.clock.advance(31_000)
  expect(contextOf(await $.tool.call(edit(FILE)))).toEqual([BLOCK])
  expect(pathFactsCalls(r).length).toBe(1)
})

test('API unsupported (older server): the edit proceeds untouched', async ($, on) => {
  const r = rig(on)
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(r.ran.length).toBe(1)
})

test('a slow server: the edit goes ahead at 300 ms, the late answer changes nothing, a later edit retries', async ($, on) => {
  let release!: () => void
  const slow = new Promise<void>((res) => (release = res))
  let hold = true
  const r = rig(on, {
    path: async () => {
      if (hold) await slow
      return { status: 200, json: { entity_path: 'src/a.ts', repo: '/repo', facts: TWO } }
    },
  })
  const pending = $.tool.call(edit(FILE))
  await r.clock.advance(299)
  expect(r.ran.length).toBe(0)
  await r.clock.advance(1)
  const first = await pending
  expect(contextOf(first)).toBeUndefined()
  expect(r.ran.length).toBe(1)
  hold = false
  release()
  await r.clock.settle()
  expect(r.toasts.length).toBe(0)
  expect(contextOf(await $.tool.call(edit(FILE)))).toBeUndefined()
  expect(pathFactsCalls(r).length).toBe(1)
  await r.clock.advance(61_000)
  expect(contextOf(await $.tool.call(edit(FILE)))).toEqual([BLOCK])
})

test('three quick edits of one slow file share one request, none waits past 300 ms', async ($, on) => {
  const slow = new Promise<void>(() => {})
  const r = rig(on, { path: async () => (await slow, { status: 200 }) })
  const all = Promise.all([$.tool.call(edit(FILE)), $.tool.call(edit(FILE)), $.tool.call(edit(FILE))])
  await r.clock.advance(300)
  const outs = await all
  expect(outs.map(contextOf)).toEqual([undefined, undefined, undefined])
  expect(pathFactsCalls(r).length).toBe(1)
  expect(r.ran.length).toBe(3)
})

test('/invariant forwards file and statement to the CLI and toasts its line', async ($, on) => {
  const r = rig(on, {
    cli: { 'invariant add src/a.ts keep the list sorted': { exitCode: 0, stdout: '[f9] added: "keep the list sorted"\nmore\n' } },
  })
  const out = await run($, 'invariant', 'src/a.ts keep the list sorted')
  expect(out.text).toBe('[f9] added: "keep the list sorted"')
  expect(r.toasts).toEqual(['[f9] added: "keep the list sorted"'])
})

test('/invariant: a failing CLI toasts its first stderr line', async ($, on) => {
  const r = rig(on, {
    cli: { 'invariant add src/a.ts x': { exitCode: 1, stdout: '', stderr: '\nerror: not enrolled\nsecond\n' } },
  })
  const out = await run($, 'invariant', 'src/a.ts x')
  expect(out.text).toBe('error: not enrolled')
  expect(r.toasts).toEqual(['error: not enrolled'])
})

test('/invariant: a failing CLI that printed nothing still says it failed', async ($, on) => {
  const r = rig(on, { cli: { 'invariant add src/a.ts x': { exitCode: 2, stdout: 'noise', stderr: '  \n' } } })
  const out = await run($, 'invariant', 'src/a.ts x')
  expect(out.text).toBe('engram invariant add failed (exit 2).')
  expect(r.toasts.length).toBe(1)
})

test('/invariant: exit 0 with no stdout says Recorded, whatever stderr holds', async ($, on) => {
  rig(on, { cli: { 'invariant add src/a.ts x': { exitCode: 0, stdout: '', stderr: 'warning: slow' } } })
  expect((await run($, 'invariant', 'src/a.ts x')).text).toBe('Recorded.')
})

test('/invariant: no binary installed', async ($, on) => {
  const r = rig(on, { binary: '' })
  const out = await run($, 'invariant', 'src/a.ts x')
  expect(out.text).toBe('Engram binary not found.')
  expect(r.toasts).toEqual(['Engram binary not found.'])
})

test('/invariant: a file with no statement runs nothing', async ($, on) => {
  const r = rig(on)
  const out = await run($, 'invariant', 'src/a.ts')
  expect(out.text).toBe('Usage: /invariant <file> <statement>')
  expect(r.router.processCalls.length).toBe(0)
  expect(r.toasts.length).toBe(0)
})

const WHY_FACTS = [
  fact(1, 'code gist of a.ts', { predicate: 'gist', regenerable: true, subject_path: 'src/a.ts' }),
  fact(2, 'a decision about a.ts', { predicate: 'decision', subject_path: 'src/a.ts' }),
  fact(3, 'keep the list sorted'),
]

async function paneText($: Parameters<TestBody>[0]) {
  const ui = await $.ui.mount({ plugin: 'engram', surface: 'terminal', component: 'Pane', requestId: 'engram-why', props: {} as never })
  return JSON.stringify(await ui.drawn())
}

test('/why opens the pane: invariants first, then other authored facts, then code facts', async ($, on) => {
  const r = rig(on, { path: answer(WHY_FACTS) })
  const out = await run($, 'why', 'src/a.ts')
  expect(out.text).toBe('Opened engram-why for src/a.ts.')
  expect(r.panes[0]).toMatchObject({ id: 'engram-why' })
  const body = (r.router.fetchCalls[0]!.body as { path: string; predicate?: string })
  expect(body).toEqual({ path: FILE, mod: 'sentinel' })
  const drawn = await paneText($)
  const at = (s: string) => drawn.indexOf(s)
  expect(at('Invariants')).toBeGreaterThan(-1)
  expect(at('Invariants')).toBeLessThan(at('Other authored facts'))
  expect(at('Other authored facts')).toBeLessThan(at('Code facts'))
  expect(at('[f3] keep the list sorted')).toBeLessThan(at('[f2] a decision about a.ts'))
  expect(at('[f2] a decision about a.ts')).toBeLessThan(at('[f1] code gist of a.ts'))
})

test('/why on a file outside an enrolled repo says so', async ($, on) => {
  rig(on, { path: answer([], null) })
  await run($, 'why', 'src/a.ts')
  expect(await paneText($)).toMatch('Not inside an enrolled repo')
})

test('/why against a server without the mod API says it needs a newer Engram', async ($, on) => {
  rig(on)
  await run($, 'why', 'src/a.ts')
  expect(await paneText($)).toMatch('needs a newer Engram')
})

test('/why with no path opens nothing', async ($, on) => {
  const r = rig(on, { path: answer(WHY_FACTS) })
  const out = await run($, 'why', '  ')
  expect(out.text).toBe('Usage: /why <path>')
  expect(r.panes.length).toBe(0)
  expect(r.router.fetchCalls.length).toBe(0)
})
