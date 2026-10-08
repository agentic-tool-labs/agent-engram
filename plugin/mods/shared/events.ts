// The engine refuses one event registered twice in a hooks module unless every registration
// carries a matcher, and every mod loads through the one module in hooks/register.tsx. A mod that
// shares an event with another therefore passes its matcher constant from here. `cwd` is present
// on every session start and the pattern accepts any value, so the matcher selects nothing out.
export const ANY_SESSION_START = { cwd: /.*/ }
