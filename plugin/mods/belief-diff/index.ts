import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { ENGRAM_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import type { BeliefDiffState } from '../shared/state'
import type { FactResponse } from '../shared/types'

const MOD = 'belief-diff'
const APPROVE = 'Approve'
const KEEP_OLD = 'Keep old'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)
const BELIEF = atom({ plugin: 'engram', key: 'beliefDiff' } as const, { skipToastShown: false } as BeliefDiffState)

// Pure forwards: the hooks scanner follows `$` only into a function declared in this file.
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

type Scope = 'off' | 'personal' | 'all'
type Decision = 'approve' | 'keep' | 'unanswered' | 'aborted'

const scopeOf = (value: unknown): Scope => (value === 'off' || value === 'all' ? value : 'personal')

const pad = (n: number) => String(n).padStart(2, '0')

/** Local time to the second: two revisions seconds apart must not read alike. */
export function moment(unixSeconds: number): string {
  const d = new Date(unixSeconds * 1000)
  const date = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
  return `${date} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

const bare = (handle: string) => handle.replace(/^\[|\]$/g, '')

export function questionFor(fact: FactResponse, statement: string, details: unknown, reason: unknown): string {
  const lines = [
    `Revise [${bare(fact.handle)}]  ${fact.path} · ${fact.predicate}`,
    `Now (since ${moment(fact.valid_from)}):`,
    `  ${fact.body}`,
    'Proposed:',
    `  ${statement}`,
    `Reason: ${typeof reason === 'string' && reason.trim() !== '' ? reason : '(none given)'}`,
  ]
  const dropped = fact.details !== null && fact.details !== '' && (typeof details !== 'string' || details.trim() === '')
  if (dropped) {
    lines.push(
      `This revision drops the existing details (${fact.details!.length} chars). Revise never carries details forward.`,
    )
  }
  lines.push('Approve this revision?')
  return lines.join('\n')
}

// One dialog at a time. A promise is not state, so the queue is module memory; the atom records
// which question is open.
let tail: Promise<unknown> = Promise.resolve()
function serialize<T>(job: () => Promise<T>): Promise<T> {
  const run = tail.then(job)
  tail = run.then(
    () => undefined,
    () => undefined,
  )
  return run
}

async function ask($: EngineInterface, question: string): Promise<Decision> {
  try {
    const answer = await $.ui.ask(question, [APPROVE, KEEP_OLD])
    return answer === APPROVE ? 'approve' : answer === KEEP_OLD ? 'keep' : 'unanswered'
  } catch {
    return 'unanswered'
  }
}

async function toastSkipOnce($: EngineInterface, handle: string): Promise<void> {
  try {
    if ((await read($, BELIEF)).skipToastShown) return
    await update($, BELIEF, (s) => ({ ...s, skipToastShown: true }))
    $.ui.toast(`Belief diff skipped for [${bare(handle)}]: could not ask`)
  } catch {
    // A failed notice must not stand between the user and a revise they already asked for.
  }
}

async function decide(
  $: EngineInterface,
  question: string,
  toolUseId: string,
  handle: string,
  signal: AbortSignal,
): Promise<Decision> {
  await update($, BELIEF, (s) => ({ ...s, pending: { toolUseId, handle } }))
  let onAbort: (() => void) | undefined
  const aborted = new Promise<Decision>((resolve) => {
    onAbort = () => resolve('aborted')
    signal.addEventListener('abort', onAbort, { once: true })
  })
  try {
    return await Promise.race([ask($, question), aborted])
  } finally {
    if (onAbort !== undefined) signal.removeEventListener('abort', onAbort)
    await update($, BELIEF, (s) => ({ skipToastShown: s.skipToastShown }))
  }
}

export const register: Register = (on, options) => {
  on('tool.call', { tool: ENGRAM_TOOLS.revise }, ($, e, next) => {
    const scope = scopeOf(options.belief_diff_scope)
    if (scope === 'off') return next(e)

    return serialize(async () => {
      // A hook whose budget ran out has already had its revise run on its behalf; a dialog now
      // would ask about something that is done.
      if (next.signal.aborted) return next(e)

      const factId = (e as { fact_id?: unknown }).fact_id
      const statement = (e as { statement?: unknown }).statement
      if (typeof factId !== 'string' || typeof statement !== 'string') return next(e)

      const found = await modApi(bindIo($), 'fact', { fact_id: factId }, { mod: MOD, signal: next.signal })
      if (!found.ok || !found.value.live) return next(e)
      const fact = found.value
      if (scope === 'personal' && fact.scope !== 'user') return next(e)

      const question = questionFor(
        fact,
        statement,
        (e as { details?: unknown }).details,
        (e as { reason?: unknown }).reason,
      )
      const decision = await decide($, question, e.tool_use_id, fact.handle, next.signal)

      if (decision === 'keep') return { deny: `The user kept the existing belief [${bare(fact.handle)}]; do not retry this revision.` }
      if (decision === 'unanswered') await toastSkipOnce($, fact.handle)
      return next(e)
    })
  })
}
