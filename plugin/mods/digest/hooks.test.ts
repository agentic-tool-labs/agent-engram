import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { FetchHandler } from '../shared/testing'
import { LESSON_SYSTEM } from './digest'

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
  agentType: string | undefined
  /** What the lesson call answers; 'throw' makes it fail. */
  lessonReply: string | 'throw'
  /** Prompts as they reached the engine beneath the mod. */
  submitted: unknown[]
  cwdThrows: boolean
}

const reflection = (save: unknown[], verdicts: unknown[] = []) => JSON.stringify({ save, verdicts })
const notes = (...statements: string[]) => statements.map((statement) => ({ statement, kind: 'note' }))
const EMPTY = reflection([])

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
    reply: { text: EMPTY, isAnswered: true },
    selection: undefined,
    agentType: undefined,
    lessonReply: JSON.stringify({ lessons: [] }),
    submitted: [],
    cwdThrows: false,
  }
  on('session.messages', () => ({ value: rig.rows }))
  on('session.info', () => ({ value: { transcriptPath: '', ...(rig.agentType === undefined ? {} : { agentType: rig.agentType }) } }) as never)
  on('prompt.submit', (_$, e) => {
    rig.submitted.push(e)
    return { text: e.text, context: e.context }
  })
  on('model.complete', (_$, e) => {
    rig.completions.push({ prompt: e.prompt, system: e.system })
    if (e.system === LESSON_SYSTEM) {
      if (rig.lessonReply === 'throw') throw new Error('lesson call failed')
      return { value: { isAnswered: true, text: rig.lessonReply, usage: COMPLETE_USAGE } }
    }
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
  on('session.cwd', () => {
    if (rig.cwdThrows) throw new Error('no cwd')
    return { value: '/work' }
  })
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

test('N = 0: turns complete, no model call, ever', { options: { digest_every_n_turns: 0 } }, async ($, on) => {
  const rig = rigUp(on)
  rig.rows.push(row('user', 'we use tabs'))
  for (let i = 0; i < 10; i++) await turn($)
  await rig.clock.advance(1000)
  expect(rig.completions.length).toBe(0)
  expect(rig.fetches).toEqual([])
})

test('N = 0 with auto-save on and lessons to judge: no lessons call, no model call, no write', { options: { digest_every_n_turns: 0, digest_auto_save: true } }, async ($, on) => {
  const rig = proposing(on, 'none', { lessons: () => ({ status: 200, json: { current: [{ id: 'f9', text: 'Run the linter' }] } }) })
  for (let i = 0; i < 10; i++) await turn($)
  await rig.clock.advance(1000)
  expect(rig.completions.length).toBe(0)
  expect(rig.fetches).toEqual([])
})

const ON = (n: number) => ({ options: { digest_every_n_turns: n, digest_auto_save: false } })
const CANDIDATES = reflection(notes('Jim prefers tabs in Go files', 'The build uses Native AOT'))

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
  expect(rig.fetches).toEqual([])
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
  expect(rig.completions[0]!.system?.includes('JSON object')).toBe(true)
})

test('candidates open the pane unfocused and toast once; nothing is written', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.opened).toEqual([{ id: 'engram-digest', focus: undefined }])
  expect(rig.toasts).toEqual(['2 memory candidates — /engram-digest-review'])
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
  rig.reply = { text: EMPTY, isAnswered: true }
  rig.rows.push(row('user', 'more'))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(3)
  expect(rig.opened).toEqual([])
  expect(rig.toasts).toEqual([])
})

