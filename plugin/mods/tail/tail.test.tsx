import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'

import { ENGRAM_TOOLS } from '../shared/client'
import { installFakeEngine } from '../shared/testing'
import type { FetchCall, RoutingTable } from '../shared/testing'
import type { TailEventRecord, TailResponse, TailWrite } from '../shared/types'
import { AUTO_EVIDENCE, EVIDENCE } from '../digest/digest'
import type { TailRow } from '../shared/state'
import type { ApiFailure } from '../shared/types'
import { FEED_UNAVAILABLE, emptyText, eventText, failureOutcome, layoutRow } from './model'

const NOW_MS = Date.UTC(2026, 9, 8, 12, 0, 0)
const NOW = NOW_MS / 1000
const PANE = 'engram-tail'

const RUNNING: RoutingTable = {
  binary: '/fake/bin/engram',
  cli: { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Home: '/h', Server: 'Running', Port: 7433 }) } },
}

const write = (id: number, over: Partial<TailWrite> = {}): TailWrite => ({
  handle: `f${id}`,
  id,
  created_at: NOW,
  origin: 'note',
  body: `body of ${id}`,
  evidence: null,
  replaces: null,
  live: true,
  this_session: false,
  ...over,
})

const event = (kind: string, over: Partial<TailEventRecord> = {}): TailEventRecord => ({
  timestamp: new Date(NOW_MS).toISOString(),
  session_id: 'claude-session-1',
  kind,
  ...over,
})

const response = (over: Partial<TailResponse> = {}): TailResponse => ({
  head: 10,
  now: NOW,
  writes: { rows: [], skipped: null },
  retractions: { rows: [] },
  events: { epoch: 'ep1', head: 0, rows: [], skipped: null },
  ...over,
})

type World = {
  tails: () => FetchCall[]
  advance: (ms: number) => Promise<void>
  /** The responses the server gives in order; the last repeats. */
  script: (...responses: (TailResponse | { status: number; json?: unknown } | 'hang')[]) => void
  opened: string[]
  closed: string[]
}

function world(on: On, table: RoutingTable = RUNNING): World {
  let queue: (TailResponse | { status: number; json?: unknown } | 'hang')[] = [response()]
  let served = 0
  const opened: string[] = []
  const closed: string[] = []
  const router = installFakeEngine(on, {
    ...table,
    ops: {
      tail: () => {
        const next = queue[Math.min(served++, queue.length - 1)]!
        if (next === 'hang') return new Promise<never>(() => undefined)
        return 'status' in next ? { status: next.status, json: next.json } : { status: 200, json: next }
      },
    },
  })
  const clock = mock.clock(on, { now: NOW_MS })
  on('ui.open', (_$, e) => (opened.push((e as { id: string }).id), { value: { isPlaced: true } }) as never)
  on('ui.close', (_$, e) => (closed.push((e as { id: string }).id), { value: undefined }) as never)
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('command.register', (_$, e) => ({ value: { command: (e as { name: string }).name } }) as never)
  return {
    tails: () => router.fetchCalls.filter((c) => c.op === 'tail'),
    advance: (ms) => clock.advance(ms),
    script: (...responses) => {
      queue = responses
      served = 0
    },
    opened,
    closed,
  }
}

const toggle = ($: Engine) => $.command.run({ command: 'engram-tail' } as never)

const mountOnce = ($: Engine) =>
  $.ui.mount({ plugin: 'engram', surface: 'terminal', component: 'Pane', props: { title: 'Memory Tail', isFocused: false } as never, requestId: PANE })
const panes = new WeakMap<object, ReturnType<typeof mountOnce>>()

/** The engine refuses a second mount of one pane, so each test mounts it once and keeps looking at it. */
function mountPane($: Engine) {
  const pane = panes.get($) ?? mountOnce($)
  panes.set($, pane)
  return pane
}

async function shown($: Engine): Promise<string[]> {
  return (await (await mountPane($)).findAll({ type: 'Text' })).map((t) => t.text)
}

type Drawn = { type: string; props?: Record<string, unknown>; children?: (Drawn | string)[] }

const descendants = (node: Drawn | string): Drawn[] => (typeof node === 'string' ? [] : [node, ...(node.children ?? []).flatMap(descendants)])
const textOf = (node: Drawn | string): string => (typeof node === 'string' ? node : (node.children ?? []).map(textOf).join(''))
const drawn = async ($: Engine) => (await (await mountPane($)).drawn()) as unknown as Drawn
const buttons = (tree: Drawn) => descendants(tree).filter((n) => n.type === 'Button')

type RowView = { head: string; detail?: string; session: string; state: string; time: string; label: string; qualifier?: string }
const HEAD = /^(.)(.) (\d{2}:\d{2}:\d{2}) (\S+)(?: (.+))?$/

