import type { Register } from 'claude-code'
import { register as shared } from '../mods/shared/index'
import { register as bridge } from '../mods/bridge/index'
import { register as lens } from '../mods/lens/index'
import { register as band } from '../mods/band/index'
import { register as toasts } from '../mods/toasts/index'
import { register as capture } from '../mods/capture/index'
import { register as primer } from '../mods/primer/index'
import { register as sentinel } from '../mods/sentinel/index'
import { register as beliefDiff } from '../mods/belief-diff/index'
import { register as digest } from '../mods/digest/index'
import { register as tail } from '../mods/tail/index'

// A plugin has one hooks module, so every mod registers through this list.
export const register: Register = (on, options) => {
  shared(on, options)
  // Above every mod that hooks a tool call: its guard deny stops the call before any of them runs,
  // and a deny from beneath reaches it as one, so a refused edit is never reported to file-touched.
  bridge(on, options)
  lens(on, options)
  band(on, options)
  // Beneath toasts: the toast reads what this wrote once next(e) resolves.
  toasts(on, options)
  capture(on, options)
  primer(on, options)
  sentinel(on, options)
  beliefDiff(on, options)
  digest(on, options)
  tail(on, options)
}
