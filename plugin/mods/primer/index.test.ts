import { expect, mock, test } from 'claude-code/testing'
import type { On } from 'claude-code'
import type { PromptSubmitInput, PromptSubmitResult } from 'claude-code'
import { fakeModIo, installFakeEngine } from '../shared/testing'
import type { FetchHandler, RoutingTable } from '../shared/testing'
import { jitConfig, primePrompt } from './index'
import type { JitConfig } from './index'

const BIN = '/fake/bin/engram'
const RUNNING: Pick<RoutingTable, 'binary' | 'cli'> = {
  binary: BIN,
  cli: { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Server: 'Running', Port: 7433 }) } },
}
const DIGEST = '2 facts\n- prefers tabs'
const reply = (coverage: string, text = DIGEST): FetchHandler => () => ({ status: 200, json: { coverage, text } })
const PROMPT = 'how should I format this file?'
const composer = { kind: 'composer' } as const

const prompt = (text = PROMPT, extra: Partial<PromptSubmitInput> = {}): PromptSubmitInput =>
  ({ text, wait: false, origin: composer, ...extra }) as PromptSubmitInput

// The test environment has no DOM lib, so the host timer is reached through globalThis.
const pause = (ms: number) =>
  new Promise<void>((resolve) => (globalThis as unknown as { setTimeout(f: () => void, ms: number): void }).setTimeout(resolve, ms))

const config = (mode: JitConfig['mode'], budgetTokens = 400): JitConfig => ({ mode, budgetTokens })

function rig(recall?: FetchHandler) {
  const fake = fakeModIo({ ...RUNNING, ops: recall === undefined ? {} : { recall } })
  const seen: PromptSubmitInput[] = []
  const controller = new AbortController()
  const next = Object.assign(
    async (e: PromptSubmitInput): Promise<PromptSubmitResult> => {
      seen.push(e)
      return { text: e.text }
    },
    { signal: controller.signal },
  )
  return { ...fake, seen, next }
}

test('off: nothing is called and the prompt passes through', async () => {
  const r = rig(reply('high'))
  const e = prompt()
  await primePrompt(r.io, config('off'), e, r.next)
  expect(r.seen).toEqual([e])
  expect(r.seen[0]).toBe(e)
  expect(r.router.fetchCalls.length).toBe(0)
  expect(r.router.processCalls.length).toBe(0)
})

test('shadow + high: recall is called as shadow and the prompt is untouched', async () => {
  const r = rig(reply('high'))
  const e = prompt()
  await primePrompt(r.io, config('shadow'), e, r.next)
  await r.clock.settle()
  expect(r.seen[0]).toBe(e)
  expect(r.router.fetchCalls.length).toBe(1)
  const call = r.router.fetchCalls[0]!
  expect(call.op).toBe('recall')
  expect(call.headers['X-Engram-Mod']).toBe('primer')
  expect(call.body).toEqual({
    session_id: 'claude-session-1',
    query: PROMPT,
    budget_tokens: 400,
    mode: 'shadow',
    mod: 'primer',
  })
})

test('shadow, server answers after 5 s: the prompt is passed on at once and the answer is ignored', async () => {
  let release!: () => void
  const gate = new Promise<void>((resolve) => (release = resolve))
  const r = rig(async () => {
    await gate
    return { status: 200, json: { coverage: 'high', text: DIGEST } }
  })
  const e = prompt()
  const done = primePrompt(r.io, config('shadow'), e, r.next)
  await r.clock.settle()
  expect(r.seen.length).toBe(1)
  expect(r.seen[0]).toBe(e)
  expect(r.router.fetchCalls.length).toBe(1)

  await r.clock.advance(5_000)
  release()
  await r.clock.settle()
  await done
  expect(r.seen.length).toBe(1)
  expect(r.seen[0]!.context).toBeUndefined()
})

test('shadow, API down: the prompt is passed on at once, nothing thrown', async () => {
  const fake = fakeModIo({ binary: BIN, cli: { 'status --json': { exitCode: 1, stdout: '{"Server":"NotRunning"}' } } })
  const seen: PromptSubmitInput[] = []
  const next = Object.assign(async (e: PromptSubmitInput) => (seen.push(e), { text: e.text }), {
    signal: new AbortController().signal,
  })
  const e = prompt()
  await primePrompt(fake.io, config('shadow'), e, next)
  await fake.clock.settle()
  expect(seen[0]).toBe(e)
})

// A rejection nobody handles fails the whole file in the runner ("the file ran to its end"), which
// is the guard on the detached request's `.catch`; this test makes that rejection happen and then
// lets it surface.
test('shadow, a detached request that rejects still passes the prompt on, once', async () => {
  const r = rig(reply('high'))
  const failing = { ...r.io, sessionId: () => Promise.reject(new Error('no session')) }
  await primePrompt(failing, config('shadow'), prompt(), r.next)
  await r.clock.settle()
  await pause(5)
  expect(r.seen.length).toBe(1)
  expect(r.router.fetchCalls.length).toBe(0)
})