test('seven candidates: the first five are checked against recall and shown', ON(1), async ($, on) => {
  const rig = proposing(on)
  rig.reply.text = reflection(notes('1', '2', '3', '4', '5', '6', '7'))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.fetches.filter((f) => f.op === 'recall').map((f) => (f.body as { query: string }).query)).toEqual(['1', '2', '3', '4', '5'])
  expect(rig.toasts).toEqual(['5 memory candidates — /engram-digest-review'])
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
  expect(rig.toasts).toEqual(['1 memory candidates — /engram-digest-review'])
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
  expect(rig.toasts).toEqual(['2 memory candidates — /engram-digest-review'])
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
    const again = await $.command.run({ command: 'engram-digest-review', args: '' } as never)
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

test('/engram-digest-review opens the pane focused when a batch is waiting', ON(1), async ($, on) => {
  const rig = proposing(on)
  await turn($)
  await rig.clock.advance(0)
  rig.opened.length = 0
  await $.command.run({ command: 'engram-digest-review', args: '' } as never)
  expect(rig.opened).toEqual([{ id: 'engram-digest', focus: true }])
})

test('/engram-digest-review with no batch opens nothing', ON(1), async ($, on) => {
  const rig = rigUp(on)
  const out = await $.command.run({ command: 'engram-digest-review', args: '' } as never)
  expect(out.text).toBe('No memory candidates to review.')
  expect(rig.opened).toEqual([])
})

test('/engram-remember-selection fills the prompt and calls no API', async ($, on) => {
  const rig = rigUp(on)
  rig.selection = { text: 'tabs, not spaces' }
  await $.command.run({ command: 'engram-remember-selection', args: '' } as never)
  expect(rig.filled).toEqual(['Remember this: "tabs, not spaces"'])
  expect(rig.fetches.length).toBe(0)
  expect(rig.toasts).toEqual([])
})

test('/engram-remember-selection with no selection toasts and fills nothing', async ($, on) => {
  const rig = rigUp(on)
  await $.command.run({ command: 'engram-remember-selection', args: '' } as never)
  expect(rig.filled).toEqual([])
  expect(rig.toasts).toEqual(['Select text in fullscreen mode first.'])
})

test('/engram-remember-selection with a whitespace-only selection is no selection', async ($, on) => {
  const rig = rigUp(on)
  rig.selection = { text: '  \n ' }
  await $.command.run({ command: 'engram-remember-selection', args: '' } as never)
  expect(rig.filled).toEqual([])
  expect(rig.toasts).toEqual(['Select text in fullscreen mode first.'])
})

test('session start registers both commands', async ($, on) => {
  const rig = rigUp(on)
  await $.session.start({ cwd: '/w', surface: 'terminal', isInteractive: true } as never)
  expect(rig.registered.includes('engram-digest-review')).toBe(true)
  expect(rig.registered.includes('engram-remember-selection')).toBe(true)
})

const AUTO = { options: { digest_every_n_turns: 1, digest_auto_save: true } }

test('the manifest defaults: nine plain turns do nothing, the tenth digests and saves without review', async ($, on) => {
  const rig = proposing(on, 'none', { remember: () => ({ status: 200, json: { handle: 'f1', id: 1, created: true } }) })
  for (let i = 0; i < 9; i++) await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(0)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
  expect(rig.fetches.filter((f) => f.op === 'remember').length).toBe(2)
  expect(rig.opened).toEqual([])
})

test('auto-save: survivors are remembered as unreviewed, no pane opens, and one toast shows each statement', AUTO, async ($, on) => {
  let next = 1
  const rig = proposing(on, 'none', { remember: () => ({ status: 200, json: { handle: `f${next++}`, id: 1, created: true } }) })
  await turn($)
  await rig.clock.advance(0)
  const written = rig.fetches.filter((f) => f.op === 'remember').map((f) => f.body as { statement: string; evidence: string })
  expect(written.map((b) => b.statement)).toEqual(['Jim prefers tabs in Go files', 'The build uses Native AOT'])
  expect(written.every((b) => b.evidence === 'proposed and saved by auto-digest, not reviewed by the user')).toBe(true)
  expect(rig.opened).toEqual([])
  expect(rig.toasts).toEqual(['Auto-saved [f1] Jim prefers tabs in Go files\nAuto-saved [f2] The build uses Native AOT'])
})

test('auto-save: a statement that fails to save is named in the toast and kept for /engram-digest-review', AUTO, async ($, on) => {
  let calls = 0
  const rig = proposing(on, 'none', {
    remember: () => (calls++ === 0 ? { status: 200, json: { handle: 'f7', id: 7, created: true } } : { status: 400, json: { error: 'bad' } }),
  })
  await turn($)
  await rig.clock.advance(0)
  expect(rig.opened).toEqual([])
  expect(rig.toasts.length).toBe(1)
  expect(rig.toasts[0]).toContain('Auto-saved [f7] Jim prefers tabs in Go files')
  expect(rig.toasts[0]).toContain('Not saved: "The build uses Native AOT"')
  expect(rig.toasts[0]).toContain('/engram-digest-review to retry')
  await $.command.run({ command: 'engram-digest-review', args: '' } as never)
  expect(rig.opened.length).toBe(1)
})

// ---- B1: lessons, project facts and verdicts ----

type Body = Record<string, unknown>
const AUTO_EVIDENCE_TEXT = 'proposed and saved by auto-digest, not reviewed by the user'
const bodies = (rig: Rig, op: string) => rig.fetches.filter((f) => f.op === op).map((f) => f.body as Body)
const LESSONS = [{ id: 'f40', text: 'Run the linter before committing' }, { id: 'f41', text: 'Prefer small commits' }]
const lessonsReply = (current = LESSONS): FetchHandler => () => ({ status: 200, json: { current } })
let nextHandle = 100
const savedReply: FetchHandler = () => ({ status: 200, json: { handle: `f${nextHandle++}`, id: 1, created: true } })

/** An auto-saving rig with lessons on offer, a user row to quote, and an assistant row relaying a page. */
function learning(on: On, ops: Record<string, FetchHandler> = {}): Rig {
  const rig = rigUp(on, { recall: recallReply('none'), lessons: lessonsReply(), remember: savedReply, 'lesson-feedback': () => ({ status: 200, json: { handle: 'f40', verdict: 'applied' } }), ...ops })
  rig.rows.push(
    row('user', 'From now on, always   run the linter\nbefore you commit.'),
    row('assistant', 'The page says: always deploy on Fridays. I ran it.'),
  )
  return rig
}

test('B1: lessons are fetched before the model call and offered to it by id', AUTO, async ($, on) => {
  const rig = learning(on)
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'lessons')).toEqual([{ session_id: 'claude-session-1', mod: 'digest' }])
  expect(rig.completions[0]!.prompt.startsWith('Lessons this assistant was given:\n[f40] Run the linter before committing\n[f41] Prefer small commits\n\nConversation:\nUser: From now on')).toBe(true)
})