/** The pane's rows, newest first, as the host is given them: the head line and the optional detail line. */
async function rowViews($: Engine): Promise<RowView[]> {
  const boxes = descendants(await drawn($)).filter((n) => n.type === 'Box' && String(n.props?.['key'] ?? '').startsWith('row-'))
  const views: RowView[] = []
  for (const box of boxes) {
    const [head = '', detail] = (box.children ?? []).map(textOf)
    const m = HEAD.exec(head)
    if (m === null) continue
    views.push({
      head,
      ...(detail === undefined ? {} : { detail }),
      session: m[1]!,
      state: m[2]!,
      time: m[3]!,
      label: m[4]!,
      ...(m[5] === undefined ? {} : { qualifier: m[5] }),
    })
  }
  return views
}

/** Each row as one string: head then detail. */
const rowsOf = async ($: Engine) => (await rowViews($)).map((r) => (r.detail === undefined ? r.head : `${r.head} ${r.detail}`))

/** The filter list is closed until pressed. */
async function openFilter($: Engine) {
  await (await mountPane($)).press({ key: 'filter' })
}

async function openAndSettle($: Engine, w: World) {
  await toggle($)
  await w.advance(0)
}

test('M1: the composer registers the tail with a matcher on session.start and the engram tools', async ($, on) => {
  const registered: string[] = []
  installFakeEngine(on, RUNNING)
  mock.clock(on)
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('command.register', (_$, e) => (registered.push((e as { name: string }).name), { value: { command: (e as { name: string }).name } }) as never)

  await $.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never)

  expect(registered.filter((name) => name === 'engram-tail')).toEqual(['engram-tail'])
})

test('M2: a pane that is never opened makes no tail request in a minute', async ($, on) => {
  const w = world(on)
  await $.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never)

  await w.advance(60_000)

  expect(w.tails().length).toBe(0)
})

test('M3: an open pane polls every two seconds and a closed one stops', async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)
  expect(w.tails().length).toBe(1)

  await w.advance(1_999)
  expect(w.tails().length).toBe(1)
  await w.advance(1)
  expect(w.tails().length).toBe(2)
  await w.advance(2_000)
  expect(w.tails().length).toBe(3)

  await toggle($)
  await w.advance(30_000)
  expect(w.tails().length).toBe(3)
  expect(w.closed).toEqual([PANE])
})

test('M4: the first request carries no cursors and its rows are not drawn', async ($, on) => {
  const w = world(on)
  w.script(
    response({
      writes: { rows: [write(9)], skipped: 4 },
      retractions: { rows: [{ handle: 'f8', id: 8, retracted_at: NOW, reason: 'r', body: 'old', origin: 'note', this_session: true }] },
      events: { epoch: 'ep1', head: 3, rows: [{ seq: 3, record: event('recall', { query: 'q' }) }], skipped: 2 },
    }),
  )

  await openAndSettle($, w)

  const body = w.tails()[0]!.body as Record<string, unknown>
  expect(Object.keys(body).sort()).toEqual(['mod', 'scope', 'session_id'])
  expect(await shown($)).toContain('No activity yet.')
  expect((await rowsOf($)).length).toBe(0)
})

test('M4: a pane opened again after being closed starts from fresh cursors and keeps what it drew', async ($, on) => {
  const w = world(on)
  w.script(response({ head: 10 }), response({ head: 11, writes: { rows: [write(11)], skipped: null } }), response({ head: 40 }))
  await openAndSettle($, w)
  await w.advance(2_000)
  await toggle($)

  await toggle($)
  await w.advance(0)

  expect(Object.keys(w.tails()[2]!.body as Record<string, unknown>).sort()).toEqual(['mod', 'scope', 'session_id'])
  expect((await rowsOf($)).some((r) => r.includes(' f11 '))).toBe(true)
})

test('M5: later requests send the head, the retraction cursor and the event cursor', async ($, on) => {
  const w = world(on)
  w.script(
    response({ head: 10, now: NOW, events: { epoch: 'ep1', head: 5, rows: [], skipped: null } }),
    response({ head: 12, now: NOW + 2, writes: { rows: [write(11)], skipped: null }, events: { epoch: 'ep1', head: 7, rows: [], skipped: null } }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)
  await w.advance(2_000)

  const second = w.tails()[1]!.body as Record<string, unknown>
  expect(second).toMatchObject({ after: 10, closed_after: NOW, event_epoch: 'ep1', event_after: 5, scope: 'session', session_id: 'claude-session-1' })
  const third = w.tails()[2]!.body as Record<string, unknown>
  expect(third['after']).toBe(12)
  expect(third['event_after']).toBe(7)
})

test('M6: the scope is session unless the option says all', async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)
  expect((w.tails()[0]!.body as { scope: string }).scope).toBe('session')
})

test('M6: tail_scope all is sent as all', { options: { tail_scope: 'all' } }, async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)
  expect((w.tails()[0]!.body as { scope: string }).scope).toBe('all')
})

