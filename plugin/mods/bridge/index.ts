import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, ToolCallInput } from 'claude-code'
import { SHARED_INITIAL, engramCli } from '../shared/client'
import type { ModIo } from '../shared/client'
import { ANY_PROMPT_SUBMIT, ANY_SESSION_START } from '../shared/events'
import { once } from '../shared/guard'

// Where an organization manages Claude Code, hooks.json and every `classic.*` mod event are
// skipped, so this mod runs the same `engram hook <verb>` processes from mod events instead. Each
// verb stays the one implementation of its behaviour; `--source mod` lets the verb stand itself
// down when the classic hooks already ran for this session, so nothing fires twice.

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

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

export const PRIMER_TIMEOUT_MS = 5_000
export const GUARD_TIMEOUT_MS = 2_000

// hooks.json's matchers, one per verb. Patterns, not tool-name lists: this engine's tool names
// have no MultiEdit, and a list naming it does not type-check.
const GUARDED = /^(Write|Edit|MultiEdit)$/
const NUDGED = { tool: /^(Grep|Glob|Bash)$/ }
const TOUCHING = { tool: /^(Edit|Write|MultiEdit|NotebookEdit)$/ }
const ANY_ENGRAM_TOOL = { tool: /^mcp__plugin_engram_engram__/ }

type Json = Record<string, unknown>
type Payload = { session_id: string; cwd: string; hook_event_name: string } & Json

/** The verb's stdout as JSON, or nothing: no binary, a timeout, a non-zero exit, silence or non-JSON. */
export async function runVerb(io: ModIo, verb: string, payload: Payload, timeoutMs?: number): Promise<Json | undefined> {
  try {
    const opts = timeoutMs === undefined ? { stdin: JSON.stringify(payload) } : { stdin: JSON.stringify(payload), timeoutMs }
    const run = await engramCli(io, ['hook', verb, '--source', 'mod'], opts)
    if (run === undefined || run.exitCode !== 0) return undefined
    const out: unknown = JSON.parse(run.stdout)
    return typeof out === 'object' && out !== null && !Array.isArray(out) ? (out as Json) : undefined
  } catch {
    return undefined
  }
}

const specific = (out: Json | undefined): Json | undefined => {
  const inner = out?.hookSpecificOutput
  return typeof inner === 'object' && inner !== null ? (inner as Json) : undefined
}

/** A primer verb's `additionalContext`, when it printed one. */
export function contextOf(out: Json | undefined): string | undefined {
  const context = specific(out)?.additionalContext
  return typeof context === 'string' && context.trim() !== '' ? context : undefined
}

/** A guard verb's deny reason, when it denied. */
export function denyOf(out: Json | undefined): string | undefined {
  const inner = specific(out)
  return inner?.permissionDecision === 'deny' && typeof inner.permissionDecisionReason === 'string'
    ? inner.permissionDecisionReason
    : undefined
}

/** The call's own arguments, as a classic hook's `tool_input` carries them: the envelope keys left out. */
export function toolInputOf(e: ToolCallInput): Json {
  const { tool: _tool, tool_use_id: _id, agentId: _agent, requestMeta: _meta, ...input } = e as unknown as Json
  return input
}