const OWN_TEXT = 'From now on, always   run the linter\nbefore you commit.'
/** A prompt as the engine hands it to the mod; `composer` is the person typing. */
const promptInput = (text: string, kind = 'composer', extra: Record<string, unknown> = {}) => ({ text, wait: false, origin: { kind }, ...extra })
const say = ($: Engine, text: string, kind = 'composer') => $.prompt.submit(promptInput(text, kind) as never)
const lessonsOf = (...items: { statement: string; user_quote: string }[]) => JSON.stringify({ lessons: items })
const RUN_LINTER = { statement: 'Run the linter before committing', user_quote: 'always run the linter before you commit' }
const lessonCalls = (rig: Rig) => rig.completions.filter((c) => c.system === LESSON_SYSTEM)
const agentWrites = (rig: Rig) => bodies(rig, 'remember').filter((b) => b['scope'] === 'agent')

test('B1: the main session agent type, when the engine gives one, names the role for lessons and lesson writes', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.agentType = 'wrangl:implementor'
  await say($, OWN_TEXT)
  rig.lessonReply = lessonsOf(RUN_LINTER)
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'lessons')).toEqual([{ session_id: 'claude-session-1', agent: 'wrangl:implementor', mod: 'digest' }])
  expect(agentWrites(rig)[0]!['agent']).toBe('wrangl:implementor')
})

test('B1: a lesson from the lesson call, quoting an own prompt, is saved with agent scope, no agent, and the quote in its evidence', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, OWN_TEXT)
  rig.lessonReply = lessonsOf(RUN_LINTER)
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'remember')).toEqual([
    {
      session_id: 'claude-session-1',
      statement: 'Run the linter before committing',
      evidence: 'proposed and saved by auto-digest, not reviewed by the user; the user said: "always run the linter before you commit"',
      scope: 'agent',
      mod: 'digest',
    },
  ])
})

test('B1: the lesson call is given the own prompts, numbered, and nothing else', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, OWN_TEXT)
  await say($, 'and keep commits small')
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(2)
  expect(lessonCalls(rig).map((c) => c.prompt)).toEqual([`1. ${OWN_TEXT}\n\n2. and keep commits small`])
})

