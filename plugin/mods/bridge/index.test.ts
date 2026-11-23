import { expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On, PromptSubmitInput } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { ProcessResult, RoutingTable } from '../shared/testing'
import { PLUGIN_VERSION } from '../shared/client'
import { GUARD_TIMEOUT_MS, PRIMER_TIMEOUT_MS } from './index'

const BIN = '/fake/bin/engram'
const SESSION = 'claude-session-1'
const CWD = '/repo'
const PRIMER = 'Engram is the durable memory store for this session.'
const SUB_PRIMER = 'You are a subagent; write down what you learn.'
const REASON = 'Write to Engram, not the memory directory.'

type Verb = 'session-start' | 'subagent-start' | 'memory-guard' | 'lookup-nudge' | 'file-touched' | 'tool-observed'
type Answer = ProcessResult | ((argv: readonly string[], stdin?: string) => ProcessResult | Promise<ProcessResult>)

const ok = (out: unknown): ProcessResult => ({ exitCode: 0, stdout: JSON.stringify(out) + '\n' })
const silent: ProcessResult = { exitCode: 0, stdout: '' }
const primerOut = (event: string, text: string) => ok({ hookSpecificOutput: { hookEventName: event, additionalContext: text } })
const denyOut = ok({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: REASON } })

// The verbs as an initialised, un-stood-down binary answers them.
const LIVE: Partial<Record<Verb, Answer>> = {
  'session-start': primerOut('SessionStart', PRIMER),
  'subagent-start': primerOut('SubagentStart', SUB_PRIMER),
  'memory-guard': silent,
  'lookup-nudge': silent,
  'file-touched': silent,
  'tool-observed': silent,
}
// Every verb stood down: exit 0, nothing printed.
const STOOD_DOWN: Partial<Record<Verb, Answer>> = Object.fromEntries(
  Object.keys(LIVE).map((v) => [v, silent]),
) as Partial<Record<Verb, Answer>>

const prompt = (text = 'what changed?', extra: Partial<PromptSubmitInput> = {}) =>
  // Not the composer's, so the capture mod beneath sends nothing and every fetch is someone else's bug.
  ({ text, wait: false, origin: { kind: 'peer' }, ...extra }) as unknown as PromptSubmitInput

const spawnInput = (prompt: string) =>
  ({
    tool_use_id: 'tu1',
    prompt,
    description: 'look around',
    subagentType: 'Explore',
    provider: { plugin: 'engine', tier: 'core' },
    parentModel: 'opus',
    background: false,
    fork: false,
  }) as never

const EDIT = { tool: 'Edit', file_path: '/repo/src/a.ts', old_string: 'a', new_string: 'b' }
const GREP = { tool: 'Grep', pattern: 'ProcessFile' }
const RECALL = { tool: 'mcp__plugin_engram_engram__engram_recall', query: 'x' }

function rig(on: On, verbs: Partial<Record<Verb, Answer>> = LIVE, table: Partial<RoutingTable> = { binary: BIN }) {
  const cli: Record<string, Answer> = { start: { exitCode: 0, stdout: '' } }
  for (const [verb, answer] of Object.entries(verbs)) cli[`hook ${verb} --source mod`] = answer
  const router = installFakeEngine(on, { ...table, cli })
  const clock = mock.clock(on)
  on('session.cwd', () => ({ value: CWD }))
  const prompts: PromptSubmitInput[] = []
  on('prompt.submit', (_$, e) => {
    prompts.push(e)
    return { text: e.text, context: e.context }
  })
  const spawns: string[] = []
  on('agent.spawn', (_$, e) => {
    spawns.push(e.prompt)
    return { model: 'opus', agentId: 'a1' }
  })
  const tools: Record<string, unknown>[] = []
  const toolControl: { deny?: string; error?: string } = {}
  on('tool.call', (_$, e) => {
    tools.push({ ...e })
    if (toolControl.deny !== undefined) return { deny: toolControl.deny }
    if (toolControl.error !== undefined) return { isError: true, result: toolControl.error, text: toolControl.error } as never
    return { result: {}, text: 'done', ref: 0 } as never
  })
  on('turn.step', async function* (_$, e) {
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null } as never
  })
  on('session.end', (_$, e) => ({ sessionId: e.sessionId }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }) as never)
  const calls = (verb: Verb) => router.processCalls.filter((c) => c.argv.slice(1).join(' ') === `hook ${verb} --source mod`)
  // Lets the verbs the bridge fired and did not await reach the fake process table.
  const settle = () => clock.advance(0)
  return { router, prompts, spawns, tools, toolControl, calls, settle }
}

