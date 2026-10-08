import { expect, test } from 'claude-code/testing'
import { engramBinary, engramCli, modApi } from './client'
import type { ModApiOptions } from './client'
import { fakeModIo } from './testing'
import type { FetchHandler, RoutingTable } from './testing'

const BIN = '/fake/bin/engram'
const status = (Server: string, Port?: number, exitCode = Server === 'Running' ? 0 : 1) => ({
  exitCode,
  stdout: JSON.stringify(Port === undefined ? { Home: '/h', Server } : { Home: '/h', Server, Port }),
})
const RUNNING: RoutingTable = { binary: BIN, cli: { 'status --json': status('Running', 7433) } }
const OK: FetchHandler = () => ({ status: 200, json: { coverage: 'none' } })
const BODY = { session_id: 's1', query: 'q' }

const recall = (rig: ReturnType<typeof fakeModIo>, extra: Partial<ModApiOptions> = {}, body: object = BODY) =>
  modApi(rig.io, 'recall', body as never, { mod: 'lens', ...extra })

function deferred() {
  let release!: () => void
  const promise = new Promise<void>((r) => (release = r))
  return { promise, release }
}

test('binary not installed: engramBinary undefined, modApi server-down, nothing fetched', async () => {
  const rig = fakeModIo({ ops: { recall: OK } })
  expect(await engramBinary(rig.io)).toBeUndefined()
  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
  expect(rig.router.fetchCalls.length).toBe(0)
})

test('binary found: path is trimmed and resolved once per session', async () => {
  const rig = fakeModIo(RUNNING)
  expect(await engramBinary(rig.io)).toBe(BIN)
  expect(await engramBinary(rig.io)).toBe(BIN)
  expect(rig.router.processCalls.length).toBe(1)
  expect(rig.router.processCalls[0]!.argv).toEqual(['/fake/plugin/hooks/resolve-engram.sh'])
})

test('a non-installed answer is remembered too: the script runs once', async () => {
  const rig = fakeModIo({})
  await engramBinary(rig.io)
  await engramBinary(rig.io)
  expect(rig.router.processCalls.length).toBe(1)
})

for (const server of ['Wedged', 'Stale', 'NotRunning', 'Reused']) {
  test(`status ${server} is server-down and nothing is fetched`, async () => {
    const rig = fakeModIo({ binary: BIN, cli: { 'status --json': status(server) }, ops: { recall: OK } })
    expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
    expect(rig.router.fetchCalls.length).toBe(0)
  })
}

test('a Wedged status that still carries a Port is not used', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'status --json': status('Wedged', 7433) }, ops: { recall: OK } })
  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
  expect(rig.router.fetchCalls.length).toBe(0)
})

test('VersionMismatch exits 1 yet its Port is used: stdout is parsed regardless of exit code', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'status --json': status('VersionMismatch', 7434, 1) }, ops: { recall: OK } })
  expect((await recall(rig)).ok).toBe(true)
  expect(rig.router.fetchCalls[0]!.url).toBe('http://127.0.0.1:7434/mod/v1/recall')
})

test('status output that is not JSON is server-down, not a throw', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'status --json': { exitCode: 1, stdout: 'engram: boom' } }, ops: { recall: OK } })
  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
})

test('every request carries Content-Type and an X-Engram-Mod equal to the body mod', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: OK, fact: OK } })
  await recall(rig, { mod: 'toasts' })
  await modApi(rig.io, 'fact', { fact_id: 'f1' }, { mod: 'belief-diff' })
  expect(rig.router.fetchCalls.length).toBe(2)
  for (const c of rig.router.fetchCalls) {
    expect(c.headers['Content-Type']).toBe('application/json')
    expect(c.headers['X-Engram-Mod']).toBe((c.body as { mod: string }).mod)
  }
  expect(rig.router.fetchCalls.map((c) => c.headers['X-Engram-Mod'])).toEqual(['toasts', 'belief-diff'])
})

