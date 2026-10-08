import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { FetchHandler } from '../shared/testing'

type Row = { role: 'user' | 'assistant'; text: string; toolUses: [] }
const row = (role: Row['role'], text: string): Row => ({ role, text, toolUses: [] })

type Rig = {
  rows: Row[]
  completions: { prompt: string; system?: string }[]
  fetches: ReturnType<typeof installFakeEngine>['fetchCalls']
  toasts: string[]
  opened: { id: string; focus?: true }[]
  closed: string[]
  filled: string[]
  registered: string[]
  clock: ReturnType<typeof mock.clock>
  reply: { text: string; isAnswered: boolean }
  selection: { text: string } | undefined
}

const COMPLETE_USAGE = { input_tokens: 1, output_tokens: 1, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 }

/** Everything beneath the plugin that the digest touches, answered from the rig. */
function rigUp(on: On, ops: Record<string, FetchHandler> = {}): Rig {
  const clock = mock.clock(on)
  const engine = installFakeEngine(on, {
    binary: '/fake/bin/engram',
    cli: { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Server: 'Running', Port: 7433 }) } },
    ops,
    sessionId: 'claude-session-1',
  })
  const rig: Rig = {
    rows: [],
    completions: [],
    fetches: engine.fetchCalls,
    toasts: [],
    opened: [],
    closed: [],
    filled: [],
    registered: [],
    clock,
    reply: { text: '[]', isAnswered: true },
    selection: undefined,
  }
  on('session.messages', () => ({ value: rig.rows }))
  on('model.complete', (_$, e) => {
    rig.completions.push({ prompt: e.prompt, system: e.system })
    return {
      value: rig.reply.isAnswered
        ? { isAnswered: true, text: rig.reply.text, usage: COMPLETE_USAGE }
        : { isAnswered: false, reason: 'empty-reply', usage: COMPLETE_USAGE },
    }
  })
  on('ui.toast', (_$, e) => {
    rig.toasts.push(e.text)
    return { value: undefined }
  })
  on('ui.open', (_$, e) => {
    rig.opened.push({ id: e.id, focus: e.focus })
    return { value: { isPlaced: true } }
  })
  on('ui.close', (_$, e) => {
    rig.closed.push(e.id)
    return { value: undefined }
  })
  on('ui.selection', () => ({ value: rig.selection }))
  on('prompt.fill', (_$, e) => {
    rig.filled.push(e.text)
    return { isFilled: true }
  })
  on('command.register', (_$, e) => {
    rig.registered.push(e.name)
    return { value: { command: e.name } }
  })
  on('session.start', (_$, e) => ({ cwd: e.cwd }) as never)
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', () => ({ result: {}, text: '', ref: 0 }))
  return rig
}

const turn = (
  $: Engine,
  extra: { agentId?: string; isAborted?: boolean; reason?: 'answer' | 'aborted' | 'error' } = {},
) =>
  $.turn.complete({
    answer: 'done',
    durationMs: 1,
    isAborted: extra.isAborted ?? false,
    turnId: 't',
    reason: extra.reason ?? 'answer',
    ...(extra.agentId === undefined ? {} : { agentId: extra.agentId }),
  } as never)

const edit = ($: Engine) => $.tool.call({ tool: 'Edit', file_path: '/x', old_string: 'a', new_string: 'b' } as never)

const recallReply = (coverage: string): FetchHandler => () => ({ status: 200, json: { coverage, fact_count: 0, notes: [], gaps: null, text: '', facts: [] } })

test('N = 0: turns complete, no model call, ever', async ($, on) => {
  const rig = rigUp(on)
  rig.rows.push(row('user', 'we use tabs'))
  for (let i = 0; i < 10; i++) await turn($)
  await rig.clock.advance(1000)
  expect(rig.completions.length).toBe(0)
})

const ON = (n: number) => ({ options: { digest_every_n_turns: n } })
const CANDIDATES = JSON.stringify(['Jim prefers tabs in Go files', 'The build uses Native AOT'])