test('shadow: the request does not depend on the dispatch signal, which may abort once the hook returns', async () => {
  const r = rig(reply('high'))
  const controller = new AbortController()
  controller.abort()
  const next = Object.assign(r.next, { signal: controller.signal })
  await primePrompt(r.io, config('shadow'), prompt(), next)
  await r.clock.settle()
  expect(r.seen.length).toBe(1)
  expect(r.router.fetchCalls.length).toBe(1)
})

test('inject + high: exactly one block follows existing context, text unchanged', async () => {
  const r = rig(reply('high'))
  await primePrompt(r.io, config('inject', 250), prompt(PROMPT, { context: ['other plugin'] }), r.next)
  expect(r.seen.length).toBe(1)
  expect(r.seen[0]!.text).toBe(PROMPT)
  expect(r.seen[0]!.context).toEqual([
    'other plugin',
    'Memory relevant to this message (recalled automatically):\n' + DIGEST,
  ])
  expect((r.router.fetchCalls[0]!.body as { mode: string; budget_tokens: number }).mode).toBe('inject')
  expect((r.router.fetchCalls[0]!.body as { budget_tokens: number }).budget_tokens).toBe(250)
})

test('inject + high with no prior context creates the context list', async () => {
  const r = rig(reply('high'))
  await primePrompt(r.io, config('inject'), prompt(), r.next)
  expect(r.seen[0]!.context).toEqual(['Memory relevant to this message (recalled automatically):\n' + DIGEST])
})

for (const coverage of ['partial', 'none']) {
  test(`inject + ${coverage}: nothing appended`, async () => {
    const r = rig(reply(coverage))
    const e = prompt()
    await primePrompt(r.io, config('inject'), e, r.next)
    expect(r.seen[0]).toBe(e)
    expect(r.router.fetchCalls.length).toBe(1)
  })
}

test('inject + high with an empty digest appends nothing', async () => {
  const r = rig(reply('high', '  \n'))
  const e = prompt()
  await primePrompt(r.io, config('inject'), e, r.next)
  expect(r.seen[0]).toBe(e)
})

const skipped: [string, PromptSubmitInput][] = [
  ['a slash command', prompt('/lens')],
  ['a slash command that is long enough', prompt('/engram:recall something long')],
  ['a prompt under 12 characters', prompt('ok')],
  ['11 characters of text padded with spaces', prompt('   hello    ')],
  ['no origin', { text: PROMPT, wait: false } as PromptSubmitInput],
  ...['peer', 'task-notification', 'bridge', 'unclassified', 'sdk', 'scheduled-trigger', 'plugin'].map(
    (kind): [string, PromptSubmitInput] => [
      `origin ${kind}`,
      { text: PROMPT, wait: false, origin: { kind } } as unknown as PromptSubmitInput,
    ],
  ),
]
for (const [name, e] of skipped) {
  test(`skipped, no API call: ${name}`, async () => {
    const r = rig(reply('high'))
    await primePrompt(r.io, config('inject'), e, r.next)
    expect(r.seen[0]).toBe(e)
    expect(r.router.fetchCalls.length).toBe(0)
  })
}

test('a prompt of exactly 12 characters is not skipped', async () => {
  const r = rig(reply('high'))
  await primePrompt(r.io, config('shadow'), prompt('twelve chars'), r.next)
  await r.clock.settle()
  expect(r.router.fetchCalls.length).toBe(1)
})

test('a 50,000-character prompt: query truncated to 2,000, prompt unchanged', async () => {
  const r = rig(reply('high'))
  const text = 'x'.repeat(50_000)
  await primePrompt(r.io, config('inject'), prompt(text), r.next)
  expect((r.router.fetchCalls[0]!.body as { query: string }).query.length).toBe(2_000)
  expect(r.seen[0]!.text.length).toBe(50_000)
})

test('server down: the prompt passes untouched and nothing is thrown', async () => {
  const fake = fakeModIo({ binary: BIN, cli: { 'status --json': { exitCode: 1, stdout: '{"Server":"NotRunning"}' } } })
  const seen: PromptSubmitInput[] = []
  const next = Object.assign(async (e: PromptSubmitInput) => (seen.push(e), { text: e.text }), {
    signal: new AbortController().signal,
  })
  const e = prompt()
  await primePrompt(fake.io, config('inject'), e, next)
  expect(seen[0]).toBe(e)
})

test('an unsupported API is asked once, then never again', async () => {
  const r = rig()
  await primePrompt(r.io, config('inject'), prompt(), r.next)
  await primePrompt(r.io, config('inject'), prompt('a second long enough prompt'), r.next)
  expect(r.router.fetchCalls.length).toBe(1)
  expect(r.seen.every((e) => e.context === undefined)).toBe(true)
})

