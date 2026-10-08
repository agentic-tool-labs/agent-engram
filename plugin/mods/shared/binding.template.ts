// The ModIo binding every hooks file that uses the client declares for itself. Copy it into the
// file; do not import it. The hooks scanner follows `$` only into a function declared in the
// same file and requires each engine call to be spelled `$.noun.event(...)` there, so the binding
// cannot live in a shared module. It must stay pure forwards: no defaults, caching, error
// handling or argument rewriting, because all of that is the client's, once.
// This file is type-checked (a missing or extra member is a tsc error) but never loaded.
import { atom, read, update } from 'claude-code'
import type { EngineInterface } from 'claude-code'
import { SHARED_INITIAL } from './client'
import type { ModIo } from './client'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

export const bindIo = ($: EngineInterface): ModIo => ({
  run: (argv, opts) => $.process.run(argv, opts),
  fetch: (url, init) => $.http.fetch(url, init),
  sleep: (ms, opts) => $.clock.sleep(ms, opts),
  now: () => $.clock.now(),
  sessionId: () => $.session.id(),
  pluginRoot: $.plugin.root,
  readShared: () => read($, SHARED),
  updateShared: (fn) => update($, SHARED, fn),
})
