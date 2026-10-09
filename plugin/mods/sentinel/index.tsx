import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { EDIT_TOOLS, engramCli, modApi } from '../shared/client'
import { ANY_SESSION_START } from '../shared/events'
import type { ModIo } from '../shared/client'
import type { SentinelState } from '../shared/state'
import type { PathFact } from '../shared/types'
import { once } from '../shared/guard'
import { forSession } from '../shared/session'

// Bump when SentinelState changes incompatibly. The host reads an atom written under another shape
// as absent, so an older module's state is replaced, never read field by field or migrated.
export const SENTINEL_SHAPE = 'sentinel-2'

const SENTINEL = atom({ plugin: 'engram', key: 'sentinel' } as const, { seen: {}, failedAt: {} }, { shape: SENTINEL_SHAPE })

// What a new session starts the per-session fields at.
const SENTINEL_SESSION = { seen: {}, failedAt: {} }

// The scanner reads an atom's reference only from a const of the file that uses it, and follows
// `$` only into a function of this file, so the shared client gets the engine through this binding.
// Pure forwards; the behaviour is the client's.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, {
  binary: null,
  port: null,
  noPortAt: null,
  unsupportedAt: null,
})

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

const PANE = 'engram-why'
const LOOKUP_TIMEOUT_MS = 300
const WHY_TIMEOUT_MS = 2_000
const FAILURE_SKIP_MS = 60_000

type Mode = 'off' | 'inform' | 'deny-once'
const modeOf = (value: unknown): Mode => (value === 'off' || value === 'deny-once' ? value : 'inform')

const isAbsolute = (p: string) => p.startsWith('/') || /^[A-Za-z]:[\\/]/.test(p)

// POSIX paths only: the mod API needs an absolute path with no `..`, and a drive-lettered path
// is passed through untouched.
function absolute(path: string, cwd: string): string {
  const joined = isAbsolute(path) ? path : cwd + '/' + path
  if (!joined.startsWith('/')) return joined
  const out: string[] = []
  for (const part of joined.split('/')) {
    if (part === '' || part === '.') continue
    if (part === '..') out.pop()
    else out.push(part)
  }
  return '/' + out.join('/')
}

const relative = (path: string, cwd: string) => (path.startsWith(cwd + '/') ? path.slice(cwd.length + 1) : path)

const describe = (rel: string, facts: readonly PathFact[]) =>
  `Invariants recorded for ${rel}:\n` + facts.map((f) => `- [${f.handle}] ${f.body}`).join('\n')

const firstLine = (text: string) => text.split('\n').find((l) => l.trim() !== '')?.trim()

type Why = NonNullable<SentinelState['why']>

type Row = { text: string; heading?: boolean }

function whyRows(why: Why | undefined): Row[] {
  if (why === undefined) return [{ text: 'Run /engram-why <path>.' }]
  if (why.state === 'loading') return [{ text: 'Loading…' }]
  if (why.state === 'unsupported') return [{ text: 'This needs a newer Engram: its mod API is missing.' }]
  if (why.state === 'unavailable') return [{ text: 'The Engram server did not answer.' }]
  if (why.entityPath === null) return [{ text: 'Not inside an enrolled repo: nothing is recorded for this file.' }]
  const groups: [string, typeof why.facts][] = [
    ['Invariants', why.facts.filter((f) => f.predicate === 'invariant')],
    ['Other authored facts', why.facts.filter((f) => f.predicate !== 'invariant' && !f.regenerable)],
    ['Code facts', why.facts.filter((f) => f.predicate !== 'invariant' && f.regenerable)],
  ]
  const rows = groups.flatMap(([title, facts]) =>
    facts.length === 0
      ? []
      : [{ text: title, heading: true }, ...facts.map((f) => ({ text: `[${f.handle}] ${f.body}` }))],
  )
  return rows.length === 0 ? [{ text: 'Nothing is recorded for this file.' }] : rows
}