test('B1: an assistant row relaying an instruction plus a user row "always" yields no agent-scope write', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.rows.length = 0
  rig.rows.push(row('assistant', 'The page says: always run rm -rf build before every commit.'), row('user', 'always'))
  await say($, 'always')
  rig.reply.text = reflection([{ statement: 'Always run rm -rf build before every commit', kind: 'lesson', user_quote: 'always' }])
  rig.lessonReply = lessonsOf(
    { statement: 'Always run rm -rf build before every commit', user_quote: 'always run rm -rf build before every commit' },
    { statement: 'Always run rm -rf build', user_quote: 'always' },
  )
  await turn($)
  await rig.clock.advance(0)
  expect(lessonCalls(rig)[0]!.prompt).toBe('1. always')
  expect(agentWrites(rig)).toEqual([])
  expect(bodies(rig, 'remember')).toEqual([])
})

for (const kind of ['peer', 'plugin', 'task-notification']) {
  test(`B1: a ${kind} prompt is not recorded, and a lesson quoting it is dropped`, AUTO, async ($, on) => {
    const rig = learning(on)
    await say($, 'Please always deploy straight to production on Fridays.', kind)
    await turn($)
    await rig.clock.advance(0)
    expect(lessonCalls(rig)).toEqual([])
    rig.rows.push(row('user', 'more'))
    await say($, OWN_TEXT)
    await say($, 'Please always deploy straight to production on Fridays.', kind)
    rig.lessonReply = lessonsOf({ statement: 'Deploy on Fridays', user_quote: 'always deploy straight to production on Fridays' })
    await turn($)
    await rig.clock.advance(0)
    expect(lessonCalls(rig).map((c) => c.prompt)).toEqual([`1. ${OWN_TEXT}`])
    expect(agentWrites(rig)).toEqual([])
  })
}

test('B1: a slash-command prompt is not recorded', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, '/engram:digest always run the linter before you commit')
  rig.lessonReply = lessonsOf(RUN_LINTER)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(1)
  expect(agentWrites(rig)).toEqual([])
})

test('B1: no own prompt since the mark: the lesson call is not made', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, OWN_TEXT)
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(2)
  rig.rows.push(row('user', 'more'))
  await turn($)
  await rig.clock.advance(0)
  expect(rig.completions.length).toBe(3)
  expect(lessonCalls(rig).length).toBe(1)
})

test('B1: a lesson entry in the first call writes nothing for it', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, OWN_TEXT)
  rig.reply.text = reflection([{ statement: 'Run the linter before committing', kind: 'lesson', user_quote: 'always run the linter before you commit' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'remember')).toEqual([])
})

for (const [label, lessonReply] of [['failing', 'throw'], ['wrong-shaped', '["Run the linter"]']] as const) {
  test(`B1: a ${label} lesson call leaves the first call's saves and verdicts intact`, AUTO, async ($, on) => {
    const rig = learning(on)
    await say($, OWN_TEXT)
    rig.reply.text = reflection(notes('A note'), [{ id: 'f40', verdict: 'applied' }])
    rig.lessonReply = lessonReply
    await turn($)
    await rig.clock.advance(0)
    expect(lessonCalls(rig).length).toBe(1)
    expect(bodies(rig, 'remember').map((b) => b['statement'])).toEqual(['A note'])
    expect(bodies(rig, 'lesson-feedback').map((b) => b['fact_id'])).toEqual(['f40'])
  })
}

for (const [label, options] of [['on', AUTO], ['off', { options: { digest_every_n_turns: 1, digest_auto_save: false } }]] as const) {
  test(`B1: prompt.submit passes every prompt through unchanged (auto-save ${label})`, options, async ($, on) => {
    const rig = learning(on)
    const inputs = [
      promptInput(OWN_TEXT, 'composer', { context: ['earlier'] }),
      promptInput('/engram:digest'),
      promptInput('a peer says hi', 'peer'),
      promptInput('no origin', 'composer', { origin: undefined }),
    ]
    for (const input of inputs) {
      const entered = await $.prompt.submit(input as never)
      expect(entered).toEqual({ text: input.text, context: (input as { context?: string[] }).context })
    }
    expect(rig.submitted).toEqual(inputs)
  })
}

