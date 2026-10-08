import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'
import { isNewHighRecall } from './index'
import { installFakeEngine } from '../shared/testing'
import type { FetchHandler, RoutingTable } from '../shared/testing'

const BIN = '/fake/bin/engram'
const RUNNING = { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Server: 'Running', Port: 7433 }) } }
const NOW_MS = 5_000_000
const capture = (handle: string, body: string) => ({ handle, id: 1, body, created_at: 4_999 })
const reply =
  (...captures: ReturnType<typeof capture>[]): FetchHandler =>
  () => ({ status: 200, json: { captures } })
const forgot =
  (retracted: boolean): FetchHandler =>
  () => ({ status: 200, json: { handle: 'f2', retracted } })

type Rig = { toasts: string[]; clips: unknown[]; commands: string[]; deny: { audio: boolean; register: boolean }; boom: boolean; order: string[]; router: ReturnType<typeof installFakeEngine> }

function rig(on: On, ops: RoutingTable['ops'] = {}, table: RoutingTable = {}): Rig {
  mock.clock(on, { now: NOW_MS })
  const r: Rig = { toasts: [], clips: [], commands: [], deny: { audio: false, register: false }, boom: false, order: [], router: undefined as never }
  const routing: RoutingTable = {
    binary: BIN,
    cli: RUNNING,
    ...table,
    ops,
    get sessionId() {
      if (r.boom) throw new Error('session id unavailable')
      return table.sessionId
    },
  }
  r.router = installFakeEngine(on, routing)
  on('session.start', (_$, e) => ({ cwd: e.cwd }) as never)
  on('ui.toast', (_$, e) => (r.toasts.push(e.text), { value: undefined }))
  on('audio.play', (_$, e) => (r.deny.audio ? { deny: 'refused' } : (r.clips.push(e.clip), { value: undefined })))
  on('command.register', (_$, e) => (r.deny.register ? { deny: 'taken' } : (r.commands.push(e.name), { value: undefined })) as never)
  on('classic.UserPromptSubmit', () => (r.order.push('next'), {}))
  return r
}

const submit = ($: Engine, prompt: string) => $.classic.UserPromptSubmit({ prompt })
const undo = ($: Engine) =>
  $.command.run({
    command: 'undo-capture',
    args: '',
    origin: { kind: 'user' },
    presentation: { isFullscreen: false, columns: 80 },
  } as never)
const calls = (r: Rig, op: string) => r.router.fetchCalls.filter((c) => c.op === op)
const CHIME = { asset: 'mods/toasts/chime.wav' }
const HINT = ' · /undo-capture to forget'

test('a prompt with no capture: no toast, one captures call', async ($, on) => {
  const r = rig(on, { captures: reply() })
  await submit($, 'run the tests')
  expect(r.toasts).toEqual([])
  expect(calls(r, 'captures').length).toBe(1)
})

test('captures are asked for after next(e) resolved, never before', async ($, on) => {
  const r = rig(on, {
    captures: () => (r.order.push('captures'), { status: 200, json: { captures: [] } }),
  })
  await submit($, 'I live in Lyon')
  expect(r.order).toEqual(['next', 'captures'])
})

test('since is the second before now, and the session id rides the body', async ($, on) => {
  const r = rig(on, { captures: reply() }, { sessionId: 'sess-9' })
  await submit($, 'I live in Lyon')
  expect(calls(r, 'captures')[0]!.body).toEqual({ session_id: 'sess-9', since: 4_999, mod: 'toasts' })
})

test('two captures: two toasts with handle and statement only, both handles remembered', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'Jim likes tea'), capture('f2', 'Jim lives in Lyon')) })
  await submit($, 'I like tea and I live in Lyon')
  expect(r.toasts).toEqual(['Remembered [f1]: Jim likes tea' + HINT, 'Remembered [f2]: Jim lives in Lyon'])
  r.toasts.length = 0
  await submit($, 'again')
  expect(r.toasts).toEqual([])
})

test('a capture returned again by an overlapping window is not toasted again', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'Jim likes tea')) })
  await submit($, 'I like tea')
  await submit($, 'what time is it')
  expect(r.toasts).toEqual(['Remembered [f1]: Jim likes tea' + HINT])
})

