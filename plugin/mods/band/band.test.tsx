import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { RoutingTable } from '../shared/testing'
import type { EmbedStatus, LensRecall, LensState } from '../shared/state'

const SURFACES = ['terminal', 'desktop'] as const
const BIN = '/fake/bin/engram'

const recall = (over: Partial<LensRecall> = {}): LensRecall => ({
  toolUseId: 'tu1',
  turnId: 'turn-2',
  at: 0,
  query: 'where is the thing',
  parsed: true,
  isError: false,
  coverage: 'high',
  factCount: 3,
  notes: [],
  facts: [],
  raw: '',
  ...over,
})

const lensOf = (recalls: LensRecall[], currentTurnId: string | null = 'turn-2'): LensState => ({
  recalls,
  ...(currentTurnId === null ? {} : { currentTurnId }),
  history: {},
})

const embed = (over: Partial<EmbedStatus> = {}, backlog: Partial<EmbedStatus['backlog']> = {}): EmbedStatus => ({
  space: 'qwen',
  provider: 'local',
  embedded: 40,
  total: 100,
  remaining: 60,
  rate: '2/s',
  eta: 'about 30s',
  note: null,
  last_error: null,
  ...over,
  backlog: { state: 'running', pid: 1, last_update_seconds: 1, reason: null, ...backlog },
})

const cli = (stdout: object | string, exitCode = 0) => ({
  exitCode,
  stdout: typeof stdout === 'string' ? stdout : JSON.stringify(stdout),
})

type World = { setLens: (lens: LensState) => void; statuses: (string | undefined)[]; submits: string[]; processes: () => string[]; advance: (ms: number) => Promise<void> }

function world(on: On, lens: LensState | undefined, table: RoutingTable = { binary: BIN }): World {
  const router = installFakeEngine(on, table)
  const clock = mock.clock(on, { now: 1_000_000 })
  const statuses: (string | undefined)[] = []
  const submits: string[] = []
  let currentLens = lens
  if (lens !== undefined) on('state.get', { plugin: 'engram', key: 'lens' }, () => ({ value: { value: currentLens, version: 1 } }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('session.end', (_$, e) => ({ sessionId: e.sessionId }))
  on('ui.render', ($, e) => {
    const { Box } = $.ui.resolve(e)
    return <Box />
  })
  on('ui.status', (_$, e) => {
    statuses.push(e.text)
    return { value: undefined }
  })
  on('prompt.submit', (_$, e) => {
    submits.push(e.text)
    return { text: e.text }
  })
  return {
    setLens: (next) => {
      currentLens = next
    },
    statuses,
    submits,
    processes: () => router.processCalls.map((c) => c.argv.slice(1).join(' ')).filter((a) => a !== ''),
    advance: (ms) => clock.advance(ms),
  }
}

const PROPS = { hasSurvey: false, isWorking: false, maxRows: 5, bodyColumns: 80, scroll: { offset: 0, bodyRows: 5 }, view: {} }

async function draw(eng: Engine, surface: (typeof SURFACES)[number], props: Partial<typeof PROPS> = {}) {
  return eng.ui.mount({ plugin: 'engram', surface, component: 'AbovePrompt', props: { ...PROPS, ...props } })
}

// The first poll runs behind the hook, so the clock is let go once before anything is asserted.
async function start(eng: Engine, w: World) {
  await eng.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never)
  await w.advance(0)
}

for (const surface of SURFACES) {
  test(`${surface}: no recall this turn draws no coverage line`, async (eng, on) => {
    world(on, lensOf([]))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeUndefined()
  })

  test(`${surface}: a recall from the previous turn is gone once the next turn starts`, async (eng, on) => {
    world(on, lensOf([recall({ turnId: 'turn-1' })], 'turn-2'))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeUndefined()
  })

  test(`${surface}: no current turn id means no line`, async (eng, on) => {
    world(on, lensOf([recall()], null))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeUndefined()
  })

  for (const coverage of ['high', 'partial'] as const) {
    test(`${surface}: coverage ${coverage} shows the line and no button`, async (eng, on) => {
      world(on, lensOf([recall({ coverage, factCount: 3 })]))
      const ui = await draw(eng, surface)
      expect(await ui.find({ type: 'Text', text: `memory ${coverage} · 3 facts` })).toBeDefined()
      expect(await ui.find({ type: 'Button', key: 'remember' })).toBeUndefined()
    })
  }

  test(`${surface}: coverage none shows the line and the button`, async (eng, on) => {
    world(on, lensOf([recall({ coverage: 'none', factCount: 0 })]))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: 'memory none · 0 facts' })).toBeDefined()
    expect(await ui.find({ type: 'Button', key: 'remember' })).toBeDefined()
  })

  test(`${surface}: an availability note is shown verbatim next to the button`, async (eng, on) => {
    const note = 'overlap lane did not run (index being rebuilt)'
    world(on, lensOf([recall({ coverage: 'none', factCount: 0, notes: [note] })]))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: `memory none · 0 facts · ${note}` })).toBeDefined()
    expect(await ui.find({ type: 'Button', key: 'remember' })).toBeDefined()
  })

  test(`${surface}: a vector lane that is off prints nothing about vectors`, async (eng, on) => {
    world(on, lensOf([recall({ coverage: 'partial', notes: [] })]))
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: /vector/i })).toBeUndefined()
  })

  test(`${surface}: pressing the button submits one prompt and the button goes away`, async (eng, on) => {
    const w = world(on, lensOf([recall({ coverage: 'none', factCount: 0 })]))
    const ui = await draw(eng, surface)
    await ui.press({ key: 'remember' })
    expect(w.submits).toEqual([`Once you've worked out the answer to "where is the thing", save it to memory.`])
    const again = await draw(eng, surface)
    expect(await again.find({ type: 'Button', key: 'remember' })).toBeUndefined()
    expect(await again.find({ type: 'Text', text: 'memory none · 0 facts' })).toBeDefined()
    expect(w.submits.length).toBe(1)
  })

  test(`${surface}: a newer recall in the current turn gets its own button after an older one was pressed`, async (eng, on) => {
    const w = world(on, lensOf([recall({ toolUseId: 'tu1', coverage: 'none', factCount: 0 })]))
    const first = await draw(eng, surface)
    await first.press({ key: 'remember' })
    expect(w.submits.length).toBe(1)

    w.setLens(lensOf([recall({ toolUseId: 'tu2', query: 'second question', coverage: 'none', factCount: 0 }), recall({ toolUseId: 'tu1', coverage: 'none' })]))
    const second = await draw(eng, surface)
    expect(await second.find({ type: 'Button', key: 'remember' })).toBeDefined()
    await second.press({ key: 'remember' })
    expect(w.submits).toEqual([
      `Once you've worked out the answer to "where is the thing", save it to memory.`,
      `Once you've worked out the answer to "second question", save it to memory.`,
    ])
  })

  test(`${surface}: pressing twice before a redraw submits once`, async (eng, on) => {
    const w = world(on, lensOf([recall({ coverage: 'none', factCount: 0 })]))
    const ui = await draw(eng, surface)
    await Promise.all([ui.press({ key: 'remember' }), ui.press({ key: 'remember' })])
    expect(w.submits.length).toBe(1)
  })

  test(`${surface}: the button is hidden while a turn is running, the line stays`, async (eng, on) => {
    world(on, lensOf([recall({ coverage: 'none', factCount: 0 })]))
    const ui = await draw(eng, surface, { isWorking: true })
    expect(await ui.find({ type: 'Button', key: 'remember' })).toBeUndefined()
    expect(await ui.find({ type: 'Text', text: 'memory none · 0 facts' })).toBeDefined()
  })

  test(`${surface}: a survey holds the band, so nothing is drawn`, async (eng, on) => {
    world(on, lensOf([recall()]))
    const ui = await draw(eng, surface, { hasSurvey: true })
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeUndefined()
  })
}