/** A rig whose model proposes `CANDIDATES` and whose recall says `coverage` for every query. */
function proposing(on: On, coverage = 'none', ops: Record<string, FetchHandler> = {}): Rig {
  const rig = rigUp(on, { recall: recallReply(coverage), ...ops })
  rig.rows.push(row('user', 'we use tabs in Go'), row('assistant', 'noted'))
  rig.reply.text = CANDIDATES
  return rig
}

test('positive control: N = 1 digests after one completed turn', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

test('N = 5: four plain turns do nothing, the fifth digests', ON(5), async ($, on) => {
  const rig = proposing(on)
  for (let i = 0; i < 4; i++) await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

test('a turn that edited files digests at the second turn, not the first', ON(5), async ($, on) => {
  const rig = proposing(on)
  await edit($)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
  await edit($)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

test('an edit in an earlier turn does not count toward a later turn', ON(5), async ($, on) => {
  const rig = proposing(on)
  await edit($)
  await turn($)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
})

test('a non-editing tool does not shorten the period', ON(5), async ($, on) => {
  const rig = proposing(on)
  await $.tool.call({ tool: 'Read', file_path: '/x' } as never)
  await turn($)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
})

test('subagent, aborted and errored turns are not counted', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($, { agentId: 'sub-1' })
  await turn($, { isAborted: true, reason: 'aborted' })
  await turn($, { reason: 'error' })
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

test('the turn completes before any digest work starts', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  expect(rig.completions.length).toBe(0)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

test('the deferred work still reaches the engine after the hook returned: messages, model, API, pane, toast', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  expect(rig.opened).toEqual([])
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
  expect(rig.fetches.some((f) => f.op === 'recall')).toBe(true)
  expect(rig.opened.length).toBe(1)
  expect(rig.toasts.length).toBe(1)
})

test('the model sees the conversation text, not tool output, and the system prompt asks for JSON', ON(1), async ($, on) => {
  const rig = proposing(on)
  rig.rows.push(row('user', ''))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions[0]!.prompt).toBe('User: we use tabs in Go\n\nAssistant: noted')
  expect(rig.completions[0]!.system?.includes('JSON array')).toBe(true)
})

test('candidates open the pane unfocused and toast once; nothing is written', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.opened).toEqual([{ id: 'engram-digest', focus: undefined }])
  expect(rig.toasts).toEqual(['2 memory candidates — /digest-review'])
  expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(0)
})

test('prose, an unanswered model and an empty array show nothing', ON(1), async ($, on) => {
  const rig = proposing(on)
  rig.reply.text = 'You should remember that tabs matter.'
  await turn($)
  await rig.clock.advance(0)
  rig.reply.isAnswered = false
  rig.rows.push(row('user', 'next'))
  await turn($)
  await rig.clock.advance(0)
  rig.reply = { text: '[]', isAnswered: true }
  rig.rows.push(row('user', 'more'))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(3)
  expect(rig.opened).toEqual([])
  expect(rig.toasts).toEqual([])
})

test('seven candidates: the first five are checked against recall and shown', ON(1), async ($, on) => {
  const rig = proposing(on)
  rig.reply.text = JSON.stringify(['1', '2', '3', '4', '5', '6', '7'])
  await turn($)
  await rig.clock.advance(0)
  expect(rig.fetches.filter((f) => f.op === 'recall').map((f) => (f.body as { query: string }).query)).toEqual(['1', '2', '3', '4', '5'])
  expect(rig.toasts).toEqual(['5 memory candidates — /digest-review'])
})

test('recall request: session id, the statement, a 100-token budget, mod digest', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  const first = rig.fetches.find((f) => f.op === 'recall')!
  expect(first.body).toEqual({ session_id: 'claude-session-1', query: 'Jim prefers tabs in Go files', budget_tokens: 100, mod: 'digest' })
  expect(first.headers['X-Engram-Mod']).toBe('digest')
})

test('a candidate recall already covers (high) is dropped; partial and none stay', ON(1), async ($, on) => {
  const rig = proposing(on, 'none', {
    recall: (call) => {
      const query = (call.body as { query: string }).query
      return recallReply(query.startsWith('Jim') ? 'high' : 'partial')(call)
    },
  })
  await turn($)
  await rig.clock.advance(0)
  expect(rig.toasts).toEqual(['1 memory candidates — /digest-review'])
})

test('every candidate known: nothing shown', ON(1), async ($, on) => {
  const rig = proposing(on, 'high')
  await turn($)
  await rig.clock.advance(0)
  expect(rig.opened).toEqual([])
  expect(rig.toasts).toEqual([])
})

test('API down: candidates are kept, dedupe is best effort', ON(1), async ($, on) => {
  const rig = proposing(on, 'none', { recall: () => ({ status: 500, json: { error: 'boom' } }) })
  await turn($)
  await rig.clock.advance(0)
  expect(rig.toasts).toEqual(['2 memory candidates — /digest-review'])
})

test('the next digest reads only what was said since the last one', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  rig.rows.push(row('user', 'second topic'))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions[1]!.prompt).toBe('User: second topic')
})