test('a new handle beside an already shown one is toasted alone', async ($, on) => {
  let n = 0
  const r = rig(on, {
    captures: () => ({
      status: 200,
      json: { captures: n++ === 0 ? [capture('f1', 'a')] : [capture('f1', 'a'), capture('f3', 'b')] },
    }),
  })
  await submit($, 'one')
  await submit($, 'two')
  expect(r.toasts).toEqual(['Remembered [f1]: a' + HINT, 'Remembered [f3]: b'])
})

test('the hook returns the next result unchanged', async ($, on) => {
  rig(on, { captures: reply(capture('f1', 'a')) })
  expect(await submit($, 'I like tea')).toEqual({})
})

test('a slash command makes no API call; a slash mid-prompt does', async ($, on) => {
  const r = rig(on, { captures: reply() })
  await submit($, '/lens')
  expect(r.router.fetchCalls.length).toBe(0)
  await submit($, 'look at /etc/hosts')
  expect(calls(r, 'captures').length).toBe(1)
})

test('toasts_enabled false: no API calls at all', { options: { toasts_enabled: false } }, async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await submit($, 'I like tea')
  expect(r.router.fetchCalls.length).toBe(0)
  expect(r.router.processCalls.length).toBe(0)
  expect(r.toasts).toEqual([])
})

test('server not running: no toast and no error toast', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) }, { cli: { 'status --json': { exitCode: 1, stdout: '{"Server":"NotRunning"}' } } })
  await submit($, 'I like tea')
  expect(r.toasts).toEqual([])
  expect(r.router.fetchCalls.length).toBe(0)
})

test('API unsupported (404): no toast, no retry on the next prompt', async ($, on) => {
  const r = rig(on, {})
  await submit($, 'I like tea')
  await submit($, 'and coffee')
  expect(r.toasts).toEqual([])
  expect(calls(r, 'captures').length).toBe(1)
})

test('undo forgets exactly the most recent capture and says so', async ($, on) => {
  const r = rig(on, {
    captures: reply(capture('f1', 'a'), capture('f2', 'b')),
    forget: forgot(true),
  })
  await submit($, 'one and two')
  r.toasts.length = 0
  await undo($)
  expect(calls(r, 'forget').map((c) => c.body)).toEqual([{ session_id: 'claude-session-1', fact_id: 'f2', mod: 'toasts' }])
  expect(r.toasts).toEqual(['Forgot [f2]'])
})

test('undo twice: the second answers retracted false and says already forgotten', async ($, on) => {
  let first = true
  const r = rig(on, {
    captures: reply(capture('f2', 'b')),
    forget: () => ({ status: 200, json: { handle: 'f2', retracted: first ? ((first = false), true) : false } }),
  })
  await submit($, 'two')
  r.toasts.length = 0
  await undo($)
  await undo($)
  expect(r.toasts).toEqual(['Forgot [f2]', '[f2] is already forgotten'])
})

test('undo failure is a toast naming the reason', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f2', 'b')), forget: () => ({ status: 503 }) })
  await submit($, 'two')
  r.toasts.length = 0
  await undo($)
  expect(r.toasts).toEqual(['Could not forget [f2]: not-initialised'])
})

test('undo before any capture touches nothing', async ($, on) => {
  const r = rig(on, { forget: forgot(true) })
  await undo($)
  expect(calls(r, 'forget').length).toBe(0)
  expect(r.toasts).toEqual(['No captured memory to forget'])
})

test('chime off (default): audio.play is never called', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await submit($, 'I like tea')
  expect(r.clips).toEqual([])
})

test('chime captures: one clip per toast batch, none for an empty or repeated batch', { options: { chime: 'captures' } }, async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a'), capture('f2', 'b')) })
  await submit($, 'one and two')
  expect(r.clips).toEqual([CHIME])
  await submit($, 'again')
  expect(r.clips).toEqual([CHIME])
})

test('chime recalls: a capture does not chime', { options: { chime: 'recalls' } }, async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await submit($, 'I like tea')
  expect(r.clips).toEqual([])
})