test('timeout: the prompt passes untouched; the late answer is never injected', async () => {
  let release!: () => void
  const gate = new Promise<void>((resolve) => (release = resolve))
  let calls = 0
  const r = rig(async () => {
    if (calls++ > 0) return { status: 200, json: { coverage: 'none', text: '' } }
    await gate
    return { status: 200, json: { coverage: 'high', text: DIGEST } }
  })
  const e = prompt()
  const done = primePrompt(r.io, config('inject'), e, r.next)
  await r.clock.settle()
  await r.clock.advance(800)
  await done
  expect(r.seen.length).toBe(1)
  expect(r.seen[0]).toBe(e)

  release()
  await r.clock.settle()
  expect(r.seen.length).toBe(1)

  const later = prompt('a different, later prompt')
  r.router.fetchCalls.length = 0
  const second = primePrompt(r.io, config('inject'), later, r.next)
  await r.clock.settle()
  await r.clock.advance(800)
  await second
  expect(r.seen[1]).toBe(later)
  expect(r.seen[1]!.context).toBeUndefined()
})

test('an already-aborted dispatch passes the prompt untouched without a request', async () => {
  const r = rig(reply('high'))
  const controller = new AbortController()
  controller.abort()
  const seen: PromptSubmitInput[] = []
  const next = Object.assign(async (e: PromptSubmitInput) => (seen.push(e), { text: e.text }), {
    signal: controller.signal,
  })
  const e = prompt()
  await primePrompt(r.io, config('inject'), e, next)
  expect(seen[0]).toBe(e)
  expect(r.router.fetchCalls.length).toBe(0)
})

test('jitConfig: defaults, unknown mode and bad budget fall back', () => {
  expect(jitConfig({})).toEqual({ mode: 'off', budgetTokens: 400 })
  expect(jitConfig({ jit_mode: 'inject', jit_budget_tokens: 120 })).toEqual({ mode: 'inject', budgetTokens: 120 })
  expect(jitConfig({ jit_mode: 'shadow' }).mode).toBe('shadow')
  expect(jitConfig({ jit_mode: 'loud', jit_budget_tokens: 0 })).toEqual({ mode: 'off', budgetTokens: 400 })
})

for (const [budget, expected] of [
  [50, 50],
  [4_000, 4_000],
  [49, 400],
  [4_001, 400],
  [120.5, 400],
  [Number.NaN, 400],
  [-1, 400],
  ['300', 400],
] as const) {
  test(`jitConfig: budget ${String(budget)} resolves to ${expected}`, () => {
    expect(jitConfig({ jit_mode: 'shadow', jit_budget_tokens: budget as never }).budgetTokens).toBe(expected)
  })
}

const answerSubmit = (on: On) => on('prompt.submit', (_$, e) => ({ text: e.text, context: e.context }))

test('manifest default is off: a plugin loaded with defaults makes no recall', async ($, on) => {
  const router = installFakeEngine(on, { ...RUNNING, ops: { recall: reply('high') } })
  mock.clock(on)
  answerSubmit(on)
  await $.prompt.submit(prompt())
  expect(router.fetchCalls.length).toBe(0)
})

test('engine: a plugin-origin prompt is skipped even in inject mode', { options: { jit_mode: 'inject' } }, async ($, on) => {
  const router = installFakeEngine(on, { ...RUNNING, ops: { recall: reply('high') } })
  mock.clock(on)
  answerSubmit(on)
  await $.prompt.submit(prompt(PROMPT, { origin: { kind: 'plugin', name: 'other' } as never }))
  expect(router.fetchCalls.length).toBe(0)
})

test('real binding: a composer prompt in inject mode reaches the API and gains one block', { options: { jit_mode: 'inject' } }, async ($, on) => {
  const router = installFakeEngine(on, { ...RUNNING, ops: { recall: reply('high') } })
  mock.clock(on)
  answerSubmit(on)
  const entered = await $.prompt.submit(prompt())
  expect(router.fetchCalls.length).toBe(1)
  expect(router.fetchCalls[0]!.url).toBe('http://127.0.0.1:7433/mod/v1/recall')
  expect((router.fetchCalls[0]!.body as { session_id: string }).session_id).toBe('claude-session-1')
  expect(entered.context).toEqual(['Memory relevant to this message (recalled automatically):\n' + DIGEST])
})

test('real binding: shadow mode calls the API and attaches nothing', { options: { jit_mode: 'shadow' } }, async ($, on) => {
  const router = installFakeEngine(on, { ...RUNNING, ops: { recall: reply('high') } })
  mock.clock(on)
  answerSubmit(on)
  const entered = await $.prompt.submit(prompt())
  for (let i = 0; i < 100 && router.fetchCalls.length === 0; i++) await pause(5)
  expect(router.fetchCalls.length).toBe(1)
  expect((router.fetchCalls[0]!.body as { mode: string }).mode).toBe('shadow')
  expect(entered.context).toBeUndefined()
})
