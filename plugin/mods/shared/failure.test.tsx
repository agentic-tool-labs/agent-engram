import { expect, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'
import type { On } from 'claude-code'
import { PANE as DIGEST_PANE } from '../digest/digest'
import { ENGRAM_TOOLS } from './client'

// The whole composed plugin runs with every engine noun the mods use failing, and every event any
// mod registers is driven through it. A mod's hook must never throw or call `next` twice, so each
// event still completes with exactly what the core answered, the core runs once, and the mods
// beneath a failing one still run. The last point is what an unguarded hook would break: a throw
// before `next` stops the hooks beneath it. (A lone hook's missing guard is invisible here, the
// engine falling back to the core either way; PluginSourceTests holds that.)

const MODES = ['throws', 'rejects'] as const
type Mode = (typeof MODES)[number]

type Rig = {
  attempts: Record<string, number>
  registered: string[]
  core: Record<string, number>
}

const CORE_TEXT = 'core answered'

function inject(on: On, mode: Mode, classicCore: () => unknown = () => ({})): Rig {
  const rig: Rig = { attempts: {}, registered: [], core: {} }
  const fail = (noun: string) => {
    rig.attempts[noun] = (rig.attempts[noun] ?? 0) + 1
    const error = new Error(`injected ${noun}`)
    if (mode === 'throws') throw error
    return Promise.reject(error)
  }
  const failing = [
    'process.run',
    'http.fetch',
    'session.id',
    'ui.toast',
    'ui.open',
    'ui.close',
    'ui.status',
    'ui.selection',
    'audio.play',
    'prompt.fill',
    'clock.now',
    'clock.after',
    'clock.sleep',
    'model.complete',
    'session.messages',
    'session.cwd',
  ] as const
  for (const noun of failing) on(noun as never, (() => fail(noun)) as never)
  on('command.register', ((_$: unknown, e: { name: string }) => {
    rig.registered.push(e.name)
    return fail('command.register')
  }) as never)
  on('state.get', (() => fail('state.get')) as never)
  on('state.set', (() => fail('state.set')) as never)

  const answer = (event: string, result: unknown) =>
    on(event as never, (() => {
      rig.core[event] = (rig.core[event] ?? 0) + 1
      return result
    }) as never)
  answer('session.start', { cwd: '/work' })
  answer('turn.start', { turnId: 't1' })
  answer('turn.complete', { text: CORE_TEXT })
  answer('prompt.submit', { text: CORE_TEXT })
  answer('tool.call', { result: {}, text: CORE_TEXT })
  on('classic.UserPromptSubmit' as never, (() => {
    rig.core['classic.UserPromptSubmit'] = (rig.core['classic.UserPromptSubmit'] ?? 0) + 1
    return classicCore()
  }) as never)
  answer('command.run', { text: CORE_TEXT })
  on('ui.render', (($: any, e: any) => {
    rig.core['ui.render'] = (rig.core['ui.render'] ?? 0) + 1
    const { Text } = $.ui.resolve(e)
    return <Text>{CORE_TEXT}</Text>
  }) as never)
  return rig
}

const run = ($: Engine, command: string) =>
  $.command.run({ command, args: '', origin: { kind: 'user' }, presentation: { isFullscreen: false, columns: 80 } } as never)

const EVENTS: { name: string; drive: ($: Engine) => Promise<unknown>; expected: unknown; core: string }[] = [
  {
    name: 'session.start',
    drive: ($) => $.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never),
    expected: { cwd: '/work' },
    core: 'session.start',
  },
  { name: 'turn.start', drive: ($) => $.turn.start({ text: 'hi', turnId: 't1' } as never), expected: { turnId: 't1' }, core: 'turn.start' },
  {
    name: 'turn.complete',
    drive: ($) =>
      $.turn.complete({ answer: 'x', durationMs: 1, isAborted: false, turnId: 't1', reason: 'answer' } as never),
    expected: { text: CORE_TEXT },
    core: 'turn.complete',
  },
  {
    name: 'prompt.submit',
    drive: ($) => $.prompt.submit({ text: 'where is the thing defined in the code', wait: false } as never),
    expected: { text: CORE_TEXT },
    core: 'prompt.submit',
  },
  {
    name: 'tool.call Edit',
    drive: ($) => $.tool.call({ tool: 'Edit', tool_use_id: 'u1', file_path: '/w/a.ts', old_string: 'a', new_string: 'b' } as never),
    expected: { result: {}, text: CORE_TEXT },
    core: 'tool.call',
  },
  {
    name: 'tool.call recall',
    drive: ($) => $.tool.call({ tool: ENGRAM_TOOLS.recall, tool_use_id: 'u2', query: 'kestrel' } as never),
    expected: { result: {}, text: CORE_TEXT },
    core: 'tool.call',
  },
  {
    name: 'tool.call revise',
    drive: ($) => $.tool.call({ tool: ENGRAM_TOOLS.revise, tool_use_id: 'u3', fact_id: 'f1', statement: 'new' } as never),
    expected: { result: {}, text: CORE_TEXT },
    core: 'tool.call',
  },
  { name: 'classic.UserPromptSubmit', drive: ($) => $.classic.UserPromptSubmit({ prompt: 'remember that I like tea' }), expected: {}, core: 'classic.UserPromptSubmit' },
]

