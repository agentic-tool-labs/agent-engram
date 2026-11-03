import { expect, mock, test } from 'claude-code/testing'
import type { On, PromptSubmitInput } from 'claude-code'
import { installFakeEngine } from '../shared/testing'
import type { FetchHandler, RoutingTable } from '../shared/testing'

const RUNNING: Pick<RoutingTable, 'binary' | 'cli'> = {
  binary: '/fake/bin/engram',
  cli: { 'status --json': { exitCode: 0, stdout: JSON.stringify({ Server: 'Running', Port: 7433 }) } },
}
const CONTEXT = 'Engram captured [f7] I moved to Seattle.'
const TEXT = 'I moved to Seattle.'
const stored: FetchHandler = () => ({ status: 200, json: { context: CONTEXT } })
const nothing: FetchHandler = () => ({ status: 200, json: { context: null } })
const prompt = (extra: Partial<PromptSubmitInput> = {}) =>
  ({ text: TEXT, wait: false, origin: { kind: 'composer' }, ...extra }) as PromptSubmitInput

function rig(on: On, ops: RoutingTable['ops']) {
  const router = installFakeEngine(on, { ...RUNNING, ops })
  mock.clock(on)
  on('prompt.submit', (_$, e) => ({ text: e.text, context: e.context }))
  return { router, captures: () => router.fetchCalls.filter((c) => c.op === 'capture') }
}

test('a stored statement reaches the model as context, keeping what was already attached', async ($, on) => {
  const r = rig(on, { capture: stored })
  const entered = await $.prompt.submit(prompt({ context: ['earlier'] }))
  expect(entered.context).toEqual(['earlier', CONTEXT])
  expect(r.captures().length).toBe(1)
  expect(r.captures()[0]!.body).toMatchObject({ mod: 'capture', session_id: 'claude-session-1', text: TEXT })
})

test('nothing stored: the prompt passes with no context', async ($, on) => {
  rig(on, { capture: nothing })
  const entered = await $.prompt.submit(prompt())
  expect(entered.text).toBe(TEXT)
  expect(entered.context).toBeUndefined()
})

for (const [name, input] of [
  ['a peer message', prompt({ origin: { kind: 'peer' } as never })],
  ['a task notification', prompt({ origin: { kind: 'task-notification' } as never })],
  ['claude -p', prompt({ origin: { kind: 'sdk' } as never })],
  ['no origin', prompt({ origin: undefined })],
  ['a slash command', prompt({ text: '/engram:status' })],
] as const) {
  test(`${name} is never sent for capture`, async ($, on) => {
    const r = rig(on, { capture: stored })
    const entered = await $.prompt.submit(input)
    expect(r.captures().length).toBe(0)
    expect(entered.context).toBeUndefined()
  })
}

test('a server that fails costs the capture, never the prompt', async ($, on) => {
  rig(on, { capture: () => ({ status: 500, json: { error: 'internal' } }) })
  const entered = await $.prompt.submit(prompt())
  expect(entered.text).toBe(TEXT)
  expect(entered.context).toBeUndefined()
})

// The toast asks for captures since one second before it started, so it has to start before the
// capture does, or a capture slower than that second never gets its toast.
test('the toast starts its window before a slow capture', async ($, on) => {
  const clock = mock.clock(on, { now: 5_000_000 })
  const router = installFakeEngine(on, {
    ...RUNNING,
    ops: {
      capture: async () => {
        await clock.advance(1500)
        return { status: 200, json: { context: null } }
      },
      captures: () => ({ status: 200, json: { captures: [] } }),
    },
  })
  on('prompt.submit', (_$, e) => ({ text: e.text }))
  await $.prompt.submit(prompt())
  const lookup = router.fetchCalls.find((c) => c.op === 'captures')
  expect((lookup!.body as { since: number }).since).toBe(4_999)
})