test('server older than the API: first call unsupported, the second makes no request', async () => {
  const rig = fakeModIo(RUNNING)
  expect(await recall(rig)).toEqual({ ok: false, reason: 'unsupported' })
  expect(await recall(rig)).toEqual({ ok: false, reason: 'unsupported' })
  expect(rig.router.fetchCalls.length).toBe(1)
})

test('404 with a not_found body is not-found and the API stays supported', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { fact: () => ({ status: 404, json: { error: 'not_found', detail: 'no such fact' } }) } })
  const ask = () => modApi(rig.io, 'fact', { fact_id: 'f999999' }, { mod: 'lens' })
  expect(await ask()).toEqual({ ok: false, reason: 'not-found', detail: 'no such fact' })
  expect(await ask()).toEqual({ ok: false, reason: 'not-found', detail: 'no such fact' })
  expect(rig.router.fetchCalls.length).toBe(2)
})

test('503, 400 and 500 map to not-initialised, bad-request and error', async () => {
  const rig = fakeModIo({
    ...RUNNING,
    ops: {
      recall: () => ({ status: 503, json: { error: 'not_initialised', detail: 'no config' } }),
      fact: () => ({ status: 400, json: { error: 'bad_request', detail: 'fx' } }),
      history: () => ({ status: 500, json: { error: 'internal' } }),
    },
  })
  expect(await recall(rig)).toEqual({ ok: false, reason: 'not-initialised', detail: 'no config' })
  expect(await modApi(rig.io, 'fact', { fact_id: 'fx' }, { mod: 'lens' })).toEqual({ ok: false, reason: 'bad-request', detail: 'fx' })
  expect(await modApi(rig.io, 'history', { fact_id: 'f1' }, { mod: 'lens' })).toEqual({ ok: false, reason: 'error', detail: 'HTTP 500' })
})

test('a 200 that is not JSON is an error value, not a throw', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: () => ({ status: 200, text: '<html>' }) } })
  expect((await recall(rig) as { reason: string }).reason).toBe('error')
})

test('timeout: the late answer changes nothing, even a 404 that would mark the API unsupported', async () => {
  const gate = deferred()
  let n = 0
  const rig = fakeModIo({
    ...RUNNING,
    ops: {
      recall: async () => {
        if (n++ === 0) {
          await gate.promise
          return { status: 404 }
        }
        return { status: 200, json: { coverage: 'high' } }
      },
    },
  })
  const first = recall(rig)
  await rig.clock.settle()
  await rig.clock.advance(1_000)
  expect(await first).toEqual({ ok: false, reason: 'timeout' })

  gate.release()
  await rig.clock.settle()

  const second = await recall(rig, {}, { session_id: 's1', query: 'other' })
  expect(second).toEqual({ ok: true, value: { coverage: 'high' } })
  expect(rig.shared().unsupported).toBe(false)
})

test('a caller arriving after the first timed out joins the pending fetch and gets an answer inside its own window', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate.promise, { status: 200, json: { coverage: 'partial' } }) } })
  const first = recall(rig)
  await rig.clock.settle()
  await rig.clock.advance(1_000)
  expect(await first).toEqual({ ok: false, reason: 'timeout' })

  const second = recall(rig)
  await rig.clock.settle()
  await rig.clock.advance(500)
  gate.release()
  await rig.clock.settle()
  expect(await second).toEqual({ ok: true, value: { coverage: 'partial' } })
  expect(rig.router.fetchCalls.length).toBe(1)
})

test('an answer just inside the timeout is not a timeout', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate.promise, { status: 200, json: { coverage: 'low' } }) } })
  const p = recall(rig)
  await rig.clock.settle()
  await rig.clock.advance(999)
  gate.release()
  await rig.clock.settle()
  expect(((await p) as { ok: boolean }).ok).toBe(true)
})