test('one fact is singular', async (eng, on) => {
  world(on, lensOf([recall({ factCount: 1 })]))
  const ui = await draw(eng, 'terminal')
  expect(await ui.find({ type: 'Text', text: 'memory high · 1 fact' })).toBeDefined()
})

// The backlog line comes from the poller, so each of these starts a session first.
const TABLE = (embedOut: object | string | undefined, extra: RoutingTable['cli'] = {}): RoutingTable => ({
  binary: BIN,
  cli: embedOut === undefined ? extra : { 'embed --status --json': cli(embedOut), ...extra },
})

for (const surface of SURFACES) {
  test(`${surface}: a backlog shows embedded/total, the eta, and nothing else when running`, async (eng, on) => {
    const w = world(on, lensOf([]), TABLE(embed()))
    await start(eng, w)
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: 'embedding 40/100 · about 30s' })).toBeDefined()
  })

  test(`${surface}: a stalled backlog shows the CLI's reason verbatim`, async (eng, on) => {
    const w = world(on, lensOf([]), TABLE(embed({ eta: null }, { state: 'unavailable', reason: 'qwen3-embedding-0.6b is not downloaded yet' })))
    await start(eng, w)
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: 'embedding 40/100 · qwen3-embedding-0.6b is not downloaded yet' })).toBeDefined()
  })

  test(`${surface}: a backlog that is not running and gives no reason names its state`, async (eng, on) => {
    const w = world(on, lensOf([]), TABLE(embed({ eta: null }, { state: 'stalled' })))
    await start(eng, w)
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: 'embedding 40/100 · stalled' })).toBeDefined()
  })

  test(`${surface}: remaining 0 draws no backlog line`, async (eng, on) => {
    const w = world(on, lensOf([]), TABLE(embed({ remaining: 0, embedded: 100 })))
    await start(eng, w)
    const ui = await draw(eng, surface)
    expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeUndefined()
  })

  test(`${surface}: maxRows 1 with both lines draws the coverage line only`, async (eng, on) => {
    const w = world(on, lensOf([recall()]), TABLE(embed()))
    await start(eng, w)
    const ui = await draw(eng, surface, { maxRows: 1 })
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeUndefined()
  })

  test(`${surface}: with room for both, both lines are drawn`, async (eng, on) => {
    const w = world(on, lensOf([recall()]), TABLE(embed()))
    await start(eng, w)
    const ui = await draw(eng, surface, { maxRows: 2 })
    expect(await ui.find({ type: 'Text', text: /memory/ })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeDefined()
  })
}

