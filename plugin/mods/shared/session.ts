/**
 * A per-session field is valid only while the atom's stored `session` is the current session id.
 * When the ids differ (or none is stored) the listed fields go back to their initial values and the
 * id is stamped; when they match the value comes back as it was.
 *
 * It takes the id as a value, never `$`, because the mod scanner refuses `$` passed across an
 * import. Callers apply it where a per-session field is read or updated, not at `session.start`,
 * which re-fires on every hot reload and would reset a session that has not changed.
 */
export function forSession<T extends { session?: string | undefined }>(value: T, sessionId: string, initial: Partial<T>): T {
  return value.session === sessionId ? value : { ...value, ...initial, session: sessionId }
}