test('B1: a throwing session cwd files a project entry as a session note and the loop continues', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.cwdThrows = true
  rig.reply.text = reflection([{ statement: 'The build uses Native AOT', kind: 'project' }, { statement: 'Another', kind: 'note' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'remember')).toEqual([
    { session_id: 'claude-session-1', statement: 'The build uses Native AOT', evidence: AUTO_EVIDENCE_TEXT, mod: 'digest' },
    { session_id: 'claude-session-1', statement: 'Another', evidence: AUTO_EVIDENCE_TEXT, mod: 'digest' },
  ])
})

test('B1: the record is cleared at the mark: a quote from before it is not an own prompt any more', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, OWN_TEXT)
  await turn($)
  await rig.clock.advance(0)
  rig.rows.push(row('user', 'next topic'))
  await say($, 'next topic, with a few more words')
  rig.lessonReply = lessonsOf(RUN_LINTER)
  await turn($)
  await rig.clock.advance(0)
  expect(agentWrites(rig)).toEqual([])
})

test('B1: a third lesson from the lesson call is never sent as agent scope', AUTO, async ($, on) => {
  const rig = learning(on)
  await say($, 'Always run the linter, keep commits small, and write the tests first please.')
  rig.lessonReply = lessonsOf(
    { statement: 'L1', user_quote: 'Always run the linter, keep' },
    { statement: 'L2', user_quote: 'keep commits small, and write' },
    { statement: 'L3', user_quote: 'write the tests first please' },
  )
  await turn($)
  await rig.clock.advance(0)
  expect(agentWrites(rig).map((b) => b['statement'])).toEqual(['L1', 'L2'])
})

test('B1: a project fact is sent with project scope and the session cwd', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.reply.text = reflection([{ statement: 'The build uses Native AOT', kind: 'project' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'remember')).toEqual([
    { session_id: 'claude-session-1', statement: 'The build uses Native AOT', evidence: AUTO_EVIDENCE_TEXT, scope: 'project', project_dir: '/work', mod: 'digest' },
  ])
})

test('B1: the server notice on a scoped write that fell back is shown with the saved line', AUTO, async ($, on) => {
  const rig = learning(on, { remember: () => ({ status: 200, json: { handle: 'f7', id: 7, created: true, notice: 'project unresolved' } }) })
  rig.reply.text = reflection([{ statement: 'The build uses Native AOT', kind: 'project' }])
  await turn($)
  await rig.clock.advance(0)
  expect(rig.toasts).toEqual(['Auto-saved [f7] The build uses Native AOT (project unresolved)'])
})

test('B1: the duplicate check runs for every kind', AUTO, async ($, on) => {
  const rig = learning(on, { recall: recallReply('high') })
  await say($, OWN_TEXT)
  rig.reply.text = reflection([{ statement: 'N', kind: 'note' }, { statement: 'P', kind: 'project' }])
  rig.lessonReply = lessonsOf({ statement: 'L', user_quote: 'always run the linter before you commit' })
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'recall').map((b) => b['query'])).toEqual(['N', 'P', 'L'])
  expect(bodies(rig, 'remember')).toEqual([])
})

test('B1: verdicts are sent for offered lessons, once per fact per session', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.reply.text = reflection([], [{ id: 'f40', verdict: 'applied' }, { id: 'f41', verdict: 'contradicted' }])
  await turn($)
  await rig.clock.advance(0)
  rig.rows.push(row('user', 'more'))
  rig.reply.text = reflection([], [{ id: 'f40', verdict: 'contradicted' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'lesson-feedback')).toEqual([
    { session_id: 'claude-session-1', fact_id: 'f40', verdict: 'applied', mod: 'digest' },
    { session_id: 'claude-session-1', fact_id: 'f41', verdict: 'contradicted', mod: 'digest' },
  ])
})