const primersIn = (entered: { context?: readonly string[] }) => (entered.context ?? []).filter((c) => c === PRIMER).length

async function step($: Engine, messageCount: number, agentId?: string) {
  const s = $.turn.step({ turnId: 't', index: 0, model: 'opus', messageCount, ...(agentId === undefined ? {} : { agentId }) } as never)
  for await (const _ of s) {
    // drained
  }
}
const clear = ($: Engine) =>
  $.session.end({ reason: 'clear', sessionId: SESSION, resume: { sessionId: SESSION } } as never)

// --- the primer -------------------------------------------------------------------------------

test('the first prompt carries the session-start primer after what was attached; the verb gets a classic payload', async ($, on) => {
  const r = rig(on)
  const entered = await $.prompt.submit(prompt('hi', { context: ['earlier'] }))
  expect(entered.context).toEqual(['earlier', PRIMER])
  const [call] = r.calls('session-start')
  expect(call!.argv).toEqual([BIN, 'hook', 'session-start', '--source', 'mod'])
  expect(call!.timeoutMs).toBe(PRIMER_TIMEOUT_MS)
  expect(JSON.parse(call!.stdin!)).toEqual({ session_id: SESSION, cwd: CWD, hook_event_name: 'SessionStart' })
  // The plugin's release rides on session-start only, as hooks.json sets it for the classic hook.
  expect(call!.env).toEqual({ ENGRAM_PLUGIN_VERSION: PLUGIN_VERSION })
})

test('the primer: once per process, once after each /clear, once after each compaction, never otherwise', async ($, on) => {
  const r = rig(on)
  const seen: number[] = []
  const send = async () => seen.push(primersIn(await $.prompt.submit(prompt())))

  await send() // first prompt: primed
  await step($, 10)
  await step($, 20)
  await send() // nothing happened: not primed
  await clear($)
  await send() // after /clear: primed
  await step($, 3) // the new conversation is shorter than the old one; that is not a compaction
  await send()
  await step($, 9)
  await step($, 4) // fewer messages than the step before: a compaction
  await send() // primed
  await step($, 6)
  await send()

  expect(seen).toEqual([1, 0, 1, 0, 1, 0])
  expect(r.calls('session-start').length).toBe(3)
})

test('a subagent step carrying fewer messages never re-primes', async ($, on) => {
  rig(on)
  await $.prompt.submit(prompt())
  await step($, 40)
  await step($, 2, 'agent-1')
  await step($, 41)
  expect(primersIn(await $.prompt.submit(prompt()))).toBe(0)
})

test('a stream that fails beneath reaches the caller as it failed, after what it yielded', async ($, on) => {
  installFakeEngine(on, { binary: BIN })
  mock.clock(on)
  on('turn.step', async function* () {
    yield { kind: 'text', index: 0, text: 'partial' } as never
    throw new Error('overloaded')
  })
  const seen: unknown[] = []
  let failure: unknown
  try {
    for await (const chunk of $.turn.step({ turnId: 't', index: 0, model: 'opus', messageCount: 3 } as never)) seen.push(chunk)
  } catch (err) {
    failure = err
  }
  expect(seen).toEqual([expect.objectContaining({ kind: 'text', text: 'partial' })])
  // The kit reports the failure beneath as its own error; what matters is that one arrives.
  expect(failure).toBeDefined()
})

const slash = (name: string) => prompt('/' + name, { command: { name, args: '' } })

test('two slash commands then a plain prompt: the primer rides the first slash command and the plain prompt only', async ($, on) => {
  const r = rig(on)
  const seen: number[] = []
  for (const p of [slash('model'), slash('config'), prompt(), prompt()]) seen.push(primersIn(await $.prompt.submit(p)))
  expect(seen).toEqual([1, 0, 1, 0])
  expect(r.calls('session-start').length).toBe(2)
})

