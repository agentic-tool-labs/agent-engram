import { atom, read, update } from 'claude-code'
import type { EngineInterface, PluginOptions, PromptSubmitInput, PromptSubmitResult, Register } from 'claude-code'
import { SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import { ANY_PROMPT_SUBMIT } from '../shared/events'
import { once } from '../shared/guard'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

// Pure forwards: the scanner needs each engine call spelled here, and all behaviour is the client's.
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

export type JitMode = 'off' | 'shadow' | 'inject'
export type JitConfig = { mode: JitMode; budgetTokens: number }

const QUERY_CHARS = 2_000
const MIN_PROMPT_CHARS = 12
const RECALL_TIMEOUT_MS = 800
// The recall API rejects a budget outside this integer range, which in shadow would measure nothing.
const MIN_BUDGET = 50
const MAX_BUDGET = 4_000
const DEFAULT_BUDGET = 400
const BLOCK_HEADER = 'Memory relevant to this message (recalled automatically):\n'

export function jitConfig(options: PluginOptions): JitConfig {
  const mode = options.jit_mode
  const budget = options.jit_budget_tokens
  return {
    mode: mode === 'shadow' || mode === 'inject' ? mode : 'off',
    budgetTokens:
      typeof budget === 'number' && Number.isInteger(budget) && budget >= MIN_BUDGET && budget <= MAX_BUDGET
        ? budget
        : DEFAULT_BUDGET,
  }
}

type SubmitNext = {
  (e: PromptSubmitInput): Promise<PromptSubmitResult>
  readonly signal: AbortSignal
}

/**
 * In `inject` mode, recalls on the prompt and, with `high` coverage, attaches the digest as
 * model-only context for this prompt. In `shadow` mode the recall is started and not awaited.
 * Every other path, including every failure, passes the prompt through untouched.
 */
export async function primePrompt(
  io: ModIo,
  config: JitConfig,
  e: PromptSubmitInput,
  next: SubmitNext,
): Promise<PromptSubmitResult> {
  if (config.mode === 'off') return next(e)
  if (e.origin?.kind !== 'composer') return next(e)
  if (e.text.startsWith('/') || e.text.trim().length < MIN_PROMPT_CHARS) return next(e)

  const mode = config.mode
  const ask = async (signal?: AbortSignal) =>
    modApi(
      io,
      'recall',
      {
        session_id: await io.sessionId(),
        query: e.text.slice(0, QUERY_CHARS),
        budget_tokens: config.budgetTokens,
        mode,
      },
      signal === undefined
        ? { mod: 'primer', timeoutMs: RECALL_TIMEOUT_MS }
        : { mod: 'primer', timeoutMs: RECALL_TIMEOUT_MS, signal },
    )

  if (mode === 'shadow') {
    // The server's mod-call record is the measurement, so nothing is waited for or read back.
    // The dispatch signal is not passed: the request must outlive this hook.
    ask().catch(() => {})
    return next(e)
  }

  let digest: string | undefined
  try {
    const reply = await ask(next.signal)
    if (reply.ok && reply.value.coverage === 'high' && reply.value.text.trim() !== '') digest = reply.value.text
  } catch {
    digest = undefined
  }

  if (digest === undefined) return next(e)
  return next({ ...e, context: [...(e.context ?? []), BLOCK_HEADER + digest] })
}

export const register: Register = (on, options) => {
  const config = jitConfig(options)
  on('prompt.submit', ANY_PROMPT_SUBMIT, async ($, e, next) => {
    const go = once(next)
    try {
      let io: ModIo
      try {
        io = bindIo($)
      } catch {
        return go(e)
      }
      return await primePrompt(io, config, e, go)
    } catch {
      return go.fallback(e)
    }
  })
}