test('M7: rows that reach us twice are drawn once', async ($, on) => {
  const w = world(on)
  const retraction = { handle: 'f3', id: 3, retracted_at: NOW, reason: 'wrong', body: 'gone', origin: 'note', this_session: false }
  const again = response({
    head: 12,
    writes: { rows: [write(11)], skipped: null },
    retractions: { rows: [retraction] },
    events: { epoch: 'ep1', head: 1, rows: [{ seq: 1, record: event('recall', { query: 'dup' }) }], skipped: null },
  })
  w.script(response(), again, again)

  await openAndSettle($, w)
  await w.advance(2_000)
  await w.advance(2_000)

  const rows = await rowsOf($)
  expect(rows.filter((r) => r.includes(' f11 ')).length).toBe(1)
  expect(rows.filter((r) => r.includes(' retract ') && r.includes(' f3 ')).length).toBe(1)
  expect(rows.filter((r) => r.includes('"dup"')).length).toBe(1)
})

test('M8: within one second activity sits above its write, and a retraction above the write it follows', async ($, on) => {
  const w = world(on)
  const at = (offsetMs: number) => new Date(NOW_MS + offsetMs).toISOString()
  w.script(
    response(),
    response({
      head: 11,
      writes: { rows: [write(11, { created_at: NOW })], skipped: null },
      retractions: { rows: [{ handle: 'f11', id: 11, retracted_at: NOW, reason: 'r', body: 'x', origin: 'note', this_session: false }] },
      events: {
        epoch: 'ep1',
        head: 2,
        rows: [
          { seq: 2, record: event('recall', { query: 'half', timestamp: at(500) }) },
          { seq: 1, record: event('remember', { query: 'whole', timestamp: at(0) }) },
        ],
        skipped: null,
      },
    }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)

  const order = (await rowsOf($)).map((r) => (r.includes('"half"') ? 'half' : r.includes('"whole"') ? 'whole' : r.includes(' retract ') ? 'retract' : r.includes(' note ') ? 'write' : '?'))
  expect(order).toEqual(['half', 'whole', 'retract', 'write'])
})

test('M9: a retraction and a forget call each mark the write they name', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({ head: 12, writes: { rows: [write(11), write(12)], skipped: null } }),
    response({
      head: 12,
      retractions: { rows: [{ handle: 'f11', id: 11, retracted_at: NOW + 1, reason: 'r', body: 'b', origin: 'note', this_session: false }] },
    }),
  )
  on('tool.call', () => ({ result: {}, text: 'Retracted [f12].' }) as never)

  await openAndSettle($, w)
  await w.advance(2_000)
  const before = await rowsOf($)
  expect(before.some((r) => r.includes('✗'))).toBe(false)

  await w.advance(2_000)
  const afterRetraction = await rowsOf($)
  expect(afterRetraction.find((r) => r.includes(' note f11 '))).toContain('✗')
  expect(afterRetraction.find((r) => r.includes(' note f12 '))).not.toContain('✗')

  await $.tool.call({ tool: ENGRAM_TOOLS.forget, tool_use_id: 't1', fact_id: 'f12' } as never)
  const afterForget = await rowsOf($)
  expect(afterForget.find((r) => r.includes(' note f12 '))).toContain('✗')
})

test('M10: a new epoch is a marker without a replay, and a store behind the cursor says so', async ($, on) => {
  const w = world(on)
  w.script(
    response({ head: 10, events: { epoch: 'ep1', head: 5, rows: [], skipped: null } }),
    response({ head: 10, events: { epoch: 'ep2', head: 0, rows: [{ seq: 1, record: event('recall', { query: 'replayed' }) }], skipped: null } }),
    response({ head: 4, events: { epoch: 'ep2', head: 0, rows: [], skipped: null } }),
    response({ head: 4, events: { epoch: 'ep2', head: 0, rows: [], skipped: null } }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)
  expect(await shown($)).toContain('  event feed restarted')
  expect((await shown($)).some((t) => t.includes('replayed'))).toBe(false)

  await w.advance(2_000)
  expect(await shown($)).toContain('  store rewound — showing writes from f4')
  await w.advance(2_000)
  expect((w.tails()[3]!.body as { after: number; event_epoch: string }).after).toBe(4)
  expect((w.tails()[3]!.body as { event_epoch: string }).event_epoch).toBe('ep2')
})

test('M11: sessions and maintenance start hidden, a group toggle hides only its own kinds, and unknown kinds are Other', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      events: {
        epoch: 'ep1',
        head: 6,
        rows: [
          { seq: 6, record: event('zzz') },
          { seq: 5, record: event('index', { phase: 'finished' }) },
          { seq: 4, record: event('session-start', { long_term_fact_count: 3, tokens_returned: 40 }) },
          { seq: 3, record: event('mod-call', { mod: 'lens', tool: 'history' }) },
          { seq: 2, record: event('remember') },
          { seq: 1, record: event('recall', { query: 'q' }) },
        ],
        skipped: null,
      },
    }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)

  const kinds = async () => (await rowViews($)).map((r) => r.label)
  expect(await kinds()).toEqual(['zzz', 'mod-call', 'remember', 'recall'])

  const pane = await mountPane($)
  await openFilter($)
  await pane.press({ key: 'g-reads' })
  expect(await kinds()).toEqual(['zzz', 'mod-call', 'remember'])

  await pane.press({ key: 'g-sessions' })
  await pane.press({ key: 'g-maintenance' })
  expect(await kinds()).toEqual(['zzz', 'index', 'session-start', 'mod-call', 'remember'])
})

