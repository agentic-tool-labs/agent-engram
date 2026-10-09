import { expect, mock, test } from 'claude-code/testing'
import { installFakeEngine } from './testing'

// Every mod loads through one hooks module, and mods that take the same event each pass the
// shared matcher so the engine accepts them. This loads them all together and checks that none
// of them was dropped to make that work: the band's poller and the digest's commands both start.
test('every mod that takes session.start runs when the plugin loads', async (eng, on) => {
  const router = installFakeEngine(on, {
    binary: '/fake/bin/engram',
    cli: { 'embed --status --json': { exitCode: 0, stdout: '{}' } },
  })
  const clock = mock.clock(on)
  const registered: string[] = []
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('command.register', (_$, e) => {
    registered.push(e.name)
    return { value: { command: e.name } }
  })

  await eng.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never)
  await clock.advance(0)

  expect(registered).toContain('engram-digest-review')
  expect(registered).toContain('engram-remember-selection')
  expect(registered).toContain('engram-tail')
  expect(router.processCalls.some((c) => c.argv.includes('--status'))).toBe(true)
})
