import { expect, test } from 'claude-code/testing'
import { installFakeEngine } from './testing'

const ASK_CLOCK = {
  name: 'ask-clock',
  register: ((on: never) => {
    ;(on as import('claude-code').On)('command.run', { command: 'tick' }, async ($) => {
      const now = await $.clock.now()
      const asleep = $.clock.sleep(1000).then(() => 'slept', () => 'threw')
      await $.clock.now()
      const slept = await Promise.race([asleep, Promise.resolve('pending')])
      return { text: `${now} ${slept}` }
    })
  }) as never,
}
const TICK = { command: 'tick', args: '', origin: { kind: 'user' }, presentation: { isFullscreen: false, columns: 80 } } as never

test('installFakeEngine answers clock.now from the table and leaves clock.sleep pending', { plugins: [ASK_CLOCK] }, async ($, on) => {
  installFakeEngine(on, { now: 42_000 })
  expect(await $.command.run(TICK)).toEqual({ text: '42000 pending' })
})

test('installFakeEngine defaults clock.now to 1,000,000', { plugins: [ASK_CLOCK] }, async ($, on) => {
  installFakeEngine(on)
  expect(await $.command.run(TICK)).toEqual({ text: '1000000 pending' })
})