test('a plain first prompt uses the primer up: a slash command after it carries none', async ($, on) => {
  rig(on)
  const seen: number[] = []
  for (const p of [prompt(), slash('model'), prompt()]) seen.push(primersIn(await $.prompt.submit(p)))
  expect(seen).toEqual([1, 0, 0])
})

test('after /clear, a slash command carries the re-armed primer once more and the plain prompt uses it up', async ($, on) => {
  rig(on)
  await $.prompt.submit(slash('model'))
  await $.prompt.submit(prompt())
  await clear($)
  const seen: number[] = []
  for (const p of [slash('model'), slash('model'), prompt(), prompt()]) seen.push(primersIn(await $.prompt.submit(p)))
  expect(seen).toEqual([1, 0, 1, 0])
})

test('a resume re-primes the next prompt and resets the compaction baseline', async ($, on) => {
  const r = rig(on)
  await $.prompt.submit(prompt())
  await step($, 30)
  await $.session.end({ reason: 'resume', sessionId: SESSION, resume: { sessionId: SESSION } } as never)
  const seen: number[] = []
  seen.push(primersIn(await $.prompt.submit(prompt())))
  await step($, 4) // the resumed conversation is shorter than the one left; that is not a compaction
  seen.push(primersIn(await $.prompt.submit(prompt())))
  expect(seen).toEqual([1, 0])
  expect(r.calls('session-start').length).toBe(2)
})

test('a session end other than /clear does not re-prime', async ($, on) => {
  rig(on)
  await $.prompt.submit(prompt())
  await $.session.end({ reason: 'other', sessionId: SESSION, resume: { sessionId: SESSION } } as never)
  expect(primersIn(await $.prompt.submit(prompt()))).toBe(0)
})

// --- the subagent primer ----------------------------------------------------------------------

test('a spawn gets the subagent primer and a blank line before its prompt, typed by the spawn', async ($, on) => {
  const r = rig(on)
  await $.agent.spawn(spawnInput('find the bug'))
  expect(r.spawns).toEqual([SUB_PRIMER + '\n\nfind the bug'])
  const [call] = r.calls('subagent-start')
  expect(call!.timeoutMs).toBe(PRIMER_TIMEOUT_MS)
  expect(JSON.parse(call!.stdin!)).toEqual({
    session_id: SESSION,
    cwd: CWD,
    hook_event_name: 'SubagentStart',
    agent_type: 'Explore',
  })
  expect(call!.env).toBeUndefined()
})

test('a resumed spawn runs no verb and its prompt passes as given', async ($, on) => {
  const r = rig(on)
  await $.agent.spawn({ ...(spawnInput('carry on') as object), resume: { agentId: 'a0' } } as never)
  expect(r.spawns).toEqual(['carry on'])
  expect(r.calls('subagent-start').length).toBe(0)
})

// --- stood down: byte-identical ---------------------------------------------------------------

test('stood down, the prompt, the spawn prompt and the tool call pass byte-identical', async ($, on) => {
  const r = rig(on, STOOD_DOWN)
  const p = prompt('exactly this', { context: ['kept'] })
  await $.prompt.submit(p)
  expect(r.prompts[0]!.text).toBe('exactly this')
  expect(r.prompts[0]!.context).toEqual(['kept'])

  await $.agent.spawn(spawnInput('exactly that'))
  expect(r.spawns).toEqual(['exactly that'])

  for (const call of [EDIT, GREP, RECALL]) {
    await $.tool.call(call as never)
    const got = r.tools[r.tools.length - 1]!
    for (const [k, v] of Object.entries(call)) expect(got[k]).toBe(v)
  }
  expect(r.calls('session-start').length).toBe(1)
  expect(r.calls('subagent-start').length).toBe(1)
  expect(r.calls('memory-guard').length).toBe(1)
  expect(r.calls('lookup-nudge').length).toBe(1)
})

// --- the guards -------------------------------------------------------------------------------

