import { expect, mock, test } from 'claude-code/testing'
import { installFakeEngine } from './testing'

const ASK_CLOCK = {
  name: 'ask-clock',
  register: ((on: import('claude-code').On) => {
    on('command.run', { command: 'tick' }, async ($) => {
      const now = await $.clock.now()
      await $.clock.sleep(1000)
      return { text: String(now) }
    })
  }) as never,
}
const TICK = { command: 'tick', args: '', origin: { kind: 'user' }, presentation: { isFullscreen: false, columns: 80 } } as never

test('installFakeEngine leaves the clock to mock.clock, so a test may use both', { plugins: [ASK_CLOCK] }, async ($, on) => {
  installFakeEngine(on)
  const clock = mock.clock(on, { now: 42_000 })
  const run = $.command.run(TICK)
  await clock.advance(1000)
  expect(await run).toEqual({ text: '42000' })
})