test('M12: each kind is drawn with its own fields, and a mod-call never shows a count', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      events: {
        epoch: 'ep1',
        head: 4,
        rows: [
          { seq: 4, record: event('zzz', { mode: 'm', decision: 'd', relation: 'rel', repo: 'r', phase: 'p', path: '/p', tool: 't', query: 'q' }) },
          { seq: 3, record: event('mod-call', { mod: 'lens', tool: 'recall', query: 'kestrel', coverage: 'partial', fact_count: 7 }) },
          { seq: 2, record: event('mod-call', { mod: 'toasts', tool: 'forget', fact_count: 9 }) },
          { seq: 1, record: event('recall', { query: 'kestrel', fact_count: 3, coverage: 'high' }) },
        ],
        skipped: null,
      },
    }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)

  const views = await rowViews($)
  const detail = (kind: string, n = 0) => views.filter((r) => r.label === kind)[n]!.detail
  expect(detail('recall')).toBe('3 facts · high "kestrel"')
  expect(detail('mod-call', 0)).toBe('lens · recall · partial "kestrel"')
  expect(detail('mod-call', 1)).toBe('toasts · forget')
  expect(detail('zzz')).toBe('"q" · t · /p · p · r · rel · d · m')
  expect(views.some((r) => `${r.detail}`.includes('7 facts') || `${r.detail}`.includes('9 facts'))).toBe(false)
})

test('M12: session starts show their counts and a subagent shows its type', { options: {} }, async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      events: {
        epoch: 'ep1',
        head: 2,
        rows: [
          { seq: 2, record: event('subagent-start', { long_term_fact_count: 5, tokens_returned: 60, agent_type: 'Explore' }) },
          { seq: 1, record: event('session-start', { long_term_fact_count: 5, tokens_returned: 80 }) },
        ],
        skipped: null,
      },
    }),
  )
  await openAndSettle($, w)
  await w.advance(2_000)
  await openFilter($)
  await (await mountPane($)).press({ key: 'g-sessions' })

  const views = await rowViews($)
  expect(views[0]).toMatchObject({ label: 'subagent-start', detail: '5 facts · 60 tok Explore' })
  expect(views[1]).toMatchObject({ label: 'session-start', detail: '5 facts · 80 tok' })
})

const FAILURES: { name: string; reply: { status: number; json?: unknown }; line: string; stops: boolean }[] = [
  { name: 'not-initialised', reply: { status: 503, json: { error: 'not_initialised' } }, line: 'Not initialised · engram init', stops: false },
  { name: 'bad-request', reply: { status: 400, json: { error: 'bad_request', detail: 'limit must be 1 to 50' } }, line: 'Tail request rejected:', stops: false },
  { name: 'error', reply: { status: 500, json: { error: 'internal' } }, line: 'Tail error · retrying', stops: false },
  { name: 'not-found', reply: { status: 404, json: { error: 'not_found' } }, line: 'Server too old for the tail · update, then /engram:restart', stops: true },
  { name: 'unsupported', reply: { status: 404 }, line: 'Server has no mod API', stops: true },
]

for (const failure of FAILURES) {
  test(`M13: ${failure.name} shows its line and ${failure.stops ? 'stops the loop' : 'retries in fifteen seconds'}`, async ($, on) => {
    const w = world(on)
    w.script(failure.reply)

    await openAndSettle($, w)

    expect(await shown($)).toContain(failure.line)
    if (failure.name === 'bad-request') expect(await shown($)).toContain('limit must be 1 to 50')
    expect(w.tails().length).toBe(1)
    if (failure.stops) {
      await w.advance(120_000)
      expect(w.tails().length).toBe(1)
    } else {
      await w.advance(14_999)
      expect(w.tails().length).toBe(1)
      await w.advance(1)
      expect(w.tails().length).toBe(2)
    }
  })
}

test('M13: a server that is not running says so, keeps trying, and clears the line when it answers', async ($, on) => {
  let up = false
  const w = world(on, {
    binary: '/fake/bin/engram',
    cli: {
      'status --json': () =>
        up
          ? { exitCode: 0, stdout: JSON.stringify({ Home: '/h', Server: 'Running', Port: 7433 }) }
          : { exitCode: 1, stdout: JSON.stringify({ Home: '/h', Server: 'NotRunning' }) },
    },
  })

  await openAndSettle($, w)
  expect(await shown($)).toContain('Server down · /engram:start')

  up = true
  await w.advance(90_000)
  expect(w.tails().length).toBeGreaterThan(0)
  expect((await shown($)).some((t) => t.includes('Server down'))).toBe(false)
})