test('after a compaction the count cannot skip rows written since', ON(1), async ($, on) => {
  const rig = proposing(on)
  rig.rows.length = 0
  for (let i = 0; i < 100; i++) rig.rows.push(row('user', `before ${i}`))
  await turn($)
  await rig.clock.advance(0)
  rig.rows.length = 0
  rig.rows.push(row('assistant', 'summary of earlier work'))
  for (let i = 0; i < 109; i++) rig.rows.push(row('user', `after ${i}`))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions[1]!.prompt.includes('after 0')).toBe(true)
  expect(rig.completions[1]!.prompt.includes('Assistant: summary of earlier work')).toBe(true)
})

test('a digest with nothing new says nothing and calls no model', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
})

for (const surface of ['terminal', 'desktop'] as const) {
  const pane = ($: Engine) => $.ui.mount({ plugin: 'engram', surface, component: 'Pane', requestId: 'engram-digest', props: {} } as never)

  test(`[${surface}] Save writes exactly the ticked statements with the fixed evidence, then toasts handles`, ON(1), async ($, on) => {
    let next = 88
    const rig = proposing(on, 'none', { remember: () => ({ status: 200, json: { handle: `f${next++}`, id: 1, created: true } }) })
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    await ui.press({ key: 'tick0' })
    await ui.press({ key: 'tick1' })
    expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(0)
    await ui.press({ key: 'save' })
    const written = rig.fetches.filter((f) => f.op === 'remember')
    expect(written.map((f) => f.body)).toEqual([
      { session_id: 'claude-session-1', statement: 'Jim prefers tabs in Go files', evidence: 'proposed by auto-digest, approved by the user', mod: 'digest' },
      { session_id: 'claude-session-1', statement: 'The build uses Native AOT', evidence: 'proposed by auto-digest, approved by the user', mod: 'digest' },
    ])
    expect(rig.toasts.at(-1)).toBe('Saved [f88], [f89]')
    expect(rig.closed).toEqual(['engram-digest'])
  })

  test(`[${surface}] only the ticked rows are saved; a toggled-off row is not`, ON(1), async ($, on) => {
    const rig = proposing(on, 'none', { remember: () => ({ status: 200, json: { handle: 'f1', id: 1, created: true } }) })
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    await ui.press({ key: 'tick0' })
    await ui.press({ key: 'tick1' })
    await ui.press({ key: 'tick0' })
    await ui.press({ key: 'save' })
    expect(rig.fetches.filter((f) => f.op === 'remember').map((f) => (f.body as { statement: string }).statement)).toEqual(['The build uses Native AOT'])
  })

  test(`[${surface}] rows start unticked and Save with none ticked writes nothing`, ON(1), async ($, on) => {
    const rig = proposing(on)
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    expect((await ui.find({ key: 'tick0' }))?.text).toContain('[ ]')
    await ui.press({ key: 'save' })
    expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(0)
  })

  test(`[${surface}] Skip discards the batch without writing`, ON(1), async ($, on) => {
    const rig = proposing(on)
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    await ui.press({ key: 'skip' })
    expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(0)
    expect(rig.closed).toEqual(['engram-digest'])
    const again = await $.command.run({ command: 'digest-review', args: '' } as never)
    expect(again.text).toBe('No memory candidates to review.')
  })

  test(`[${surface}] API down at Save: failures are listed per statement, nothing is retried`, ON(1), async ($, on) => {
    const rig = proposing(on, 'none', { remember: () => ({ status: 503, json: { error: 'not_initialised' } }) })
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    await ui.press({ key: 'tick0' })
    await ui.press({ key: 'tick1' })
    await ui.press({ key: 'save' })
    await rig.clock.advance(10_000)
    expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(2)
    expect(rig.toasts.at(-1)).toBe('Not saved: "Jim prefers tabs in Go files" (not-initialised)\nNot saved: "The build uses Native AOT" (not-initialised)')
    expect(rig.closed).toEqual([])
    expect((await ui.find({ key: 'tick0' }))?.text).toContain('[ ]')
    expect((await ui.find({ key: 'tick1' }))?.text).toContain('[ ]')
  })

  test(`[${surface}] a partial failure reports the saved handle and the failed statement together`, ON(1), async ($, on) => {
    let calls = 0
    const rig = proposing(on, 'none', {
      remember: () => (calls++ === 0 ? { status: 200, json: { handle: 'f7', id: 7, created: true } } : { status: 400, json: { error: 'bad' } }),
    })
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    await ui.press({ key: 'tick0' })
    await ui.press({ key: 'tick1' })
    await ui.press({ key: 'save' })
    expect(rig.toasts.at(-1)).toBe('Saved [f7]\nNot saved: "The build uses Native AOT" (bad-request)')
    expect(rig.closed).toEqual([])
    expect(await ui.find({ key: 'tick1' })).toBeUndefined()
    expect((await ui.find({ key: 'tick0' }))?.text).toContain('[ ]')
  })
}

