import { expect, test } from 'claude-code/testing'
import { ANY_PROMPT_SUBMIT, ANY_SESSION_START, ANY_TURN_COMPLETE, ANY_TURN_START } from './events'

// Each constant keys on the one field the engine's types give every occurrence of its event: an
// optional field (agentId, attachments) would skip occurrences without it, and the hook with it.
const ALWAYS_PRESENT = [
  ['session.start', ANY_SESSION_START, 'cwd'],
  ['turn.start', ANY_TURN_START, 'turnId'],
  ['turn.complete', ANY_TURN_COMPLETE, 'turnId'],
  ['prompt.submit', ANY_PROMPT_SUBMIT, 'text'],
] as const

for (const [event, matcher, field] of ALWAYS_PRESENT) {
  test(`${event}: the shared matcher keys on ${field} alone`, () => {
    expect(Object.keys(matcher)).toEqual([field])
  })

  test(`${event}: the shared matcher accepts any value, empty and multi-line included`, () => {
    const pattern = (matcher as Record<string, RegExp>)[field]!
    for (const value of ['', 't-1', 'a/b/c', 'multi\nline', ' \t']) {
      expect(pattern.test(value)).toBe(true)
    }
  })
}
