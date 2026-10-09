// The plugin's one state contract: PluginState['engram'] must be a single literal, so every
// mod's atom is declared here. Each stream edits only its own key's type (and the named types
// only that key uses). The file is self-contained: validate refuses an import in a manifest contract.

export type SharedState = {
  /** `null` until resolved; `{ path: null }` when no binary was found. */
  binary: { path: string | null } | null
  /** Loopback port of the running server, `null` when unknown. */
  port: number | null
  /** When the last lookup or connection failed to yield a port (`io.now()`), `null` if none yet. */
  noPortAt: number | null
  /** The server answered 404 to the mod API: no further calls this session. */
  unsupported: boolean
}

export type HistoryVersion = {
  handle: string
  body: string
  valid_from: number
  valid_to: number | null
  learned_via: string
  closed_reason: string | null
}

/** What the `history` op returns for a fact's thread, oldest first. */
export type HistoryView = { path: string; predicate: string; versions: HistoryVersion[] }

export type LensFact = {
  handle: string
  id: number
  body: string
  /** Raw paren text of the recall line. */
  meta: string
  /** 1 when the line carries no vN marker. */
  versions: number
  withheld?: string
  pinned: boolean
  judged: boolean
  location?: string
}

export type LensRecall = {
  toolUseId: string
  agentId?: string
  turnId?: string
  /** Milliseconds. */
  at: number
  query: string
  parsed: boolean
  isError: boolean
  coverage?: 'high' | 'partial' | 'none'
  factCount?: number
  /** Availability notes, verbatim. */
  notes: string[]
  gaps?: string
  facts: LensFact[]
  raw: string
}

export type LensState = {
  /** Newest first, at most 20. */
  recalls: LensRecall[]
  currentTurnId?: string
  /** Keyed by fact handle. */
  history: Record<string, HistoryView | 'loading' | 'unavailable' | 'failed'>
  /** Index into a loaded history's versions, keyed by fact handle; absent means the newest. Optional so readers of the recalls (the band) can build a LensState without it. */
  selected?: Record<string, number>
  /** Whether `/engram-lens` last left the pane open. */
  paneOpen?: boolean
  /** The pane has already been opened by `lens_auto_open` this session. */
  autoOpened?: boolean
}

/** The JSON of `engram embed --status --json`. */
export type EmbedStatus = {
  space: string | null
  provider: string | null
  embedded: number
  total: number
  remaining: number
  rate: string | null
  eta: string | null
  backlog: {
    state: 'running' | 'not-running' | 'stalled' | 'unavailable'
    pid: number | null
    last_update_seconds: number | null
    reason: string | null
  }
  note: string | null
  last_error: string | null
}

export type BandState = {
  /** toolUseIds. */
  pressed: string[]
  backlog?: EmbedStatus
  status?: { server: string; version: string }
  polledAt?: number
}

export type ToastsState = {
  /** Fact handles already announced. */
  shown: string[]
}

export type SentinelState = {
  /** Paths already announced, keyed by agentId + path for subagents. */
  seen: string[]
  /** Last API failure time per path (`io.now()`), for the 60 s skip. */
  failedAt: Record<string, number>
  /** What the `engram-why` pane shows; absent until `/engram-why` ran. */
  why?: {
    path: string
    state: 'loading' | 'ready' | 'unsupported' | 'unavailable'
    entityPath: string | null
    facts: { handle: string; predicate: string; subject_path: string; body: string; regenerable: boolean }[]
  }
}

export type BeliefDiffState = {
  skipToastShown: boolean
}

export type DigestCandidate = { text: string; ticked: boolean }

export type DigestState = {
  /** Main-session turns completed since the last digest ran. */
  turnsSinceDigest: number
  /** A main-session edit tool ran during the current turn. */
  editedThisTurn: boolean
  /** Length of the main conversation when the last digest read it. */
  seenMessages: number
  /** Fingerprint of the last row that digest read; the count is trusted only while it still matches. */
  lastSeen: string
  /** The latest batch awaiting Save or Skip, in display order. */
  candidates: DigestCandidate[]
}

/** The pane's toggles; a row belongs to exactly one, and a marker to none. */
export type TailGroup =
  | 'writes'
  | 'retractions'
  | 'reads'
  | 'remember'
  | 'mods'
  | 'sessions'
  | 'maintenance'
  | 'other'

export type TailRow = {
  /** `w<id>`, `r<id>`, `e<epoch>:<seq>`, `c<n>` or `m<n>`; a row with an existing key replaces it. */
  key: string
  /** Epoch milliseconds the row sorts by. */
  ms: number
  /** Breaks a tie in `ms`: writes 0, retractions 1, activity 2, markers 3. */
  rank: number
  /** Breaks a tie in `ms` and `rank`. */
  id: number
  kind: 'write' | 'retraction' | 'event' | 'call' | 'marker'
  group: TailGroup | null
  /** The exact kind, origin or tool name; empty on a marker. */
  label: string
  handle?: string
  /** Everything after the label, without the retracted suffix. */
  text: string
  /** Known to be this session's. */
  mine: boolean
  retracted: boolean
}

export type TailState = {
  /** Whether `/engram-tail` last left the pane open. */
  paneOpen: boolean
  /** Newest first, at most 200. */
  rows: TailRow[]
  /** Write cursor: the store's head at the last read. Absent until the first read. */
  after?: number
  /** Retraction cursor, in the server's unix seconds. */
  closedAfter?: number
  /** Activity feed epoch and the last sequence number seen in it. */
  epoch?: string
  eventAfter?: number
  callSeq: number
  markerSeq: number
  /** Handles known to be this session's, from its own Engram calls and the rows the server marked. */
  handles: string[]
  groups: Record<TailGroup, boolean>
  /** The line shown above the rows; absent when the last request succeeded. */
  status?: string | undefined
}

declare module 'claude-code' {
  interface PluginState {
    engram: {
      shared: SharedState
      lens: LensState
      band: BandState
      toasts: ToastsState
      sentinel: SentinelState
      beliefDiff: BeliefDiffState
      digest: DigestState
      tail: TailState
    }
  }
}
