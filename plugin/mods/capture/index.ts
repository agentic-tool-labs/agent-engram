import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import { ANY_PROMPT_SUBMIT, isOwnPrompt } from '../shared/events'
import { once } from '../shared/guard'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

// The most the client allows: a capture that loses this race may still land, without its context.
const CAPTURE_TIMEOUT_MS = 2000

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

/** Hands the person's own prompt to the server, which stores any statement in it and says what to tell the model. */
export const register: Register = (on) => {
  on('prompt.submit', ANY_PROMPT_SUBMIT, async ($, e, next) => {
    const go = once(next)
    try {
      let context: string | null = null
      try {
        if (!isOwnPrompt(e)) return go(e)
        const io = bindIo($)
        const reply = await modApi(
          io,
          'capture',
          { session_id: await io.sessionId(), text: e.text },
          { mod: 'capture', timeoutMs: CAPTURE_TIMEOUT_MS, signal: next.signal },
        )
        if (reply.ok) context = reply.value.context
      } catch {
        // A capture that cannot be made is not worth failing a prompt over.
      }
      return context === null ? go(e) : go({ ...e, context: [...(e.context ?? []), context] })
    } catch {
      return go.fallback(e)
    }
  })
}