test('timeoutMs above 2000 is clamped to 2000', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate.promise, { status: 200, json: {} }) } })
  const p = recall(rig, { timeoutMs: 60_000 })
  await rig.clock.settle()
  await rig.clock.advance(1_999)
  await rig.clock.settle()
  gate.release()
  await rig.clock.settle()
  expect(((await p) as { ok: boolean }).ok).toBe(true)

  const gate2 = deferred()
  const rig2 = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate2.promise, { status: 200, json: {} }) } })
  const q = recall(rig2, { timeoutMs: 60_000 })
  await rig2.clock.settle()
  await rig2.clock.advance(2_000)
  expect(await q).toEqual({ ok: false, reason: 'timeout' })
  gate2.release()
  await rig2.clock.settle()
})

test('the hook signal aborting ends the wait as a timeout', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate.promise, { status: 200, json: {} }) } })
  const hook = new AbortController()
  const p = recall(rig, { signal: hook.signal })
  await rig.clock.settle()
  hook.abort()
  expect(await p).toEqual({ ok: false, reason: 'timeout' })
  gate.release()
  await rig.clock.settle()
})

test('two calls for the same (op, key) while one is in flight share one request', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { recall: async () => (await gate.promise, { status: 200, json: { coverage: 'none' } }) } })
  const a = recall(rig)
  const b = recall(rig, {}, { query: 'q', session_id: 's1' })
  await rig.clock.settle()
  gate.release()
  await rig.clock.settle()
  expect(await a).toEqual(await b)
  expect(rig.router.fetchCalls.length).toBe(1)
})

test('calls that differ in body or mod do not share a request', async () => {
  const gate = deferred()
  const rig = fakeModIo({ ...RUNNING, ops: { 'path-facts': async () => (await gate.promise, { status: 200, json: { facts: [] } }) } })
  const ask = (path: string, mod = 'sentinel') => modApi(rig.io, 'path-facts', { path }, { mod })
  const all = [ask('/r/a.cs'), ask('/r/b.cs'), ask('/r/a.cs', 'digest')]
  await rig.clock.settle()
  gate.release()
  await rig.clock.settle()
  await Promise.all(all)
  expect(rig.router.fetchCalls.length).toBe(3)
})

test('the same request made again after the first settled is a new request', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: OK } })
  await recall(rig)
  await recall(rig)
  expect(rig.router.fetchCalls.length).toBe(2)
})

test('status is looked up once while a port is known', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: OK } })
  await recall(rig)
  await recall(rig, {}, { session_id: 's1', query: 'two' })
  expect(rig.router.processCalls.filter((c) => c.argv.includes('status')).length).toBe(1)
})

test('no port from status: no second status spawn for 60 s, then one retry', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'status --json': status('NotRunning') }, ops: { recall: OK } })
  const spawns = () => rig.router.processCalls.filter((c) => c.argv.includes('status')).length

  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
  expect(spawns()).toBe(1)

  await rig.clock.advance(59_999)
  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
  expect(spawns()).toBe(1)

  await rig.clock.advance(1)
  await recall(rig)
  expect(spawns()).toBe(2)
})

test('a connection failure backs off 60 s, then the port is looked up again', async () => {
  let up = false
  const rig = fakeModIo({
    ...RUNNING,
    ops: {
      recall: () => {
        if (!up) throw new Error('ECONNREFUSED')
        return { status: 200, json: { coverage: 'none' } }
      },
    },
  })
  const spawns = () => rig.router.processCalls.filter((c) => c.argv.includes('status')).length

  expect(((await recall(rig)) as { reason: string }).reason).toBe('server-down')
  expect(spawns()).toBe(1)

  up = true
  await rig.clock.advance(59_000)
  expect(await recall(rig)).toEqual({ ok: false, reason: 'server-down' })
  expect(spawns()).toBe(1)

  await rig.clock.advance(1_000)
  expect(((await recall(rig)) as { ok: boolean }).ok).toBe(true)
  expect(spawns()).toBe(2)
})