test('/digest-review opens the pane focused when a batch is waiting', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  rig.opened.length = 0
  await $.command.run({ command: 'digest-review', args: '' } as never)
  expect(rig.opened).toEqual([{ id: 'engram-digest', focus: true }])
})

test('/digest-review with no batch opens nothing', ON(1), async ($, on) => {
  const rig = rigUp(on)
  const out = await $.command.run({ command: 'digest-review', args: '' } as never)
  expect(out.text).toBe('No memory candidates to review.')
  expect(rig.opened).toEqual([])
})

test('/remember-selection fills the prompt and calls no API', async ($, on) => {
  const rig = rigUp(on)
  rig.selection = { text: 'tabs, not spaces' }
  await $.command.run({ command: 'remember-selection', args: '' } as never)
  expect(rig.filled).toEqual(['Remember this: "tabs, not spaces"'])
  expect(rig.fetches.length).toBe(0)
  expect(rig.toasts).toEqual([])
})

test('/remember-selection with no selection toasts and fills nothing', async ($, on) => {
  const rig = rigUp(on)
  await $.command.run({ command: 'remember-selection', args: '' } as never)
  expect(rig.filled).toEqual([])
  expect(rig.toasts).toEqual(['Select text in fullscreen mode first.'])
})

test('/remember-selection with a whitespace-only selection is no selection', async ($, on) => {
  const rig = rigUp(on)
  rig.selection = { text: '  \n ' }
  await $.command.run({ command: 'remember-selection', args: '' } as never)
  expect(rig.filled).toEqual([])
  expect(rig.toasts).toEqual(['Select text in fullscreen mode first.'])
})

test('session start registers both commands', async ($, on) => {
  const rig = rigUp(on)
  await $.session.start({ cwd: '/w', surface: 'terminal', isInteractive: true } as never)
  expect(rig.registered.includes('digest-review')).toBe(true)
  expect(rig.registered.includes('remember-selection')).toBe(true)
})