test('chime both: a capture chimes', { options: { chime: 'both' } }, async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await submit($, 'I like tea')
  expect(r.clips).toEqual([CHIME])
})

test('a play that is refused is silent and the toasts still show', { options: { chime: 'captures' } }, async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  r.deny.audio = true
  await submit($, 'I like tea')
  expect(r.toasts).toEqual(['Remembered [f1]: a' + HINT])
})

test('the first capture toast of a session carries the undo hint; the second does not', async ($, on) => {
  let n = 0
  const r = rig(on, { captures: () => ({ status: 200, json: { captures: [capture(`f${++n}`, `s${n}`)] } }) })
  await submit($, 'one')
  await submit($, 'two')
  expect(r.toasts).toEqual(['Remembered [f1]: s1' + HINT, 'Remembered [f2]: s2'])
})

test('only the first toast of the first batch carries the hint', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a'), capture('f2', 'b')) })
  await submit($, 'one and two')
  expect(r.toasts).toEqual(['Remembered [f1]: a' + HINT, 'Remembered [f2]: b'])
})

const undoRegs = (r: Rig) => r.commands.filter((n) => n === 'undo-capture')
const start = ($: Engine) => $.session.start({ cwd: '/w', surface: 'terminal', isInteractive: true } as never)

test('session start registers /undo-capture before any capture, and a capture does not register it again', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await start($)
  expect(undoRegs(r)).toEqual(['undo-capture'])
  await submit($, 'I like tea')
  expect(undoRegs(r)).toEqual(['undo-capture'])
})

test('no registration happens without a session start', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await submit($, 'I like tea')
  expect(undoRegs(r)).toEqual([])
})

test('a command that cannot register does not stop session start or the toasts', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  r.deny.register = true
  await start($)
  await submit($, 'I like tea')
  expect(r.toasts).toEqual(['Remembered [f1]: a' + HINT])
})

test('toasts_enabled false registers no command', { options: { toasts_enabled: false } }, async ($, on) => {
  const r = rig(on, {})
  await start($)
  expect(undoRegs(r)).toEqual([])
})

test('a prompt event with no text still reaches next and throws nothing', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  await $.classic.UserPromptSubmit({ prompt: undefined } as never)
  expect(r.order).toEqual(['next'])
  expect(r.router.fetchCalls.length).toBe(0)
})

test('an unavailable session id costs the toast, never the chain', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')) })
  r.boom = true
  expect(await submit($, 'I like tea')).toEqual({})
  expect(r.toasts).toEqual([])
})

test('undo with an unavailable session id answers nothing and throws nothing', async ($, on) => {
  const r = rig(on, { captures: reply(capture('f1', 'a')), forget: forgot(true) })
  await submit($, 'I like tea')
  r.toasts.length = 0
  r.boom = true
  expect(await undo($)).toEqual({})
  expect(r.toasts).toEqual([])
  expect(calls(r, 'forget').length).toBe(0)
})

test('a system-sourced prompt with no new capture: one captures call, no toast', async ($, on) => {
  const r = rig(on, { captures: reply() })
  await $.classic.UserPromptSubmit({ prompt: 'task finished', source: 'system' })
  expect(calls(r, 'captures').length).toBe(1)
  expect(r.toasts).toEqual([])
})

const lens = (toolUseId: string, coverage: string) => ({ recalls: [{ toolUseId, coverage }], history: {} })

test('a new high-coverage recall chimes; partial, none, a rewrite of the same recall and an empty atom do not', () => {
  expect(isNewHighRecall(lens('t2', 'high'), lens('t1', 'partial'))).toBe(true)
  expect(isNewHighRecall(lens('t1', 'high'), undefined)).toBe(true)
  expect(isNewHighRecall(lens('t2', 'partial'), lens('t1', 'high'))).toBe(false)
  expect(isNewHighRecall(lens('t3', 'none'), lens('t2', 'high'))).toBe(false)
  expect(isNewHighRecall(lens('t2', 'high'), lens('t2', 'high'))).toBe(false)
  expect(isNewHighRecall({ recalls: [], history: {} }, lens('t2', 'high'))).toBe(false)
  expect(isNewHighRecall(undefined, undefined)).toBe(false)
})
