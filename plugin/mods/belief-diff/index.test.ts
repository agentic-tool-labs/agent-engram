import { expect, mock, test } from 'claude-code/testing'
import type { On } from 'claude-code'
import { ENGRAM_TOOLS } from '../shared/client'
import { installFakeEngine } from '../shared/testing'
import type { RoutingTable } from '../shared/testing'
import type { FactResponse } from '../shared/types'
import { moment, questionFor } from './index'

const FACT: FactResponse = {
  handle: 'f12',
  id: 12,
  path: '/user/preferences',
  predicate: 'preference',
  body: 'Indent Go files with tabs.',
  details: null,
  scope: 'user',
  learned_via: 'stated',
  evidence: null,
  valid_from: 1_760_000_000,
  valid_to: null,
  live: true,
  versions: 1,
}
const ARGS = { fact_id: 'f12', statement: 'Indent Go files with two spaces.', reason: 'The user changed their mind.' }
const RUNNING = { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Server: 'Running', Port: 7433 }) } }

type Script = (n: number, question: string) => Promise<string | undefined | 'reject'> | string | undefined

function deferred<T>() {
  let resolve!: (v: T) => void
  const promise = new Promise<T>((r) => (resolve = r))
  return { promise, resolve }
}

// The test environment has setTimeout but the shared lib config names no timer types.
declare const setTimeout: (fn: (value: unknown) => void, ms: number) => unknown

const settle = async () => {
  await new Promise((resolve) => setTimeout(resolve, 50))
}

/** The engine beneath the mod: an Engram that answers `fact`, a user who answers by `script`. */
function rig(on: On, fact: (id: string) => { status: number; json?: unknown }, script: Script, table: RoutingTable = {}) {
  mock.clock(on)
  const asked: string[] = []
  const revised: Record<string, unknown>[] = []
  const toasts: string[] = []
  const api = installFakeEngine(on, {
    binary: '/fake/engram',
    cli: RUNNING,
    ops: { fact: (call) => fact((call.body as { fact_id: string }).fact_id) },
    ...table,
  })
  on('tool.call', { tool: 'AskUserQuestion' }, async (_$, e) => {
    const question = e.questions[0]!.question
    asked.push(question)
    const answer = await script(asked.length - 1, question)
    if (answer === 'reject') return { deny: 'PreToolUse:AskUserQuestion hook error: not available' }
    return { result: { questions: e.questions, answers: answer === undefined ? {} : { [question]: answer } } } as never
  })
  on('tool.call', { tool: ENGRAM_TOOLS.revise }, (_$, e) => {
    revised.push(e as unknown as Record<string, unknown>)
    return { result: 'revised' } as never
  })
  on('ui.toast', (_$, e) => {
    toasts.push(e.text)
    return { value: undefined }
  })
  return { asked, revised, toasts, api }
}

const live = (extra: Partial<FactResponse> = {}) => () => ({ status: 200, json: { ...FACT, ...extra } })
const revise = ($: { tool: { call: (i: never) => Promise<unknown> } }, args: object = ARGS) =>
  $.tool.call({ tool: ENGRAM_TOOLS.revise, ...args } as never) as Promise<{ deny?: string }>
const ALL = { options: { belief_diff_scope: 'all' } }
const OFF = { options: { belief_diff_scope: 'off' } }
const PERSONAL = { options: { belief_diff_scope: 'personal' } }

test('personal scope, user-scope fact: the dialog shows both beliefs and Approve runs the revise once, untouched', async ($, on) => {
  const r = rig(on, live(), () => 'Approve')
  const result = await revise($ as never)
  expect(result.deny).toBeUndefined()
  expect(r.asked.length).toBe(1)
  const q = r.asked[0]!
  expect(q).toContain('[f12]')
  expect(q).toContain('/user/preferences · preference')
  expect(q).toContain('Indent Go files with tabs.')
  expect(q).toContain(ARGS.statement)
  expect(q).toContain(ARGS.reason)
  expect(r.revised.length).toBe(1)
  expect(Object.keys(r.revised[0]!).sort()).toEqual(['fact_id', 'reason', 'statement', 'tool', 'tool_use_id'])
  expect(r.revised[0]).toEqual(expect.objectContaining({ tool: ENGRAM_TOOLS.revise, ...ARGS }))
  expect(r.toasts).toEqual([])
})

test('Keep old: denied with the retry-proof reason, and the revise never runs', async ($, on) => {
  const r = rig(on, live(), () => 'Keep old')
  const result = await revise($ as never)
  expect(result.deny).toBe('The user kept the existing belief [f12]; do not retry this revision.')
  expect(r.revised.length).toBe(0)
  expect(r.toasts).toEqual([])
})

test('personal scope, a session note or code fact: no dialog, revise passes through', async ($, on) => {
  let scope = 'session'
  const r = rig(on, () => ({ status: 200, json: { ...FACT, scope } }), () => 'Keep old')
  for (const each of ['session', 'project', 'code']) {
    scope = each
    expect((await revise($ as never)).deny).toBeUndefined()
  }
  expect(r.asked.length).toBe(0)
  expect(r.revised.length).toBe(3)
})

test('all scope: a non-user fact gets the dialog', { options: ALL.options }, async ($, on) => {
  const r = rig(on, live({ scope: 'session' }), () => 'Keep old')
  const result = await revise($ as never)
  expect(r.asked.length).toBe(1)
  expect(result.deny).toContain('kept the existing belief')
  expect(r.revised.length).toBe(0)
})