test('M13: a request that outlasts its budget says the server is slow and tries again', async ($, on) => {
  const w = world(on)
  w.script('hang')
  await toggle($)
  await w.advance(1_000)

  expect(await shown($)).toContain('Server slow · retrying')
})

test('M13: a server without the activity feed keeps the two second cadence and says writes only', async ($, on) => {
  const w = world(on)
  w.script(response({ events: null }))

  await openAndSettle($, w)
  expect(await shown($)).toContain('Activity feed off · writes only')
  await w.advance(2_000)

  expect(w.tails().length).toBe(2)
})

test('M14: the digest labels come from the digest mod’s own constants', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      head: 13,
      writes: {
        rows: [
          write(13, { evidence: 'something else' }),
          write(12, { evidence: AUTO_EVIDENCE }),
          write(11, { evidence: EVIDENCE }),
        ],
        skipped: null,
      },
    }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)

  const rows = await rowsOf($)
  expect(rows.find((r) => r.includes(' f11 '))).toContain(' digest f11 ')
  expect(rows.find((r) => r.includes(' f12 '))).toContain(' digest·auto f12 ')
  expect(rows.find((r) => r.includes(' f13 '))).toContain(' note f13 ')
})

test('M15: every row reads as a time and another day is a separator line', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({ head: 12, writes: { rows: [write(12, { created_at: NOW }), write(11, { created_at: NOW - 30 * 3600 })], skipped: null } }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)

  const texts = await shown($)
  const rows = await rowsOf($)
  expect(rows.find((r) => r.includes(' f12 '))).toMatch(/^.. \d{2}:\d{2}:\d{2} note f12 /)
  expect(rows.find((r) => r.includes(' f11 '))).toMatch(/^.. \d{2}:\d{2}:\d{2} note f11 /)
  expect(texts.filter((t) => /^── \d{4}-\d{2}-\d{2} ──$/.test(t)).length).toBe(1)
})

test('M16: a skipped count is one marker and the ring keeps two hundred rows', async ($, on) => {
  const w = world(on)
  const burst = (from: number, to: number) => {
    const rows: TailWrite[] = []
    for (let id = to; id >= from; id--) rows.push(write(id, { created_at: NOW + id }))
    return rows
  }
  w.script(
    response(),
    response({ head: 130, writes: { rows: burst(11, 130), skipped: 5 } }),
    response({ head: 250, writes: { rows: burst(131, 250), skipped: 0 } }),
  )

  await openAndSettle($, w)
  await w.advance(2_000)
  expect((await shown($)).filter((t) => t.includes('more writes not shown')).length).toBe(1)
  expect(await shown($)).toContain('  … 5 more writes not shown')

  await w.advance(2_000)
  const rows = await rowsOf($)
  expect(rows.length).toBe(200)
  expect(rows[0]).toContain(' f250 ')
  expect(rows.some((r) => r.includes(' f11 '))).toBe(false)
})

test('M19: bodies, statements and queries are cut at 120 characters and marked, shorter ones are not', async ($, on) => {
  const w = world(on)
  const long = 'x'.repeat(130)
  const exact = 'y'.repeat(120)
  w.script(
    response(),
    response({
      head: 12,
      writes: { rows: [write(12, { body: long }), write(11, { body: exact })], skipped: null },
      retractions: { rows: [{ handle: 'f5', id: 5, retracted_at: NOW, reason: 'r', body: long, origin: 'note', this_session: false }] },
      events: { epoch: 'ep1', head: 1, rows: [{ seq: 1, record: event('recall', { query: long, fact_count: 1, coverage: 'high' }) }], skipped: null },
    }),
  )
  on('tool.call', () => ({ result: {}, text: '[f90] ok' }) as never)
  await openAndSettle($, w)
  await w.advance(2_000)
  await $.tool.call({ tool: ENGRAM_TOOLS.remember, tool_use_id: 't1', statement: 'z'.repeat(130) } as never)
  await $.tool.call({ tool: ENGRAM_TOOLS.recall, tool_use_id: 't2', query: 'q'.repeat(130) } as never)

  const rows = await rowsOf($)
  const cut = (c: string) => `${c.repeat(120)}…`
  expect(rows.find((r) => r.includes(' f12 '))).toContain(` ${cut('x')}`)
  expect(rows.find((r) => r.includes(' f11 '))).toContain(` ${exact}`)
  expect(rows.find((r) => r.includes(' f11 '))).not.toContain('…')
  expect(rows.find((r) => r.includes(' retract ') && r.includes(' f5 '))).toContain(cut('x'))
  expect(rows.find((r) => r.includes(' remember f90 '))).toContain(cut('z'))
  expect(rows.find((r) => r.includes(' recall ') && r.includes('qqq'))).toContain(`"${cut('q')}"`)
  expect(rows.find((r) => r.includes(' recall ') && r.includes('xxx'))).toContain(`"${cut('x')}"`)
})