test('engramCli runs the binary with the args, a 5 s default timeout, and never adds --home', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'embed --status --json': { exitCode: 0, stdout: '{}' } } })
  expect(await engramCli(rig.io, ['embed', '--status', '--json'])).toEqual({ exitCode: 0, stdout: '{}', stderr: '' })
  const run = rig.router.processCalls[1]!
  expect(run.argv).toEqual([BIN, 'embed', '--status', '--json'])
  expect(run.timeoutMs).toBe(5_000)
  expect(run.argv.some((a) => a.startsWith('--home'))).toBe(false)
})

test('engramCli passes a failing run through with its stderr text untouched', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { 'directive show f9': { exitCode: 2, stdout: '', stderr: 'no such fact: f9\n' } } })
  expect(await engramCli(rig.io, ['directive', 'show', 'f9'])).toEqual({ exitCode: 2, stdout: '', stderr: 'no such fact: f9\n' })
})

test('engramCli honours an explicit timeout', async () => {
  const rig = fakeModIo({ binary: BIN, cli: { version: { exitCode: 0, stdout: '1' } } })
  await engramCli(rig.io, ['version'], { timeoutMs: 250 })
  expect(rig.router.processCalls[1]!.timeoutMs).toBe(250)
})

test('engramCli without a binary is undefined and runs nothing else', async () => {
  const rig = fakeModIo({})
  expect(await engramCli(rig.io, ['status'])).toBeUndefined()
  expect(rig.router.processCalls.length).toBe(1)
})

test('engramCli and engramBinary return undefined when the process call throws', async () => {
  const rig = fakeModIo(RUNNING)
  const io = { ...rig.io, run: async () => { throw new Error('spawn failed') } }
  expect(await engramBinary(io)).toBeUndefined()
  expect(await engramCli(io, ['status'])).toBeUndefined()
})

test('modApi never throws: an io that rejects yields an error value', async () => {
  const rig = fakeModIo(RUNNING)
  const io = { ...rig.io, readShared: async () => { throw new Error('state gone') } }
  const r = await modApi(io, 'recall', BODY, { mod: 'lens' })
  expect(r).toEqual({ ok: false, reason: 'error', detail: 'Error: state gone' })
})

test('a hook signal that is already aborted is a timeout at once, with no request and no wait', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: OK } })
  const hook = new AbortController()
  hook.abort()
  expect(await recall(rig, { signal: hook.signal })).toEqual({ ok: false, reason: 'timeout' })
  expect(rig.router.fetchCalls.length).toBe(0)
  expect(rig.router.processCalls.length).toBe(0)
})

test('concurrent first calls share one status lookup', async () => {
  const rig = fakeModIo({ ...RUNNING, ops: { recall: OK } })
  await Promise.all([
    recall(rig, {}, { session_id: 's1', query: 'a' }),
    recall(rig, {}, { session_id: 's1', query: 'b' }),
    recall(rig, {}, { session_id: 's1', query: 'c' }),
  ])
  expect(rig.router.processCalls.filter((c) => c.argv.includes('status')).length).toBe(1)
  expect(rig.router.fetchCalls.length).toBe(3)
})

test('a connection error (fetch rejects, as the engine does for a refused port) clears the port and backs off', async () => {
  const rig = fakeModIo({
    ...RUNNING,
    ops: {
      recall: () => {
        throw new Error('HooksError: $.http.fetch(http://127.0.0.1:7433/mod/v1/recall) failed: ECONNREFUSED')
      },
    },
  })
  const r = (await recall(rig)) as { ok: false; reason: string; detail?: string }
  expect(r.reason).toBe('server-down')
  expect(r.detail).toContain('ECONNREFUSED')
  expect(rig.shared().port).toBeNull()
  expect(rig.shared().noPortAt).not.toBeNull()
})
