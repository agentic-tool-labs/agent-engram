import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { EDIT_TOOLS, engramCli, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import type { SentinelState } from '../shared/state'
import type { PathFact } from '../shared/types'

const SENTINEL = atom({ plugin: 'engram', key: 'sentinel' } as const, { seen: [], failedAt: {} })

// The scanner reads an atom's reference only from a const of the file that uses it, and follows
// `$` only into a function of this file, so the shared client gets the engine through this binding.
// Pure forwards; the behaviour is the client's.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, {
  binary: null,
  port: null,
  noPortAt: null,
  unsupported: false,
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
  if (why === undefined) return [{ text: 'Run /why <path>.' }]
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
  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: 'invariant',
      description: 'Record an invariant for a file: /invariant <file> <statement>',
    })
    await $.command.register({ name: 'why', description: 'Show what Engram records for a file: /why <path>' })
    return next(e)
  }).catch(($, e, next) => next(e))

  on('tool.call', { tool: [...EDIT_TOOLS] }, async ($, e, next) => {
    const mode = modeOf(options.sentinel_mode)
    const given = 'file_path' in e ? e.file_path : undefined
    if (mode === 'off' || typeof given !== 'string' || given === '') return next(e)

    const cwd = (await $.session.cwd()).replace(/\/+$/, '')
    const path = absolute(given, cwd)
    const key = `${e.agentId ?? ''}\0${path}`
    const state = await read($, SENTINEL)
    if (state.seen.includes(key)) return next(e)

    const io = bindIo($)
    const now = await io.now()
    const failedAt = state.failedAt[key]
    if (failedAt !== undefined && now - failedAt < FAILURE_SKIP_MS) return next(e)

    const res = await modApi(io, 'path-facts', { path, predicate: 'invariant' }, {
      mod: 'sentinel',
      timeoutMs: LOOKUP_TIMEOUT_MS,
      signal: next.signal,
    })
    if (!res.ok) {
      await update($, SENTINEL, (s) => ({ ...s, failedAt: { ...s.failedAt, [key]: now } }))
      return next(e)
    }

    // The claim is made inside the update so two concurrent edits of one file cannot both announce.
    // ponytail: `seen` grows with the files edited in a session; cap it if that ever matters.
    let claimed = false
    await update($, SENTINEL, (s) => {
      claimed = !s.seen.includes(key)
      return claimed ? { ...s, seen: [...s.seen, key] } : s
    })
    const facts = res.value.facts
    if (!claimed || facts.length === 0) return next(e)

    const rel = relative(path, cwd)
    const block = describe(rel, facts)
    if (mode === 'deny-once') return { deny: block + '\nRe-issue the edit if it respects these.' }

    $.ui.toast(`${facts.length} invariant(s) recorded for ${rel}`)
    const ran = await next(e)
    if (ran.deny !== undefined) return ran
    return { ...ran, context: [...(ran.context ?? []), block] }
  }).catch(($, e, next) => next(e))

  on('command.run', { command: 'invariant' }, async ($, e) => {
    const m = /^\s*(\S+)\s+([\s\S]*\S)\s*$/.exec(e.args)
    if (m === null) return { text: 'Usage: /invariant <file> <statement>' }
    const ran = await engramCli(bindIo($), ['invariant', 'add', m[1]!, m[2]!])
    const text =
      ran === undefined
        ? 'Engram binary not found.'
        : ran.exitCode === 0
          ? (firstLine(ran.stdout) ?? 'Recorded.')
          : (firstLine(ran.stderr) ?? `engram invariant add failed (exit ${ran.exitCode}).`)
    $.ui.toast(text)
    return { text }
  })

  on('command.run', { command: 'why' }, async ($, e) => {
    const arg = e.args.trim()
    if (arg === '') return { text: 'Usage: /why <path>' }
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
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
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
  })
}
