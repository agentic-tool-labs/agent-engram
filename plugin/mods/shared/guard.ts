// No hook of any mod may throw or call `next` twice. The engine runs every mod in one plugin, and a
// hook that throws before `next` stops the hooks beneath it for that event (measured: a second
// mod's hook on the same event never ran), while `.catch` is plugin-wide and cannot tell mods apart.
// Calling `next` again re-runs the core. So each handler wraps its body:
//
//   async ($, e, next) => {
//     const go = once(next)
//     try { ...body, calling go(e) instead of next(e)... } catch { return go.fallback(e) }
//   }
//
// Read `budget` (an engine getter) from the raw `next`, not from `once(next)`: the copy is a snapshot.
// What the engine hangs on `next` besides `signal` is not otherwise relied on.
//
// The scanner refuses a hook that is not a function literal and a `$` passed across an import, so
// this helper takes only `next`; the try/catch stays in the handler, where `$` is.

/** `next`, callable any number of times but run once; `fallback` returns what `next` settled to. */
export type Once<N extends (...args: never[]) => unknown> = N & { fallback: N }

export function once<N extends (...args: any[]) => any>(next: N): Once<N> {
  let called = false
  let result: unknown
  const run = ((...args: unknown[]) => {
    if (!called) {
      called = true
      try {
        result = next(...args)
      } catch (err) {
        result = Promise.reject(err)
      }
    }
    return result
  }) as unknown as Once<N>
  // Keeps whatever the engine hangs on `next`, `signal` included.
  Object.assign(run, next)
  // A handler that failed before calling `next` calls it now; one that failed after gets the same
  // result back. A `next` that itself rejected rejects again, which is the core's failure and not the mod's.
  run.fallback = run as unknown as N
  return run
}
