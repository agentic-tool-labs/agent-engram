export type SharedState = {
  /** `undefined` until resolved; `null` when no binary was found. */
  binary: string | null | undefined
  sessionId: string | undefined
  /** Loopback port of the running server, `null` when unknown. */
  port: number | null
  /** Clock time of the last `engram status --json` lookup, 0 before the first. */
  statusAt: number
  /** The server answered 404 to the mod API: no further calls this session. */
  unsupported: boolean
}

declare module 'claude-code' {
  interface PluginState {
    engram: { shared: SharedState }
  }
}
