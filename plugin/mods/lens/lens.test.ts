import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'

import { ENGRAM_TOOLS } from '../shared/client'
import { installFakeEngine } from '../shared/testing'
import type { RoutingTable } from '../shared/testing'
import { MAX_RECALLS } from './model'

const DIGEST = [
  'RECALL "kestrel" · 2 facts · 40/500 tokens · coverage: partial · overlap lane did not run (token index not built yet)',
  '[f3] Kestrel binds loopback only. (project · 0d · v2)',
  '[f4] Kestrel port comes from settings. (project · 0d)',
  'gaps: only partial matches for "kestrel" — verify before relying on this',
  '→ engram_remember what you discover',
].join('\n')

const RUNNING: RoutingTable = {
  binary: '/fake/bin/engram',
  cli: { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Home: '/h', Server: 'Running', Port: 7433 }) } },
}

const HISTORY = {
  path: '/knowledge/testing/kestrel',
  predicate: 'states',
  versions: [
    { handle: 'f2', body: 'Kestrel bound every interface.', valid_from: 1_700_000_000, valid_to: 1_700_000_100, learned_via: 'stated', closed_reason: 'superseded by a newer statement' },
    { handle: 'f3', body: 'Kestrel binds loopback only.', valid_from: 1_700_000_100, valid_to: null, learned_via: 'stated', closed_reason: null },
  ],
}

/** A recall answered by the engine beneath the plugin, as the model would receive it. */
function answerRecall(on: On, text: string, extra: object = {}) {
  mock.clock(on, { now: 1_700_000_000_000 })
  on('tool.call', () => ({ result: {}, text, ...extra }) as never)
}

const recallCall = (id: string, extra: object = {}) =>
  ({ tool: ENGRAM_TOOLS.recall, tool_use_id: id, query: 'kestrel', ...extra }) as never

const digestFor = (query: string) => DIGEST.replace('"kestrel"', `"${query}"`)

/** A recall whose digest carries the query the call was made with. */
function answerEachRecall(on: On) {
  mock.clock(on, { now: 1_700_000_000_000 })
  on('tool.call', (_$, e) => ({ result: {}, text: digestFor((e as { query?: string }).query ?? 'kestrel') }) as never)
}

const mountPane = ($: Engine, surface: 'terminal' | 'desktop' = 'terminal') =>
  $.ui.mount({ plugin: 'engram', surface, component: 'Pane', props: { title: 'Memory Lens', isFocused: false } as never, requestId: 'engram-lens' })

/** Everything the pane shows as text, one string per Text element. */
async function shown($: Engine): Promise<string[]> {
  const pane = await mountPane($)

  return (await pane.findAll({ type: 'Text' })).map((t) => t.text)
}

test('a recall is returned unchanged and listed with its notes, facts and gaps', async ($, on) => {
  answerRecall(on, DIGEST)
  const ran = await $.tool.call(recallCall('t1'))

  expect((ran as { text?: string }).text).toBe(DIGEST)
  const texts = await shown($)
  expect(texts.some((t) => /^"kestrel" · partial · 2 facts · \d{2}:\d{2}:\d{2}$/.test(t))).toBe(true)
  expect(texts).toContain('overlap lane did not run (token index not built yet)')
  expect(texts.some((t) => t.startsWith('f3 Kestrel binds loopback only.') && t.includes('(project · 0d · v2)'))).toBe(true)
  expect(texts.some((t) => t.startsWith('f4 Kestrel port comes from settings.'))).toBe(true)
  expect(texts).toContain('gaps: only partial matches for "kestrel" — verify before relying on this')
})

test('a header without an availability note shows none, and never infers one', async ($, on) => {
  answerRecall(on, ['RECALL "q" · 1 facts · 9/500 tokens · coverage: high', '[f1] a (project · 0d)'].join('\n'))
  await $.tool.call(recallCall('t1'))

  const texts = await shown($)
  expect(texts.some((t) => t.includes('did not run') || t.includes('Unavailable') || t.includes('Off'))).toBe(false)
})

test('tools other than recall are not observed', async ($, on) => {
  on('tool.call', () => ({ result: {}, text: DIGEST }) as never)
  await $.tool.call({ tool: ENGRAM_TOOLS.remember, tool_use_id: 't1' } as never)
  await $.tool.call({ tool: ENGRAM_TOOLS.expand, tool_use_id: 't2' } as never)
  await $.tool.call({ tool: 'Bash', tool_use_id: 't3', command: 'true' } as never)

  expect(await shown($)).toEqual(['No Engram recalls yet.'])
})

test('a subagent recall carries its agent label', async ($, on) => {
  answerRecall(on, DIGEST)
  await $.tool.call(recallCall('t1', { agentId: 'agent-abcdef123456' }))

  expect((await shown($)).some((t) => t.includes(' · subagent agent-ab · '))).toBe(true)
})

test('an errored recall is an entry with the error text and no facts', async ($, on) => {
  mock.clock(on, { now: 1_700_000_000_000 })
  on('tool.call', () => ({ result: {}, isError: true, text: 'engram is down' }) as never)
  await $.tool.call(recallCall('t1'))

  const texts = await shown($)
  expect(texts.some((t) => /^"kestrel" · error · \d{2}:\d{2}:\d{2}$/.test(t))).toBe(true)
  expect(texts).toContain('engram is down')
  expect(texts.some((t) => /^f\d+ /.test(t))).toBe(false)
})

test('text without a RECALL header is shown raw, not dropped', async ($, on) => {
  answerRecall(on, 'something else entirely')
  await $.tool.call(recallCall('t1'))

  const texts = await shown($)
  expect(texts.some((t) => t.includes('unparsed'))).toBe(true)
  expect(texts).toContain('something else entirely')
})

