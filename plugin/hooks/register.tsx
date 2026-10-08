import type { Register } from 'claude-code'
import { register as shared } from '../mods/shared/index'
import { register as lens } from '../mods/lens/index'
import { register as band } from '../mods/band/index'
import { register as toasts } from '../mods/toasts/index'
import { register as primer } from '../mods/primer/index'
import { register as sentinel } from '../mods/sentinel/index'
import { register as beliefDiff } from '../mods/belief-diff/index'
import { register as digest } from '../mods/digest/index'

// A plugin has one hooks module, so every mod registers through this list.
export const register: Register = (on, options) => {
  shared(on, options)
  lens(on, options)
  band(on, options)
  toasts(on, options)
  primer(on, options)
  sentinel(on, options)
  beliefDiff(on, options)
  digest(on, options)
}