for (const [verb, call] of [
  ['memory-guard', EDIT],
  ['lookup-nudge', GREP],
] as const) {
  test(`${verb}: a deny reaches the engine as { deny } with the verb's reason, and the tool does not run`, async ($, on) => {
    const r = rig(on, { ...LIVE, [verb]: denyOut })
    const out = await $.tool.call(call as never)
    expect(out.deny).toBe(REASON)
    expect(r.tools.length).toBe(0)
    const [sent] = r.calls(verb)
    expect(sent!.timeoutMs).toBe(GUARD_TIMEOUT_MS)
    const { tool, ...input } = call
    expect(JSON.parse(sent!.stdin!)).toEqual({
      session_id: SESSION,
      cwd: CWD,
      hook_event_name: 'PreToolUse',
      tool_name: tool,
      tool_input: input,
    })
  })

  const allow = ok({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'allow', permissionDecisionReason: 'fine' } })
  for (const [kind, answer] of [
    ['an allow', allow],
    ['silence', silent],
  ] as const) {
    test(`${verb}: ${kind} runs the call unchanged`, async ($, on) => {
      const r = rig(on, { ...LIVE, [verb]: answer })
      const out = await $.tool.call(call as never)
      expect(out.deny).toBeUndefined()
      expect(r.tools.length).toBe(1)
      for (const [k, v] of Object.entries(call)) expect(r.tools[0]![k]).toBe(v)
      expect(r.calls(verb).length).toBe(1)
    })
  }
}

test('Read matches neither guard nor file-touched', async ($, on) => {
  const r = rig(on)
  await $.tool.call({ tool: 'Read', file_path: '/repo/a' } as never)
  await r.settle()
  expect(r.router.processCalls.filter((c) => c.argv[1] === 'hook').length).toBe(0)
})

test('NotebookEdit is reported to file-touched and never guarded', async ($, on) => {
  const r = rig(on)
  await $.tool.call({ tool: 'NotebookEdit', notebook_path: '/repo/n.ipynb', new_source: 'x' } as never)
  await r.settle()
  expect(r.calls('memory-guard').length).toBe(0)
  expect(r.calls('file-touched').length).toBe(1)
})

// --- file-touched and tool-observed -----------------------------------------------------------

test('an edit that ran is reported to file-touched with a PostToolUse payload', async ($, on) => {
  const r = rig(on)
  await $.tool.call(EDIT as never)
  await r.settle()
  const [call] = r.calls('file-touched')
  const { tool, ...input } = EDIT
  expect(JSON.parse(call!.stdin!)).toEqual({
    session_id: SESSION,
    cwd: CWD,
    hook_event_name: 'PostToolUse',
    tool_name: tool,
    tool_input: input,
  })
  expect(call!.env).toBeUndefined()
})

test("a call the bridge's own guard denied triggers no file-touched", async ($, on) => {
  const r = rig(on, { ...LIVE, 'memory-guard': denyOut })
  await $.tool.call(EDIT as never)
  await r.settle()
  expect(r.calls('file-touched').length).toBe(0)
})

test('a call denied beneath the bridge triggers no file-touched', async ($, on) => {
  const r = rig(on)
  r.toolControl.deny = 'refused below'
  const out = await $.tool.call(EDIT as never)
  expect(out.deny).toBe('refused below')
  await r.settle()
  expect(r.calls('file-touched').length).toBe(0)
})

test('an edit that errored triggers no file-touched', async ($, on) => {
  const r = rig(on)
  r.toolControl.error = 'old_string not found'
  const out = await $.tool.call(EDIT as never)
  expect(out.isError).toBe(true)
  await r.settle()
  expect(r.calls('file-touched').length).toBe(0)
})

test("a subagent's calls carry its agent_id to file-touched and tool-observed, and not to the guard", async ($, on) => {
  const r = rig(on)
  await $.tool.call({ ...EDIT, agentId: 'agent-7' } as never)
  await $.tool.call({ ...RECALL, agentId: 'agent-7' } as never)
  await r.settle()
  expect(JSON.parse(r.calls('file-touched')[0]!.stdin!).agent_id).toBe('agent-7')
  expect(JSON.parse(r.calls('tool-observed')[0]!.stdin!).agent_id).toBe('agent-7')
  expect('agent_id' in JSON.parse(r.calls('memory-guard')[0]!.stdin!)).toBe(false)
})