test('M17: this session’s Engram calls become rows, and their handles mark later writes', async ($, on) => {
  const w = world(on)
  w.script(response(), response({ head: 77, writes: { rows: [write(77)], skipped: null } }))
  const answers: Record<string, string> = {
    [ENGRAM_TOOLS.remember]: '[f77] remembered: the tail is wired.',
    [ENGRAM_TOOLS.recall]: 'RECALL "kestrel" · 2 facts · 40/500 tokens · coverage: partial\n[f3] a (project · 0d)\n[f4] b (project · 0d)',
  }
  on('tool.call', (_$, e) => ({ result: {}, text: answers[(e as { tool: string }).tool] ?? '' }) as never)
  await openAndSettle($, w)

  await $.tool.call({ tool: ENGRAM_TOOLS.remember, tool_use_id: 't1', statement: 'The tail is wired.' } as never)
  await $.tool.call({ tool: ENGRAM_TOOLS.recall, tool_use_id: 't2', query: 'kestrel' } as never)

  const rows = await rowsOf($)
  expect(rows.find((r) => r.includes(' remember f77 '))).toContain('The tail is wired.')
  expect(rows.find((r) => r.includes(' recall '))).toContain('2 facts · partial "kestrel"')

  await w.advance(2_000)
  expect((await rowsOf($)).find((r) => r.includes(' note f77 '))!.startsWith('•')).toBe(true)
})

test('M17: other tools and look-alike names are not recorded', async ($, on) => {
  const w = world(on)
  on('tool.call', () => ({ result: {}, text: '' }) as never)
  await openAndSettle($, w)

  await $.tool.call({ tool: 'Bash', tool_use_id: 't1', command: 'true' } as never)
  await $.tool.call({ tool: 'mcp__other__engram_recall', tool_use_id: 't2', query: 'x' } as never)
  await $.tool.call({ tool: 'mcp__plugin_engram_engram__other', tool_use_id: 't3' } as never)
  await $.tool.call({ tool: 'x' + ENGRAM_TOOLS.recall, tool_use_id: 't4', query: 'x' } as never)

  expect((await rowsOf($)).length).toBe(0)
})

test('M17: under scope all the calls are not recorded twice', { options: { tail_scope: 'all' } }, async ($, on) => {
  const w = world(on)
  on('tool.call', () => ({ result: {}, text: '[f1] ok' }) as never)
  await openAndSettle($, w)

  await $.tool.call({ tool: ENGRAM_TOOLS.remember, tool_use_id: 't1', statement: 's' } as never)

  expect((await rowsOf($)).length).toBe(0)
})

test('M18: calls made while the pane was closed are not shown when it opens', async ($, on) => {
  const w = world(on)
  on('tool.call', () => ({ result: {}, text: '[f5] ok' }) as never)
  await $.tool.call({ tool: ENGRAM_TOOLS.remember, tool_use_id: 't1', statement: 'before the pane' } as never)

  await openAndSettle($, w)

  expect((await rowsOf($)).length).toBe(0)
  expect((await shown($)).some((t) => t.includes('before the pane'))).toBe(false)
})

// ---------------------------------------------------------------------------------------------
// The small-pane presentation. The host cuts a line to a width the mod cannot read, so these hold
// the shape of what the mod hands over; `truncateEnd` stands in for the host as the ruler.

/** A model of the host's `truncate-end`: a line that fits is whole, otherwise width - 1 characters and a mark. */
const truncateEnd = (line: string, width: number) => (line.length <= width ? line : `${line.slice(0, width - 1)}…`)

const row = (over: Partial<TailRow> = {}): TailRow => ({
  key: 'w12',
  ms: NOW_MS,
  rank: 0,
  id: 12,
  kind: 'write',
  group: 'writes',
  label: 'note',
  handle: 'f12',
  text: 'body',
  mine: false,
  retracted: false,
  ...over,
})

const MARKS_AND_TIME = /^[• ][✗ ] \d{2}:\d{2}:\d{2}/

test('W1: the marks and the time survive a 24-column cut for every kind of row', () => {
  const rows = [
    row({ label: 'subagent-start', kind: 'event', handle: undefined, text: '' }),
    row({ mine: true, retracted: true, qualifier: '← f3' }),
    row({ kind: 'retraction', label: 'retract', qualifier: 'compaction', mine: true }),
    row({ kind: 'call', label: 'remember', mine: true }),
    row({ kind: 'event', label: 'mod-call', handle: undefined }),
  ]
  for (const r of rows) expect(truncateEnd(layoutRow(r, []).head, 24)).toMatch(MARKS_AND_TIME)
})