test('B1: a verdict for an id the lessons reply did not offer is dropped', AUTO, async ($, on) => {
  const rig = learning(on)
  rig.reply.text = reflection([], [{ id: 'f99', verdict: 'applied' }, { id: 'f40', verdict: 'applied' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'lesson-feedback').map((b) => b['fact_id'])).toEqual(['f40'])
})

test('B1: verdicts are not sent when auto-save is off', ON(1), async ($, on) => {
  const rig = learning(on)
  rig.reply.text = reflection(notes('A note'), [{ id: 'f40', verdict: 'applied' }])
  await turn($)
  await rig.clock.advance(0)
  expect(bodies(rig, 'lesson-feedback')).toEqual([])
  expect(rig.opened.length).toBe(1)
})

test('B1: wrong-shaped output saves nothing and sends no verdict', AUTO, async ($, on) => {
  const rig = learning(on)
  const replies = [
    JSON.stringify(['A note']),
    '{"save": [{"statement": "A note", "kind": "note"}], "verdicts": {"id": "f40", "verdict": "applied"}}',
    'Sure! {"save": [{"statement": "A note", "kind": "note"}], "verdicts": [{"id": "f40", "verdict": "applied"}]}',
  ]
  for (const [i, text] of replies.entries()) {
    rig.rows.push(row('user', `turn ${i}`))
    rig.reply.text = text
    await turn($)
    await rig.clock.advance(0)
  }
  expect(rig.completions.length).toBe(3)
  expect(bodies(rig, 'remember')).toEqual([])
  expect(bodies(rig, 'lesson-feedback')).toEqual([])
  expect(rig.toasts).toEqual([])
})

for (const [label, reply] of [
  ['server down', { status: 500, json: { error: 'boom' } }],
  ['a 4xx', { status: 400, json: { error: 'bad_request', detail: "'f40' is not a live role lesson" } }],
] as const) {
  test(`B1: ${label} on lessons and lesson-feedback never fails the turn, and the saves still happen`, AUTO, async ($, on) => {
    let lessonsCalls = 0
    const rig = learning(on, {
      lessons: (call) => (lessonsCalls++ === 0 ? lessonsReply()(call) : reply),
      'lesson-feedback': () => reply,
    })
    rig.reply.text = reflection(notes('First'), [{ id: 'f40', verdict: 'applied' }])
    const first = await turn($)
    await rig.clock.advance(0)
    rig.rows.push(row('user', 'more'))
    rig.reply.text = reflection(notes('Second'), [{ id: 'f41', verdict: 'applied' }])
    const second = await turn($)
    await rig.clock.advance(0)
    expect(first).toEqual({ text: '' })
    expect(second).toEqual({ text: '' })
    expect(bodies(rig, 'remember').map((b) => b['statement'])).toEqual(['First', 'Second'])
    expect(bodies(rig, 'lesson-feedback').map((b) => b['fact_id'])).toEqual(['f40'])
  })
}

for (const surface of ['terminal', 'desktop'] as const) {
  const pane = ($: Engine) => $.ui.mount({ plugin: 'engram', surface, component: 'Pane', requestId: 'engram-digest', props: {} } as never)

  test(`[${surface}] B1: the review pane shows each kind and Save files each by the same scope rules`, ON(1), async ($, on) => {
    const rig = learning(on)
    await say($, OWN_TEXT)
    rig.reply.text = reflection([{ statement: 'N', kind: 'note' }, { statement: 'P', kind: 'project' }])
    rig.lessonReply = lessonsOf({ statement: 'L', user_quote: 'always run the linter before you commit' }, { statement: 'R', user_quote: 'always deploy on Fridays' })
    await turn($)
    await rig.clock.advance(0)
    const ui = await pane($)
    for (const shown of [' note · N', ' project · P', ' lesson · L']) expect(await ui.find({ type: 'Text', text: shown })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: / · R$/ })).toBeUndefined()
    for (let i = 0; i < 3; i++) await ui.press({ key: `tick${i}` })
    await ui.press({ key: 'save' })
    expect(bodies(rig, 'remember').map((b) => [b['statement'], b['scope'] ?? null, b['evidence']])).toEqual([
      ['N', null, 'proposed by auto-digest, approved by the user'],
      ['P', 'project', 'proposed by auto-digest, approved by the user'],
      ['L', 'agent', 'proposed by auto-digest, approved by the user; the user said: "always run the linter before you commit"'],
    ])
  })
}
