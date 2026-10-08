import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { ENGRAM_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import type { BeliefDiffState } from '../shared/state'
import type { FactResponse } from '../shared/types'
import { once } from '../shared/guard'

const MOD = 'belief-diff'
const APPROVE = 'Approve'
const KEEP_OLD = 'Keep old'

// The engine words a user's dismissal of a dialog this way; a hook that denies the dialog says
// "hook error" instead, and that is a mod that could not ask, not a user who declined.
const DISMISSED = "The user doesn't want to proceed with this tool use"
const DENIED_BY_HOOK = 'hook error'

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
type Decision = 'approve' | 'keep' | 'dismissed' | 'unanswered' | 'aborted'

const scopeOf = (value: unknown): Scope => (value === 'off' || value === 'all' ? value : 'personal')

const pad = (n: number) => String(n).padStart(2, '0')

/** Local time to the second: two revisions seconds apart must not read alike. */
export function moment(unixSeconds: number): string {
  const d = new Date(unixSeconds * 1000)
  const date = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
  return `${date} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

const bare = (handle: string) => handle.replace(/^\[|\]$/g, '')

/** The user's own beliefs: statements and directives, and the invariants they recorded. */
export const isPersonal = (fact: FactResponse): boolean => fact.scope === 'user' || fact.predicate === 'invariant'

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

async function ask($: EngineInterface, question: string): Promise<Decision> {
  try {
    const answer = await $.ui.ask(question, [APPROVE, KEEP_OLD])
    return answer === APPROVE ? 'approve' : answer === KEEP_OLD ? 'keep' : 'unanswered'
  } catch (err) {
    const text = String(err)
    return text.includes(DISMISSED) && !text.includes(DENIED_BY_HOOK) ? 'dismissed' : 'unanswered'
  }
}

async function toastSkipOnce($: EngineInterface, handle: string): Promise<void> {
  try {
    if ((await read($, BELIEF)).skipToastShown) return
    await update($, BELIEF, (s) => ({ ...s, skipToastShown: true }))
    $.ui.toast(`Belief diff skipped for [${bare(handle)}]: could not ask — revision applied unreviewed`)
  } catch {
    // A failed notice must not stand between the user and a revise they already asked for.
  }
}

async function decide($: EngineInterface, question: string, signal: AbortSignal): Promise<Decision> {
  let onAbort: (() => void) | undefined
  const aborted = new Promise<Decision>((resolve) => {
    onAbort = () => resolve('aborted')
    signal.addEventListener('abort', onAbort, { once: true })
  })
  try {
    return await Promise.race([ask($, question), aborted])
  } finally {
    if (onAbort !== undefined) signal.removeEventListener('abort', onAbort)
  }
}

/** A refusal to hand back instead of running the revise, or undefined to let it run; never throws. */
async function review(
  $: EngineInterface,
  scope: Scope,
  args: Readonly<Record<string, unknown>>,
  signal: AbortSignal,
): Promise<{ deny: string } | undefined> {
  try {
    if (scope === 'off' || signal.aborted) return undefined
    if (typeof args.fact_id !== 'string' || typeof args.statement !== 'string') return undefined

    const found = await modApi(bindIo($), 'fact', { fact_id: args.fact_id }, { mod: MOD, signal })
    if (!found.ok || !found.value.live) return undefined
    const fact = found.value
    if (scope === 'personal' && !isPersonal(fact)) return undefined

    const decision = await decide($, questionFor(fact, args.statement, args.details, args.reason), signal)
    const handle = bare(fact.handle)
    if (decision === 'keep') return { deny: `The user kept the existing belief [${handle}]; do not retry this revision.` }
    if (decision === 'dismissed') {
      return {
        deny: `The user dismissed the review of [${handle}]; the existing belief was kept. Do not retry unless the user asks.`,
      }
    }
    if (decision === 'unanswered') await toastSkipOnce($, fact.handle)
    return undefined
  } catch {
    return undefined
  }
}

export const register: Register = (on, options) => {
  on('tool.call', { tool: ENGRAM_TOOLS.revise }, async ($, e, next) => {
    const go = once(next)
    try {
      const refusal = await review($, scopeOf(options.belief_diff_scope), e, next.signal)
      return refusal ?? go(e)
    } catch {
      return go.fallback(e)
    }
  })
}
