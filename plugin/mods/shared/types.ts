import type { HistoryView } from './state'

export type ApiFailure = {
  ok: false
  reason: 'server-down' | 'unsupported' | 'not-initialised' | 'bad-request' | 'not-found' | 'timeout' | 'error'
  detail?: string
}
export type ApiResult<T> = { ok: true; value: T } | ApiFailure
export type ApiReason = ApiFailure['reason']

export type Coverage = 'high' | 'partial' | 'none'

export type RecallLanes = { lexical: number | null; overlap: number | null; vector: number | null }

export type RecallFact = {
  handle: string
  id: number
  body: string
  scope: string
  versions: number
  withheld_chars: number
  location: string | null
  lanes: RecallLanes
}

export type RecallRequest = {
  session_id: string
  query: string
  budget_tokens?: number
  mode?: 'shadow' | 'inject'
}

export type RecallResponse = {
  coverage: Coverage
  fact_count: number
  notes: string[]
  gaps: string | null
  text: string
  facts: RecallFact[]
}

export type FactRequest = { fact_id: string }

export type FactResponse = {
  handle: string
  id: number
  path: string
  predicate: string
  body: string
  details: string | null
  scope: string
  learned_via: string
  evidence: string | null
  valid_from: number
  valid_to: number | null
  live: boolean
  versions: number
}

export type HistoryRequest = { fact_id: string }

export type { HistoryVersion } from './state'
export type HistoryResponse = HistoryView

export type ForgetRequest = { session_id: string; fact_id: string }
export type ForgetResponse = { handle: string; retracted: boolean }

export type RememberRequest = {
  session_id: string
  statement: string
  details?: string
  subject?: string
  evidence: string
}
export type RememberResponse = { handle: string; id: number; created: boolean }

export type CapturesRequest = { session_id: string; since: number }
export type Capture = { handle: string; id: number; body: string; created_at: number }
export type CapturesResponse = { captures: Capture[] }

export type PathFactsRequest = { path: string; predicate?: string }
export type PathFact = {
  handle: string
  id: number
  subject_path: string
  predicate: string
  body: string
  scope: string
  regenerable: boolean
  valid_from: number
}
export type PathFactsResponse = { entity_path: string | null; repo: string | null; facts: PathFact[] }

export type TailRequest = {
  session_id: string
  after?: number
  closed_after?: number
  event_epoch?: string
  event_after?: number
  scope?: 'session' | 'all'
  limit?: number
}

export type TailWrite = {
  handle: string
  id: number
  created_at: number
  origin: string
  body: string
  evidence: string | null
  replaces: string | null
  live: boolean
  this_session: boolean
}

export type TailRetraction = {
  handle: string
  id: number
  retracted_at: number
  reason: string
  body: string
  origin: string
  this_session: boolean
}

/** A telemetry log line as the server serialises it; every field but the first three is optional. */
export type TailEventRecord = {
  timestamp: string
  session_id: string
  kind: string
  query?: string | null
  fact_count?: number | null
  tokens_returned?: number | null
  coverage?: string | null
  long_term_fact_count?: number | null
  agent_type?: string | null
  phase?: string | null
  path?: string | null
  repo?: string | null
  tool?: string | null
  decision?: string | null
  relation?: string | null
  mod?: string | null
  mode?: string | null
}

export type TailResponse = {
  head: number
  now: number
  writes: { rows: TailWrite[]; skipped: number | null }
  retractions: { rows: TailRetraction[] }
  events: { epoch: string; head: number; rows: { seq: number; record: TailEventRecord }[]; skipped: number | null } | null
}

/** Request and response of every mod API op, keyed by op name. */
export type ModOps = {
  recall: { request: RecallRequest; response: RecallResponse }
  fact: { request: FactRequest; response: FactResponse }
  history: { request: HistoryRequest; response: HistoryResponse }
  forget: { request: ForgetRequest; response: ForgetResponse }
  remember: { request: RememberRequest; response: RememberResponse }
  captures: { request: CapturesRequest; response: CapturesResponse }
  'path-facts': { request: PathFactsRequest; response: PathFactsResponse }
  tail: { request: TailRequest; response: TailResponse }
}
export type ModOp = keyof ModOps