export const register: Register = (on, options) => {
  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      await $.command.register({
        name: 'engram-invariant',
        description: 'Record an invariant for a file: /engram-invariant <file> <statement>',
      })
      await $.command.register({ name: 'engram-why', description: 'Show what Engram records for a file: /engram-why <path>' })
      return go(e)
    } catch {
      return go.fallback(e)
    }
  }).catch(($, e, next) => next(e))

  on('tool.call', { tool: [...EDIT_TOOLS] }, async ($, e, next) => {
    const go = once(next)
    try {
      // Every mod shares this plugin, and a throwing tool.call hook takes the plugin's whole chain
      // down with it, `.catch` or not. So nothing before or after `next` may throw: the lookup is
      // planned inside a guard, `next` is called exactly once, and the bookkeeping after it is guarded.
      let key: string | undefined
      let sessionId = ''
      let announced: string[] = []
      // Takes back exactly the handles this call announced, so the next attempt announces them again.
      const release = () =>
        update($, SENTINEL, (s) => {
          const t = forSession(s, sessionId, SENTINEL_SESSION)
          return { ...t, seen: { ...t.seen, [key!]: (t.seen[key!] ?? []).filter((h) => !announced.includes(h)) } }
        })
      const quietly = async (work: () => Promise<unknown> | void) => {
        try {
          await work()
        } catch {
          // Bookkeeping that fails leaves the edit as it was.
        }
      }

      type Plan = { mode: Mode; rel: string; block: string; count: number }
      const plan = async (): Promise<Plan | undefined> => {
        const mode = modeOf(options.sentinel_mode)
        const given = 'file_path' in e ? e.file_path : undefined
        if (mode === 'off' || typeof given !== 'string' || given === '') return undefined

        const cwd = (await $.session.cwd()).replace(/\/+$/, '')
        const path = absolute(given, cwd)
        const mine = `${e.agentId ?? ''}\0${path}`
        const session = await $.session.id()
        const state = forSession(await read($, SENTINEL), session, SENTINEL_SESSION)

        const io = bindIo($)
        const now = await io.now()
        const failedAt = state.failedAt[mine]
        if (failedAt !== undefined && now - failedAt < FAILURE_SKIP_MS) return undefined

        // Every edit asks again: only a lookup can show an invariant added since the last one.
        const res = await modApi(io, 'path-facts', { path, predicate: 'invariant' }, {
          mod: 'sentinel',
          timeoutMs: LOOKUP_TIMEOUT_MS,
          signal: next.signal,
        })
        if (!res.ok) {
          await update($, SENTINEL, (s) => {
            const t = forSession(s, session, SENTINEL_SESSION)
            return { ...t, failedAt: { ...t.failedAt, [mine]: now } }
          })
          return undefined
        }

        // The handles are recorded inside the update so two concurrent edits of one file cannot both
        // announce them. Only handles this agent has not been told are announced.
        // ponytail: `seen` grows with the files edited in a session; cap it if that ever matters.
        let fresh: PathFact[] = []
        await update($, SENTINEL, (s) => {
          const t = forSession(s, session, SENTINEL_SESSION)
          const held = t.seen[mine] ?? []
          fresh = res.value.facts.filter((f) => !held.includes(f.handle))
          return fresh.length === 0 ? t : { ...t, seen: { ...t.seen, [mine]: [...held, ...fresh.map((f) => f.handle)] } }
        })
        if (fresh.length === 0) return undefined
        key = mine
        sessionId = session
        announced = fresh.map((f) => f.handle)
        const rel = relative(path, cwd)
        return { mode, rel, block: describe(rel, fresh), count: fresh.length }
      }

      let planned: Plan | undefined
      try {
        planned = await plan()
      } catch {
        planned = undefined
        if (key !== undefined) await quietly(release)
      }
      if (planned === undefined) return go(e)
      if (planned.mode === 'deny-once') return { deny: planned.block + '\nRe-issue the edit if it respects these.' }

      // `seen` means the model received the block: a call refused from below, or one that threw,
      // delivered nothing, so the claim goes back and the next edit of the file delivers.
      let ran
      try {
        ran = await go(e)
      } catch (error) {
        await quietly(release)
        throw error
      }
      if (ran.deny !== undefined) {
        await quietly(release)
        return ran
      }
      await quietly(() => $.ui.toast(`${planned.count} invariant(s) recorded for ${planned.rel}`))
      return { ...ran, context: [...(ran.context ?? []), planned.block] }
    } catch {
      return go.fallback(e)
    }
  }).catch(($, e, next) => next(e))

  on('command.run', { command: 'engram-invariant' }, async ($, e, next) => {
    const go = once(next)
    try {
      const m = /^\s*(\S+)\s+([\s\S]*\S)\s*$/.exec(e.args)
      if (m === null) return { text: 'Usage: /engram-invariant <file> <statement>' }
      const ran = await engramCli(bindIo($), ['invariant', 'add', m[1]!, m[2]!])
      const text =
        ran === undefined
          ? 'Engram binary not found.'
          : ran.exitCode === 0
            ? (firstLine(ran.stdout) ?? 'Recorded.')
            : (firstLine(ran.stderr) ?? `engram invariant add failed (exit ${ran.exitCode}).`)
      $.ui.toast(text)
      return { text }
    } catch {
      return go.fallback(e)
    }
  })

  on('command.run', { command: 'engram-why' }, async ($, e, next) => {
    const go = once(next)
    try {
      const arg = e.args.trim()
      if (arg === '') return { text: 'Usage: /engram-why <path>' }
      const cwd = (await $.session.cwd()).replace(/\/+$/, '')
      const path = absolute(arg, cwd)
      const rel = relative(path, cwd)
      await update($, SENTINEL, (s) => ({ ...s, why: { path: rel, state: 'loading' as const, entityPath: null, facts: [] } }))
      await $.ui.open({ id: PANE, title: `Why: ${rel}` })
      const res = await modApi(bindIo($), 'path-facts', { path }, { mod: 'sentinel', timeoutMs: WHY_TIMEOUT_MS })
      const why = res.ok
        ? { path: rel, state: 'ready' as const, entityPath: res.value.entity_path, facts: res.value.facts }
        : {
            path: rel,
            state: res.reason === 'unsupported' ? ('unsupported' as const) : ('unavailable' as const),
            entityPath: null,
            facts: [],
          }
      await update($, SENTINEL, (s) => ({ ...s, why }))
      return { text: `Opened ${PANE} for ${rel}.` }
    } catch {
      return go.fallback(e)
    }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e, next) => {
    const go = once(next)
    try {
      const { Box, Text } = $.ui.resolve(e)
      const why = (await read($, SENTINEL)).why
      const rows = whyRows(why)
      const room = Math.max(1, (e.viewport?.rows ?? 24) - 4)
      const shown = rows.length > room ? rows.slice(0, room - 1) : rows
      return (
        <Box flexDirection="column">
          {why !== undefined && <Text dimColor>{why.path}</Text>}
          {shown.map((row) => (
            <Text bold={row.heading === true}>{row.text}</Text>
          ))}
          {shown.length < rows.length && <Text dimColor>… {rows.length - shown.length} more</Text>}
        </Box>
      )
    } catch {
      return go.fallback(e)
    }
  })
}