test('W2: a label of eleven characters or fewer survives a 24-column cut with a qualifier present', () => {
  const labels = ['directive', 'invariant', 'compaction', 'digest·auto', 'revision', 'capture', 'note', 'other', 'retract', 'recall']
  for (const label of labels) {
    expect(label.length).toBeLessThanOrEqual(11)
    for (const marks of [{ mine: true, retracted: true }, { mine: false, retracted: false }]) {
      const head = layoutRow(row({ label, qualifier: '← f3', ...marks }), []).head
      expect(truncateEnd(head, 24)).toContain(` ${label}`)
      expect(truncateEnd(head, 24).replace('…', '')).toContain(label)
    }
  }
})

test('W3: a recall at 24 columns keeps its count and coverage and loses its query', () => {
  const query = 'where is the kestrel config'
  const record = event('recall', { fact_count: 7, coverage: 'high', query })
  const detail = layoutRow(row({ kind: 'event', label: 'recall', handle: undefined, text: eventText(record) }), []).detail!

  expect(truncateEnd(detail, 24)).toContain('7 facts · high')
  expect(truncateEnd(detail, 24)).not.toContain(query)
})

test('W4: a write at 24 columns keeps its handle and the start of its body', () => {
  const detail = layoutRow(row({ text: 'The kestrel service binds loopback only.' }), []).detail!

  expect(truncateEnd(detail, 24)).toMatch(/^f12 The kestrel/)
  expect(detail.startsWith('f12 ')).toBe(true)
})

test('W5: at 120 columns nothing is lost from a row built from fields under the cap', () => {
  const body = 'a body of an ordinary length that is well under the cap'
  const cases: { row: TailRow; head: RegExp; detail: string | undefined }[] = [
    {
      row: row({ text: body, qualifier: '← f3', mine: true }),
      head: /^•  \d{2}:\d{2}:\d{2} note ← f3$/,
      detail: `f12 ${body}`,
    },
    {
      row: row({ kind: 'retraction', label: 'retract', qualifier: 'compaction', text: 'old body — wrong' }),
      head: /^ ✗ \d{2}:\d{2}:\d{2} retract compaction$/,
      detail: 'f12 old body — wrong',
    },
    {
      row: row({
        kind: 'event',
        label: 'subagent-start',
        handle: undefined,
        text: eventText(event('subagent-start', { long_term_fact_count: 5, tokens_returned: 60, agent_type: 'Explore' })),
      }),
      head: /^ {3}\d{2}:\d{2}:\d{2} subagent-start$/,
      detail: '5 facts · 60 tok Explore',
    },
  ]
  for (const { row: r, head: expectedHead, detail: expectedDetail } of cases) {
    const { head, detail } = layoutRow(r, [])
    expect(truncateEnd(head, 120)).toMatch(expectedHead)
    expect(detail === undefined ? undefined : truncateEnd(detail, 120)).toBe(expectedDetail)
  }
})

test('W6: no row line can wrap, and the only wrapping text is the status', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      head: 12,
      writes: { rows: [write(12, { body: 'w'.repeat(80) }), write(11, { replaces: 'f3' })], skipped: null },
      retractions: { rows: [{ handle: 'f5', id: 5, retracted_at: NOW, reason: 'r', body: 'old', origin: 'note', this_session: false }] },
      events: { epoch: 'ep1', head: 1, rows: [{ seq: 1, record: event('remember', { query: 'q' }) }], skipped: null },
    }),
    response({ head: 12, events: null }),
  )
  await openAndSettle($, w)
  await w.advance(2_000)
  await w.advance(2_000)

  const nodes = descendants(await drawn($))
  const texts = nodes.filter((n) => n.type === 'Text')
  const wrapping = texts.filter((n) => n.props?.['wrap'] === 'wrap')
  expect(wrapping.map(textOf)).toEqual([FEED_UNAVAILABLE])

  const rowBoxes = nodes.filter((n) => n.type === 'Box' && String(n.props?.['key'] ?? '').startsWith('row-'))
  expect(rowBoxes.length).toBeGreaterThanOrEqual(4)
  for (const box of rowBoxes) {
    for (const line of (box.children ?? []).filter((c): c is Drawn => typeof c !== 'string')) {
      expect(line.type).toBe('Text')
      expect(line.props?.['wrap']).toBe('truncate-end')
    }
  }
})

test('W7: a row with nothing to say after its head is one line, never a blank second one', async ($, on) => {
  const w = world(on)
  w.script(response(), response({ events: { epoch: 'ep1', head: 1, rows: [{ seq: 1, record: event('remember') }], skipped: null } }))
  await openAndSettle($, w)
  await w.advance(2_000)

  const boxes = descendants(await drawn($)).filter((n) => n.type === 'Box' && String(n.props?.['key'] ?? '').startsWith('row-'))
  expect(boxes.length).toBe(1)
  expect(boxes[0]!.children?.length).toBe(1)
})