test('the embed status exiting non-zero draws no backlog line and does not retry before 60 s', async (eng, on) => {
  const w = world(on, lensOf([]), { binary: BIN, cli: { 'embed --status --json': cli(embed(), 1) } })
  await start(eng, w)
  const ui = await draw(eng, 'terminal')
  expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeUndefined()
  const polls = () => w.processes().filter((a) => a === 'embed --status --json').length
  expect(polls()).toBe(1)
  await w.advance(59_000)
  expect(polls()).toBe(1)
  await w.advance(1_000)
  expect(polls()).toBe(2)
})

test('embed status that is not JSON draws no backlog line, and no status entry unless asked', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE('engram: boom'))
  await start(eng, w)
  const ui = await draw(eng, 'terminal')
  expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeUndefined()
  expect(w.statuses).toEqual([])
})

test('no binary installed: no backlog line, no status call, no retry storm', async (eng, on) => {
  const w = world(on, lensOf([]), {})
  await start(eng, w)
  const ui = await draw(eng, 'terminal')
  expect(await ui.find({ type: 'Text', text: /embedding/ })).toBeUndefined()
  await w.advance(30_000)
  expect(w.processes().filter((a) => a.includes('--status')).length).toBe(0)
  expect(w.statuses).toEqual([])
})

test('the poller outlives session.end, which also fires on /clear', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed({ remaining: 0, embedded: 100 })))
  await start(eng, w)
  const polls = () => w.processes().filter((a) => a === 'embed --status --json').length
  expect(polls()).toBe(1)
  await eng.session.end({ reason: 'clear', sessionId: 'old-session' } as never)
  await w.advance(60_000)
  expect(polls()).toBe(2)
})

test('while a backlog is draining under a running loop it is polled every 5 s', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed()))
  await start(eng, w)
  const polls = () => w.processes().filter((a) => a === 'embed --status --json').length
  expect(polls()).toBe(1)
  await w.advance(5_000)
  expect(polls()).toBe(2)
  await w.advance(5_000)
  expect(polls()).toBe(3)
})

test('with nothing left to embed it is polled every 60 s, not 5', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed({ remaining: 0, embedded: 100 })))
  await start(eng, w)
  const polls = () => w.processes().filter((a) => a === 'embed --status --json').length
  await w.advance(55_000)
  expect(polls()).toBe(1)
  await w.advance(5_000)
  expect(polls()).toBe(2)
})

test('a backlog that is not running is polled every 60 s', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed({}, { state: 'stalled' })))
  await start(eng, w)
  const polls = () => w.processes().filter((a) => a === 'embed --status --json').length
  await w.advance(30_000)
  expect(polls()).toBe(1)
})

const STATUS = cli({ Home: '/h', Server: 'Running', Version: '1.3.0' }, 0)
const ON = { options: { status_entry: true } }

test('status_entry on: the status line reads engram <state> <version> · embed e/t', ON, async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed(), { 'status --json': STATUS }))
  await start(eng, w)
  expect(w.statuses).toEqual(['engram Running 1.3.0 · embed 40/100'])
})

test('status_entry on, nothing left to embed: no embed part', ON, async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed({ remaining: 0 }), { 'status --json': STATUS }))
  await start(eng, w)
  expect(w.statuses).toEqual(['engram Running 1.3.0'])
})

test('status_entry on, a stopped server still reads from stdout although the exit code is 1', ON, async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed(), { 'status --json': cli({ Home: '/h', Server: 'NotRunning' }, 1) }))
  await start(eng, w)
  expect(w.statuses).toEqual(['engram NotRunning · embed 40/100'])
})

test('status_entry on, binary absent: the entry is cleared', ON, async (eng, on) => {
  const w = world(on, lensOf([]), {})
  await start(eng, w)
  expect(w.statuses).toEqual([undefined])
})

test('status_entry on, status output not JSON: the entry is cleared', ON, async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed(), { 'status --json': cli('boom', 1) }))
  await start(eng, w)
  expect(w.statuses).toEqual([undefined])
})

test('status_entry off (the default): $.ui.status is never called and status is never run', async (eng, on) => {
  const w = world(on, lensOf([]), TABLE(embed(), { 'status --json': STATUS }))
  await start(eng, w)
  await w.advance(10_000)
  expect(w.statuses).toEqual([])
  expect(w.processes().filter((a) => a === 'status --json').length).toBe(0)
})
