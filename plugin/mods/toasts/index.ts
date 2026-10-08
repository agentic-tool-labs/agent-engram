import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)
const TOASTS = atom({ plugin: 'engram', key: 'toasts' } as const, { shown: [] as string[] })

// Pure forwards (see ../shared/binding.template.ts); the behaviour is the client's.
const bindIo = ($: EngineInterface): ModIo => ({
  run: (argv, opts) => $.process.run(argv, opts),
  fetch: (url, init) => $.http.fetch(url, init),
  sleep: (ms, opts) => $.clock.sleep(ms, opts),
  now: () => $.clock.now(),
  sessionId: () => $.session.id(),
  pluginRoot: $.plugin.root,
  readShared: () => read($, SHARED),
  updateShared: (fn) => update($, SHARED, fn),
})

const UNDO_COMMAND = 'undo-capture'
// TODO: $.audio.play refuses a file: URL and an absolute asset path, so the chime is a silent no-op
// until the clip form for chime_sound is decided.
const DEFAULT_CHIME = '/System/Library/Sounds/Tink.aiff'

type RecallsValue = { recalls?: { toolUseId: string; coverage?: string }[] } | undefined

/** True when the Lens atom's newest recall is a different one than before and has high coverage. */
export function isNewHighRecall(value: unknown, previous: unknown): boolean {
  const newest = (value as RecallsValue)?.recalls?.[0]
  const before = (previous as RecallsValue)?.recalls?.[0]
  return newest !== undefined && newest.coverage === 'high' && newest.toolUseId !== before?.toolUseId
}

export const register: Register = (on, options) => {
  const toastsEnabled = options.toasts_enabled !== false
  const chime = typeof options.chime === 'string' ? options.chime : 'off'
  const chimeSound = typeof options.chime_sound === 'string' && options.chime_sound !== '' ? options.chime_sound : DEFAULT_CHIME
  const chimeOnCaptures = chime === 'captures' || chime === 'both'
  const chimeOnRecalls = chime === 'recalls' || chime === 'both'

  if (toastsEnabled) {
    on('session.start', async ($, e, next) => {
      await $.command.register({ name: UNDO_COMMAND, description: 'Forget the memory Engram captured most recently' })
      return next(e)
    })

    on('classic.UserPromptSubmit', async ($, e, next) => {
      if (e.prompt.startsWith('/')) return next(e)
      const io = bindIo($)
      let since = 0
      try {
        since = Math.floor((await io.now()) / 1000) - 1
      } catch {
        return next(e)
      }
      const ran = await next(e)
      try {
        const found = await modApi(
          io,
          'captures',
          { session_id: await io.sessionId(), since },
          { mod: 'toasts', signal: next.signal },
        )
        if (!found.ok) return ran
        let fresh: { handle: string; body: string }[] = []
        await update($, TOASTS, (s) => {
          fresh = found.value.captures.filter((c) => !s.shown.includes(c.handle))
          return { shown: [...s.shown, ...fresh.map((c) => c.handle)] }
        })
        for (const c of fresh) $.ui.toast(`Remembered [${c.handle}]: ${c.body}`)
        if (chimeOnCaptures && fresh.length > 0) $.audio.play({ url: 'file://' + chimeSound }).catch(() => {})
      } catch {
        // A failed lookup costs the toast and nothing else.
      }
      return ran
    })

    on('command.run', { command: UNDO_COMMAND }, async ($, e, next) => {
      const io = bindIo($)
      const { shown } = await read($, TOASTS)
      const handle = shown[shown.length - 1]
      if (handle === undefined) {
        $.ui.toast('No captured memory to forget')
        return {}
      }
      const done = await modApi(io, 'forget', { session_id: await io.sessionId(), fact_id: handle }, { mod: 'toasts' })
      if (!done.ok) $.ui.toast(`Could not forget [${handle}]: ${done.detail ?? done.reason}`)
      else if (done.value.retracted) $.ui.toast(`Forgot [${handle}]`)
      else $.ui.toast(`[${handle}] is already forgotten`)
      return {}
    })
  }

  if (chimeOnRecalls) {
    on('state.set', { plugin: 'engram', key: 'lens' }, ($, e, next) => {
      if (isNewHighRecall(e.value, e.previous)) $.audio.play({ url: 'file://' + chimeSound }).catch(() => {})
      return next(e)
    })
  }
}