test('W8: the filter starts collapsed as one short button', async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)

  const all = buttons(await drawn($))
  expect(all.map((b) => b.props?.['label'])).toEqual(['Filter 6/8 ▾'])
  expect(String(all[0]!.props?.['label']).length).toBeLessThanOrEqual(14)
})

test('W9: the expanded list is eight toggles, one per line, none wider than thirteen characters', async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)
  await openFilter($)

  const tree = await drawn($)
  const header = buttons(tree).find((b) => b.props?.['key'] === 'filter')!
  expect(header.props?.['label']).toBe('Filter 6/8 ▴')

  const list = descendants(tree).find((n) => n.type === 'Box' && (n.children ?? []).filter((c) => typeof c !== 'string' && c.type === 'Button').length === 8)!
  expect(list.props?.['flexDirection']).toBe('column')
  const toggles = (list.children ?? []).filter((c): c is Drawn => typeof c !== 'string')
  expect(toggles.map((t) => t.type)).toEqual(Array(8).fill('Button'))
  const labels = toggles.map((t) => String(t.props?.['label']))
  expect(labels).toEqual(['✓ Writes', '✓ Retractions', '✓ Reads', '✓ Remember', '✓ Mods', '· Sessions', '· Maintenance', '✓ Other'])
  for (const label of labels) expect(label.length).toBeLessThanOrEqual(13)
})

test('W10: a toggle flips its group, updates the count and keeps the list open', async ($, on) => {
  const w = world(on)
  await openAndSettle($, w)
  await openFilter($)

  await (await mountPane($)).press({ key: 'g-sessions' })

  const all = buttons(await drawn($))
  const labels = all.map((b) => String(b.props?.['label']))
  expect(labels[0]).toBe('Filter 7/8 ▴')
  expect(labels).toContain('✓ Sessions')
  expect(labels.length).toBe(9)
})

test('W11: every status word fits a narrow pane, with the action first', () => {
  const reasons: ApiFailure['reason'][] = ['server-down', 'unsupported', 'not-initialised', 'bad-request', 'not-found', 'timeout', 'error']
  const statuses = [
    ...reasons.map((reason) => failureOutcome({ ok: false, reason, detail: 'x'.repeat(200) }).status.split('\n')[0]!),
    FEED_UNAVAILABLE,
    emptyText({ rows: [] } as never),
  ]
  for (const status of statuses) {
    for (const word of status.split(' ')) expect(word.length).toBeLessThanOrEqual(20)
  }
  expect(failureOutcome({ ok: false, reason: 'bad-request', detail: 'x'.repeat(200) }).status.split('\n')[1]).toBe('x'.repeat(60))
  expect(failureOutcome({ ok: false, reason: 'server-down' }).status.startsWith('Server down')).toBe(true)
})

test('W12: when the filter hides every row the pane says how many', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      events: {
        epoch: 'ep1',
        head: 5,
        rows: [5, 4, 3, 2, 1].map((seq) => ({ seq, record: event('index', { phase: 'finished' }) })),
        skipped: null,
      },
    }),
  )
  await openAndSettle($, w)
  await w.advance(2_000)

  expect(await shown($)).toContain('All 5 rows hidden by the filter.')
  expect(await shown($)).not.toContain('No activity yet.')
})

test('W13: a date line appears where the day changes, and is never counted', async ($, on) => {
  const w = world(on)
  w.script(
    response(),
    response({
      head: 13,
      writes: {
        rows: [write(13, { created_at: NOW }), write(12, { created_at: NOW - 60 }), write(11, { created_at: NOW - 30 * 3600 })],
        skipped: null,
      },
    }),
  )
  await openAndSettle($, w)
  await w.advance(2_000)

  const lines = (await shown($)).filter((t) => /^── \d{4}-\d{2}-\d{2} ──$/.test(t))
  expect(lines.length).toBe(1)
  const order = descendants(await drawn($))
    .filter((n) => n.type === 'Text' || (n.type === 'Box' && String(n.props?.['key'] ?? '').startsWith('row-')))
    .map((n) => (n.type === 'Text' ? textOf(n) : `row ${textOf(n.children![0]!)}`))
    .filter((t) => t.startsWith('──') || t.startsWith('row'))
  expect(order.map((t) => (t.startsWith('──') ? 'sep' : 'row'))).toEqual(['row', 'row', 'sep', 'row'])

  await openFilter($)
  await (await mountPane($)).press({ key: 'g-writes' })
  expect(await shown($)).toContain('All 3 rows hidden by the filter.')
})

test('W14: the cap on free text is on the payload, before layout', async ($, on) => {
  const w = world(on)
  w.script(response(), response({ head: 12, writes: { rows: [write(12, { body: 'z'.repeat(500) })], skipped: null } }))
  await openAndSettle($, w)
  await w.advance(2_000)

  const [view] = await rowViews($)
  expect(view!.detail).toBe(`f12 ${'z'.repeat(120)}…`)
})