test('an Engram tool call is reported to tool-observed', async ($, on) => {
  const r = rig(on)
  await $.tool.call(RECALL as never)
  await r.settle()
  const [call] = r.calls('tool-observed')
  expect(JSON.parse(call!.stdin!)).toEqual({
    session_id: SESSION,
    cwd: CWD,
    hook_event_name: 'PostToolUse',
    tool_name: RECALL.tool,
    tool_input: { query: 'x' },
  })
})

for (const [verb, call] of [
  ['file-touched', EDIT],
  ['tool-observed', RECALL],
] as const) {
  test(`${verb} never delays the tool result: the call returns while the verb is still running`, async ($, on) => {
    let release: () => void = () => {}
    const held = new Promise<ProcessResult>((resolve) => {
      release = () => resolve(silent)
    })
    const r = rig(on, { ...LIVE, [verb]: () => held })
    try {
      const out = await $.tool.call(call as never)
      expect(out.text).toBe('done')
      // The verb is still held, so it was started and has not finished.
      await r.settle()
      expect(r.calls(verb).length).toBe(1)
    } finally {
      release()
    }
  })
}

// --- failures pass through --------------------------------------------------------------------

const FAILURES: [string, Partial<RoutingTable>, Answer | undefined][] = [
  ['no binary resolves', {}, undefined],
  [
    'a timeout',
    { binary: BIN },
    () => {
      throw new Error('killed after timeout')
    },
  ],
  // With answers on stdout, so only the exit code says not to use them.
  [
    'exit 1',
    { binary: BIN },
    {
      exitCode: 1,
      stdout: JSON.stringify({
        hookSpecificOutput: { additionalContext: PRIMER, permissionDecision: 'deny', permissionDecisionReason: REASON },
      }),
      stderr: 'usage: engram ...',
    },
  ],
  ['stdout that is not JSON', { binary: BIN }, { exitCode: 0, stdout: 'usage: engram [--home <path>]' }],
]

for (const [name, table, answer] of FAILURES) {
  test(`${name}: the prompt, the spawn and the tool calls pass through and nothing throws`, async ($, on) => {
    const verbs = Object.fromEntries(Object.keys(LIVE).map((v) => [v, answer ?? silent])) as Partial<Record<Verb, Answer>>
    const r = rig(on, verbs, table)
    const entered = await $.prompt.submit(prompt('as typed', { context: ['kept'] }))
    expect(entered.text).toBe('as typed')
    expect(entered.context).toEqual(['kept'])
    await $.agent.spawn(spawnInput('as given'))
    expect(r.spawns).toEqual(['as given'])
    for (const call of [EDIT, GREP, RECALL]) {
      const out = await $.tool.call(call as never)
      expect(out.deny).toBeUndefined()
      expect(out.text).toBe('done')
    }
    expect(r.tools.length).toBe(3)
    // The verb was asked wherever a binary resolved, so the pass-through is the failure's and not a skipped call.
    if (table.binary !== undefined) expect(r.calls('memory-guard').length).toBe(1)
  })
}

// --- server start and what the bridge never calls ---------------------------------------------

test('session start runs `engram start` without holding the chain', async ($, on) => {
  let release: () => void = () => {}
  const held = new Promise<ProcessResult>((resolve) => {
    release = () => resolve({ exitCode: 0, stdout: '' })
  })
  const router = installFakeEngine(on, { binary: BIN, cli: { start: () => held } })
  mock.clock(on)
  on('session.start', (_$, e) => ({ cwd: e.cwd }) as never)
  try {
    await $.session.start({ cwd: CWD, surface: 'terminal', isInteractive: true } as never)
    expect(router.processCalls.some((c) => c.argv.slice(1).join(' ') === 'start')).toBe(true)
  } finally {
    release()
  }
})

test('the bridge never calls the mod API', async ($, on) => {
  const r = rig(on)
  await $.session.start({ cwd: CWD, surface: null, isInteractive: false } as never)
  await $.prompt.submit(prompt())
  await $.agent.spawn(spawnInput('go'))
  for (const call of [EDIT, GREP, RECALL]) await $.tool.call(call as never)
  await step($, 5)
  await clear($)
  await r.settle()
  // Nothing in this rig asks another mod to fetch (the prompt is a peer's, no op is routed), so any
  // request at all would be the bridge's.
  expect(r.router.fetchCalls.map((c) => c.url)).toEqual([])
})