export const register: Register = (on) => {
  // Module memory, so a process start (and a plugin reload) primes once.
  let primed = false
  let pending = false
  let lastMainCount: number | undefined

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      // As ensure-server.sh does; idempotent where the classic hook already started it.
      engramCli(bindIo($), ['start']).catch(() => {})
      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('prompt.submit', ANY_PROMPT_SUBMIT, async ($, e, next) => {
    const go = once(next)
    try {
      if (primed && !pending) return go(e)
      // Claimed before the await, so a second prompt in flight does not run the verb again.
      primed = true
      pending = false
      let primer: string | undefined
      try {
        const io = bindIo($)
        const payload = { session_id: await io.sessionId(), cwd: await $.session.cwd(), hook_event_name: 'SessionStart' }
        primer = contextOf(await runVerb(io, 'session-start', payload, PRIMER_TIMEOUT_MS))
      } catch {
        primer = undefined
      }
      return primer === undefined ? go(e) : go({ ...e, context: [...(e.context ?? []), primer] })
    } catch {
      return go.fallback(e)
    }
  })

  // `/clear` goes on under a new session id with no session.start, so the next prompt re-primes.
  on('session.end', { reason: 'clear' }, async ($, e, next) => {
    const go = once(next)
    try {
      pending = true
      lastMainCount = undefined
      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  // A main-loop request carrying fewer messages than the one before it follows a compaction.
  on('turn.step', async function* ($, e, next) {
    const go = once(next)
    try {
      if (e.agentId === undefined) {
        if (lastMainCount !== undefined && e.messageCount < lastMainCount) pending = true
        lastMainCount = e.messageCount
      }
      return yield* go(e)
    } catch {
      // The stream beneath is the one `go` already started; what it yielded stays.
      return yield* go.fallback(e)
    }
  })

  on('agent.spawn', async ($, e, next) => {
    const go = once(next)
    try {
      let primer: string | undefined
      try {
        const io = bindIo($)
        const payload = {
          session_id: await io.sessionId(),
          cwd: await $.session.cwd(),
          hook_event_name: 'SubagentStart',
          agent_type: e.subagentType,
        }
        primer = contextOf(await runVerb(io, 'subagent-start', payload, PRIMER_TIMEOUT_MS))
      } catch {
        primer = undefined
      }
      return primer === undefined ? go(e) : go({ ...e, prompt: primer + '\n\n' + e.prompt })
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', TOUCHING, async ($, e, next) => {
    const go = once(next)
    try {
      let io: ModIo | undefined
      let base: Payload | undefined
      try {
        io = bindIo($)
        base = { session_id: await io.sessionId(), cwd: await $.session.cwd(), hook_event_name: 'PreToolUse' }
      } catch {
        io = undefined
      }
      const tool = { tool_name: e.tool, tool_input: toolInputOf(e) }
      if (io !== undefined && base !== undefined && GUARDED.test(e.tool)) {
        const reason = denyOf(await runVerb(io, 'memory-guard', { ...base, ...tool }, GUARD_TIMEOUT_MS))
        if (reason !== undefined) return { deny: reason }
      }
      const ran = await go(e)
      if (io !== undefined && base !== undefined && ran.deny === undefined) {
        // Not awaited: the spool write must never hold the tool's result.
        runVerb(io, 'file-touched', { ...base, ...tool, hook_event_name: 'PostToolUse' }).catch(() => {})
      }
      return ran
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', NUDGED, async ($, e, next) => {
    const go = once(next)
    try {
      let reason: string | undefined
      try {
        const io = bindIo($)
        const payload = {
          session_id: await io.sessionId(),
          cwd: await $.session.cwd(),
          hook_event_name: 'PreToolUse',
          tool_name: e.tool,
          tool_input: toolInputOf(e),
        }
        reason = denyOf(await runVerb(io, 'lookup-nudge', payload, GUARD_TIMEOUT_MS))
      } catch {
        reason = undefined
      }
      return reason === undefined ? go(e) : { deny: reason }
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', ANY_ENGRAM_TOOL, async ($, e, next) => {
    const go = once(next)
    try {
      const ran = await go(e)
      // Not awaited, as file-touched: observation that fails costs a telemetry record, never the result.
      const observe = async () => {
        const io = bindIo($)
        const payload = {
          session_id: await io.sessionId(),
          cwd: await $.session.cwd(),
          hook_event_name: 'PostToolUse',
          tool_name: e.tool,
          tool_input: toolInputOf(e),
        }
        await runVerb(io, 'tool-observed', payload)
      }
      observe().catch(() => {})
      return ran
    } catch {
      return go.fallback(e)
    }
  })
}
