// D-197 / D-181 idiom (theme.ts, pushSubscription.ts's own precedent): whether THIS browser has
// dismissed a given (profileZone, browserZone) PAIR is per-device browser-local state, never a
// server call. Dismissal is keyed by the pair, not by either zone alone, so a new mismatch (either
// the profile zone or the detected browser zone changing) always re-prompts once on its own.
const DISMISSED_PAIRS_STORAGE_KEY = 'partyup:tz-mismatch-dismissed-pairs'
const PAIR_SEPARATOR = '|'

function pairToken(profileZone: string, browserZone: string): string {
  return `${profileZone}${PAIR_SEPARATOR}${browserZone}`
}

function readDismissedPairs(): string[] {
  if (typeof localStorage === 'undefined') return []
  try {
    const raw = localStorage.getItem(DISMISSED_PAIRS_STORAGE_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((entry): entry is string => typeof entry === 'string') : []
  } catch {
    return []
  }
}

export function hasDismissedTimezoneMismatch(profileZone: string, browserZone: string): boolean {
  return readDismissedPairs().includes(pairToken(profileZone, browserZone))
}

export function markTimezoneMismatchDismissed(profileZone: string, browserZone: string) {
  if (typeof localStorage === 'undefined') return
  const token = pairToken(profileZone, browserZone)
  const pairs = readDismissedPairs()
  if (pairs.includes(token)) return
  localStorage.setItem(DISMISSED_PAIRS_STORAGE_KEY, JSON.stringify([...pairs, token]))
}
