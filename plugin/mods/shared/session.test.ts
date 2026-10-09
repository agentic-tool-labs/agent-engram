import { expect, test } from 'claude-code/testing'
import { forSession } from './session'

type Held = { session?: string; flag: boolean; seen: string[]; other: string }
const INITIAL = { flag: false, seen: [] as string[] }

test('H1: a missing or different session resets the listed fields and stamps the id', () => {
  const stale: Held = { session: 'A', flag: true, seen: ['f1'], other: 'kept' }
  const missing: Held = { flag: true, seen: ['f1'], other: 'kept' }

  expect(forSession(stale, 'B', INITIAL)).toEqual({ session: 'B', flag: false, seen: [], other: 'kept' })
  expect(forSession(missing, 'B', INITIAL)).toEqual({ session: 'B', flag: false, seen: [], other: 'kept' })
})

test('H1: a matching session returns the value untouched', () => {
  const current: Held = { session: 'A', flag: true, seen: ['f1'], other: 'kept' }

  expect(forSession(current, 'A', INITIAL)).toBe(current)
})

test('H1: the input is not mutated', () => {
  const stale: Held = { session: 'A', flag: true, seen: ['f1'], other: 'kept' }

  forSession(stale, 'B', INITIAL)

  expect(stale).toEqual({ session: 'A', flag: true, seen: ['f1'], other: 'kept' })
})