// A command a mod answers itself may answer from its own state without touching a failing noun, or
// answer nothing when it cannot; what must hold is that the run completes.
const COMMANDS = ['lens', 'digest-review', 'remember-selection', 'undo-capture', 'invariant', 'why']

for (const mode of MODES) {
  for (const event of EVENTS) {
    test(`${mode}: ${event.name} still completes with the core's answer, the core runs once`, async (eng, on) => {
      const rig = inject(on, mode)
      expect(await event.drive(eng)).toEqual(event.expected)
      expect(rig.core[event.core]).toBe(1)
    })
  }

  for (const command of COMMANDS) {
    test(`${mode}: command ${command} completes and the core runs at most once`, async (eng, on) => {
      const rig = inject(on, mode)
      await expect(run(eng, command)).resolves.toBeDefined()
      expect(rig.core['command.run'] ?? 0).toBeLessThanOrEqual(1)
    })
  }

  test(`${mode}: an edit reaches both mods that take it, whichever one fails first`, async (eng, on) => {
    const rig = inject(on, mode)
    await eng.tool.call({ tool: 'Edit', tool_use_id: 'u1', file_path: '/w/a.ts', old_string: 'a', new_string: 'b' } as never)
    // The sentinel starts by asking for the working directory; the digest, after `next`, reads its state.
    expect(rig.attempts['session.cwd']).toBeGreaterThan(0)
    expect(rig.attempts['state.get']).toBeGreaterThan(0)
  })

  test(`${mode}: every mod that registers a command at session start is still reached`, async (eng, on) => {
    const rig = inject(on, mode)
    await eng.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true } as never)
    expect(rig.registered).toContain('undo-capture')
    expect(rig.registered).toContain('digest-review')
    expect(rig.registered).toContain('lens')
    // The sentinel registers `invariant` then `why` in one step, so a failure of the first ends its hook.
    expect(rig.registered).toContain('invariant')
  })

  for (const [name, props] of [
    ['AbovePrompt', { hasSurvey: false, isWorking: false, maxRows: 5, bodyColumns: 80, scroll: { offset: 0, bodyRows: 5 }, view: {} }],
  ] as const) {
    test(`${mode}: ${name} still draws something and the core draws at most once`, async (eng, on) => {
      const rig = inject(on, mode)
      const ui = await eng.ui.mount({ plugin: 'engram', surface: 'terminal', component: name, props } as never)
      expect(await (ui as { find: (q: object) => Promise<unknown> }).find({ type: 'Text' })).toBeDefined()
      expect(rig.core['ui.render'] ?? 0).toBeLessThanOrEqual(1)
    })
  }

  for (const id of ['engram-lens', DIGEST_PANE, 'engram-why']) {
    test(`${mode}: the ${id} pane still draws and the core draws at most once`, async (eng, on) => {
      const rig = inject(on, mode)
      const ui = await eng.ui.mount({
        plugin: 'engram',
        surface: 'terminal',
        component: 'Pane',
        requestId: id,
        props: { requestId: id, scroll: { offset: 0, bodyRows: 5 }, view: {}, bodyColumns: 80 },
      } as never)
      expect(await (ui as { find: (q: object) => Promise<unknown> }).find({ type: 'Text' })).toBeDefined()
      expect(rig.core['ui.render'] ?? 0).toBeLessThanOrEqual(1)
    })
  }

  // The engine does not run a hook again once it has failed in a dispatch, so a core that rejects
  // cannot show a second `next` call here (measured: a resolving core is re-run, a failed one is
  // not). guard.test.ts holds `once` against a rejecting next, and PluginSourceTests holds every
  // handler to calling `next` only through it; this row holds that the failure surfaces unchanged.
  test(`${mode}: a slash prompt whose next rejects runs the core once (its failure surfaces; it is not retried)`, async (eng, on) => {
    const rig = inject(on, mode, () => Promise.reject(new Error('core failed')))
    await expect(eng.classic.UserPromptSubmit({ prompt: '/clear' })).rejects.toThrow()
    expect(rig.core['classic.UserPromptSubmit']).toBe(1)
  })
}
