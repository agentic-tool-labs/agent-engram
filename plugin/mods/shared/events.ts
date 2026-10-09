import type { PromptSubmitInput } from 'claude-code'

// The engine refuses a second matcher-less registration of one event across the plugin (all mods
// load as one hooks module); a mod sharing an event passes the constant from here. Each constant
// keys on a field the engine's types document as present on every occurrence of its event, with a
// pattern that accepts any value, so it selects nothing out.
export const ANY_SESSION_START = { cwd: /.*/ }
export const ANY_TURN_START = { turnId: /.*/ }
export const ANY_TURN_COMPLETE = { turnId: /.*/ }
export const ANY_PROMPT_SUBMIT = { text: /.*/ }

/** Whether a prompt.submit input is the person's own: entered in the composer, and not a slash command. */
export const isOwnPrompt = (e: PromptSubmitInput): boolean => e.origin?.kind === 'composer' && !e.text.startsWith('/')