test('the pane keeps the newest twenty of twenty-five recalls', async ($, on) => {
  answerEachRecall(on)
  for (let i = 0; i < MAX_RECALLS + 5; i++) await $.tool.call(recallCall(`t${i}`, { query: `q${i}` }))

  const headers = (await shown($)).filter((t) => /^"q\d+" · /.test(t))
  expect(headers.length).toBe(MAX_RECALLS)
  expect(headers[0]!.startsWith(`"q${MAX_RECALLS + 4}" · `)).toBe(true)
  expect(headers[MAX_RECALLS - 1]!.startsWith('"q5" · ')).toBe(true)
})

test('turn.start passes through', async ($, on) => {
  on('turn.start', (_$, e) => ({ turnId: (e as { turnId: string }).turnId }))
  const result = await $.turn.start({ text: 'hi', turnId: 'turn-9' } as never)

  expect((result as { turnId: string }).turnId).toBe('turn-9')
})

for (const surface of ['terminal', 'desktop'] as const) {
  test(`${surface}: the pane mounts and lists the recall`, async ($, on) => {
    answerRecall(on, DIGEST)
    await $.tool.call(recallCall('t1'))

    const pane = await mountPane($, surface)

    expect(await pane.find({ text: /overlap lane did not run \(token index not built yet\)/ })).toBeDefined()
    expect(await pane.find({ text: /gaps: only partial matches/ })).toBeDefined()
  })
}

const hasText = async (pane: Awaited<ReturnType<typeof mountPane>>, text: string) =>
  (await pane.findAll({ type: 'Text' })).some((t) => t.text === text)

async function openedLens($: Engine, on: On, table: RoutingTable) {
  const router = installFakeEngine(on, table)
  answerRecall(on, DIGEST)
  await $.tool.call(recallCall('t1'))

  return { pane: await mountPane($), router }
}

test('only a fact with versions above one has a History button', async ($, on) => {
  const { pane } = await openedLens($, on, RUNNING)

  const buttons = await pane.findAll({ type: 'Button' })
  expect(buttons.length).toBe(1)
  expect(buttons[0]!.key).toBe('h-t1-f3')
})

test('History fetches once, lists versions oldest to newest and steps with Prev/Next', async ($, on) => {
  let calls = 0
  const { pane } = await openedLens($, on, { ...RUNNING, ops: { history: () => (calls++, { status: 200, json: HISTORY }) } })

  await pane.press({ key: 'h-t1-f3' })
  expect(calls).toBe(1)
  expect(await pane.find({ text: /^v1 · .* · closed .*: superseded by a newer statement$/ })).toBeDefined()
  expect(await pane.find({ text: /^v2 · \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/ })).toBeDefined()
  expect(await hasText(pane, 'Kestrel binds loopback only.')).toBe(true)
  expect(await hasText(pane, 'Kestrel bound every interface.')).toBe(false)

  await pane.press({ key: 'prev-t1-f3' })
  expect(await hasText(pane, 'Kestrel bound every interface.')).toBe(true)
  expect(await hasText(pane, 'Kestrel binds loopback only.')).toBe(false)
  await pane.press({ key: 'prev-t1-f3' })
  expect(await hasText(pane, 'Kestrel bound every interface.')).toBe(true)
  await pane.press({ key: 'next-t1-f3' })
  expect(await hasText(pane, 'Kestrel binds loopback only.')).toBe(true)
  expect(calls).toBe(1)
})

test('History against an older server says so and does not retry', async ($, on) => {
  const table = { ...RUNNING }
  const { pane, router } = await openedLens($, on, table)

  await pane.press({ key: 'h-t1-f3' })

  expect(await pane.find({ text: /history unavailable/ })).toBeDefined()
  expect(await pane.find({ type: 'Button', key: 'h-t1-f3' })).toBeUndefined()
  expect(router.fetchCalls.length).toBe(1)
})

test('History for a handle the server does not know says unavailable', async ($, on) => {
  const { pane } = await openedLens($, on, { ...RUNNING, ops: { history: () => ({ status: 404, json: { error: 'not_found' } }) } })

  await pane.press({ key: 'h-t1-f3' })

  expect(await pane.find({ text: /history unavailable/ })).toBeDefined()
})

test('/lens opens the pane, then closes it', async ($, on) => {
  const seen: string[] = []
  on('ui.open', (_$, e) => (seen.push(`open:${(e as { id: string }).id}`), { value: { isPlaced: true } }) as never)
  on('ui.close', (_$, e) => (seen.push(`close:${(e as { id: string }).id}`), { value: undefined }) as never)

  await $.command.run({ command: 'lens' } as never)
  await $.command.run({ command: 'lens' } as never)

  expect(seen).toEqual(['open:engram-lens', 'close:engram-lens'])
})

test('lens_auto_open opens the pane once, on the first recall only', { options: { lens_auto_open: true } }, async ($, on) => {
  const opened: string[] = []
  on('ui.open', (_$, e) => (opened.push((e as { id: string }).id), { value: { isPlaced: true } }) as never)
  answerRecall(on, DIGEST)

  await $.tool.call(recallCall('t1'))
  await $.tool.call(recallCall('t2'))

  expect(opened).toEqual(['engram-lens'])
})

test('by default (lens_auto_open off) a recall never opens the pane', async ($, on) => {
  const opened: string[] = []
  on('ui.open', (_$, e) => (opened.push((e as { id: string }).id), { value: { isPlaced: true } }) as never)
  answerRecall(on, DIGEST)

  await $.tool.call(recallCall('t1'))

  expect(opened).toEqual([])
})