test('off scope: nothing is asked of the API or the user', { options: OFF.options }, async ($, on) => {
  const r = rig(on, live(), () => 'Keep old')
  const result = await revise($ as never)
  expect(result.deny).toBeUndefined()
  expect(r.asked.length).toBe(0)
  expect(r.api.fetchCalls.length).toBe(0)
  expect(r.revised.length).toBe(1)
})

test('details: warned only when the live fact has them and the revise sends none', { options: PERSONAL.options }, async ($, on) => {
  const withDetails = live({ details: 'x'.repeat(137) })
  const r = rig(on, withDetails, () => 'Approve')
  await revise($ as never)
  await revise($ as never, { ...ARGS, details: '' })
  await revise($ as never, { ...ARGS, details: '   ' })
  await revise($ as never, { ...ARGS, details: 'kept elaboration' })
  const line = 'This revision drops the existing details (137 chars). Revise never carries details forward.'
  expect(r.asked.map((q) => q.includes(line))).toEqual([true, true, true, false])
})

test('details: a live fact with none is never warned about', async ($, on) => {
  let details: string | null = null
  const r = rig(on, () => ({ status: 200, json: { ...FACT, details } }), () => 'Approve')
  await revise($ as never)
  details = ''
  await revise($ as never)
  expect(r.asked.length).toBe(2)
  expect(r.asked.some((q) => q.includes('drops the existing details'))).toBe(false)
})

test('unknown or closed fact: the tool is left to report it', async ($, on) => {
  const missing = rig(on, () => ({ status: 404, json: { error: 'not_found' } }), () => 'Keep old')
  expect((await revise($ as never)).deny).toBeUndefined()
  expect(missing.asked.length).toBe(0)
  expect(missing.revised.length).toBe(1)
})

test('closed fact: passes through without a dialog', async ($, on) => {
  const r = rig(on, live({ live: false, valid_to: 1_760_000_100 }), () => 'Keep old')
  expect((await revise($ as never)).deny).toBeUndefined()
  expect(r.asked.length).toBe(0)
  expect(r.revised.length).toBe(1)
})

test('API down: passes through without a dialog', async ($, on) => {
  const r = rig(on, live(), () => 'Keep old', { cli: { 'status --json': { exitCode: 1, stdout: JSON.stringify({ Server: 'NotRunning' }) } } })
  expect((await revise($ as never)).deny).toBeUndefined()
  expect(r.asked.length).toBe(0)
  expect(r.api.fetchCalls.length).toBe(0)
  expect(r.revised.length).toBe(1)
})

test('API unsupported (older server): passes through without a dialog', async ($, on) => {
  const r = rig(on, live(), () => 'Keep old', { ops: {} })
  expect((await revise($ as never)).deny).toBeUndefined()
  expect(r.asked.length).toBe(0)
  expect(r.revised.length).toBe(1)
})

test('two revises at once: the second dialog opens only after the first is decided', async ($, on) => {
  const gate = deferred<string>()
  const r = rig(on, live(), (n) => (n === 0 ? gate.promise : 'Keep old'))
  const first = revise($ as never)
  const second = revise($ as never, { ...ARGS, statement: 'A second proposal.' })
  await settle()
  expect(r.asked.length).toBe(1)
  expect(r.revised.length).toBe(0)
  gate.resolve('Approve')
  const [a, b] = await Promise.all([first, second])
  expect(r.asked.length).toBe(2)
  expect(r.asked[1]).toContain('A second proposal.')
  expect(a.deny).toBeUndefined()
  expect(b.deny).toContain('kept the existing belief')
  expect(r.revised.length).toBe(1)
})

for (const [label, answer] of [
  ['the ask is rejected', 'reject'],
  ['no answer comes back', undefined],
  ['the answer is neither label', 'Maybe'],
  ['the answer is a label in another case', 'keep old'],
] as const) {
  test(`${label}: the revise proceeds, never denied, and one skip toast says so`, async ($, on) => {
    const r = rig(on, live(), () => answer)
    const result = await revise($ as never)
    expect(result.deny).toBeUndefined()
    expect(r.revised.length).toBe(1)
    expect(r.toasts).toEqual(['Belief diff skipped for [f12]: could not ask'])
  })
}

test('a second unanswered ask in the same session proceeds without a second toast', async ($, on) => {
  const r = rig(on, live(), () => 'reject')
  await revise($ as never)
  await revise($ as never)
  expect(r.revised.length).toBe(2)
  expect(r.toasts.length).toBe(1)
})

test('an answered ask after an unanswered one still honours Keep old', async ($, on) => {
  const r = rig(on, live(), (n) => (n === 0 ? 'reject' : 'Keep old'))
  expect((await revise($ as never)).deny).toBeUndefined()
  expect((await revise($ as never)).deny).toContain('kept the existing belief')
  expect(r.revised.length).toBe(1)
})

test('moment: local time to the second, so two revisions nine seconds apart read differently', () => {
  expect(moment(1_760_000_000)).toMatch(/^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$/)
  expect(moment(1_760_000_009)).not.toBe(moment(1_760_000_000))
  const d = new Date(1_760_000_000 * 1000)
  expect(moment(1_760_000_000).slice(-8)).toBe(
    [d.getHours(), d.getMinutes(), d.getSeconds()].map((n) => String(n).padStart(2, '0')).join(':'),
  )
})

test('question: a missing reason is named, a bracketed handle is not doubled', () => {
  const q = questionFor({ ...FACT, handle: '[f12]' }, 'New.', undefined, undefined)
  expect(q).toContain('Revise [f12]')
  expect(q).not.toContain('[[f12]]')
  expect(q).toContain('Reason: (none given)')
  expect(q.endsWith('Approve this revision?')).toBe(true)
})
