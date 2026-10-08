import { expect, test } from 'claude-code/testing'
import { once } from './guard'

function counted<T>(answer: () => T) {
  const next = Object.assign(
    (_e: string) => {
      next.calls++
      return answer()
    },
    { calls: 0, signal: undefined as AbortSignal | undefined },
  )
  return next
}

test('next runs once however many times it is called', async () => {
  const next = counted(() => Promise.resolve('core'))
  const go = once(next)
  expect(await go('e')).toBe('core')
  expect(await go('e')).toBe('core')
  expect(next.calls).toBe(1)
})

test('fallback after a call returns that call result without running next again', async () => {
  const next = counted(() => Promise.resolve('core'))
  const go = once(next)
  await go('e')
  expect(await go.fallback('e')).toBe('core')
  expect(next.calls).toBe(1)
})

test('fallback before any call runs next once, and a later call reuses it', async () => {
  const next = counted(() => Promise.resolve('core'))
  const go = once(next)
  expect(await go.fallback('e')).toBe('core')
  expect(await go('e')).toBe('core')
  expect(next.calls).toBe(1)
})

test('a rejecting next is called once and the same rejection comes back from fallback', async () => {
  const boom = new Error('core failed')
  const next = counted(() => Promise.reject(boom))
  const go = once(next)
  await expect(go('e')).rejects.toBe(boom)
  await expect(go.fallback('e')).rejects.toBe(boom)
  expect(next.calls).toBe(1)
})

test('a next that throws synchronously becomes a rejection and is not called again', async () => {
  const boom = new Error('sync')
  const next = counted((): Promise<string> => {
    throw boom
  })
  const go = once(next)
  await expect(go('e')).rejects.toBe(boom)
  await expect(go.fallback('e')).rejects.toBe(boom)
  expect(next.calls).toBe(1)
})

test('what the engine hangs on next, signal included, is kept', () => {
  const signal = new AbortController().signal
  const next = counted(() => Promise.resolve('core'))
  next.signal = signal
  expect(once(next).signal).toBe(signal)
})

test('two wrappers of one next are independent', async () => {
  const next = counted(() => Promise.resolve('core'))
  await once(next)('e')
  await once(next)('e')
  expect(next.calls).toBe(2)
})
