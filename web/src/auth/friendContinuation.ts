const usernamePunctuation = new Set(['.', '_', '-'])
const friendCodeAlphabet = /^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{10}$/
const usernameCharacter = /^[\p{L}\p{N}._-]$/u
// D-225: the guest link route the exchange redirect emits — `Guid.ToString("N")`, lowercase.
const guestLinkPath = /^\/guest\/[0-9a-f]{32}$/
const formatCharacter = /\p{Cf}/u
const fixedReturnPaths = new Set([
  '/app',
  '/onboarding',
  '/settings/profile',
  '/settings/account',
])

export function normalizeFriendCode(value: string) {
  return value.trim().replace(/[ -]/g, '').toUpperCase()
}

export function isWellFormedFriendCode(value: string) {
  return friendCodeAlphabet.test(normalizeFriendCode(value))
}

function canonicalUsername(value: string) {
  const username = value.normalize('NFC')
  if (
    username.length < 3 ||
    username.length > 30 ||
    formatCharacter.test(username)
  ) {
    return undefined
  }

  const characters = Array.from(username)
  if (
    characters.length === 0 ||
    usernamePunctuation.has(characters[0]) ||
    usernamePunctuation.has(characters.at(-1) ?? '')
  ) {
    return undefined
  }

  let previous = ''
  let punctuationRun = 0
  for (const character of characters) {
    if (!usernameCharacter.test(character)) {
      return undefined
    }

    if (usernamePunctuation.has(character)) {
      punctuationRun = character === previous ? punctuationRun + 1 : 1
      if (punctuationRun > 2) {
        return undefined
      }
    } else {
      punctuationRun = 0
    }

    previous = character
  }

  return username.toLowerCase()
}

export function validateFriendContinuation(value: string | null | undefined) {
  if (!value || /[?#%\\]/.test(value)) {
    return undefined
  }

  if (value.startsWith('/u/')) {
    const username = canonicalUsername(value.slice(3))
    return username ? `/u/${username}` : undefined
  }

  if (value.startsWith('/add/')) {
    const code = normalizeFriendCode(value.slice(5))
    return friendCodeAlphabet.test(code) ? `/add/${code}` : undefined
  }

  if (guestLinkPath.test(value)) {
    return value
  }

  return undefined
}

export function readFriendContinuation(search: string) {
  return validateFriendContinuation(new URLSearchParams(search).get('next'))
}

export function validateAuthNext(value: string | null | undefined) {
  return value && fixedReturnPaths.has(value)
    ? value
    : validateFriendContinuation(value)
}

export function readAuthNext(search: string) {
  return validateAuthNext(new URLSearchParams(search).get('next'))
}

export function withFriendContinuation(
  path: string,
  continuation: string | undefined,
) {
  return continuation
    ? `${path}?next=${encodeURIComponent(continuation)}`
    : path
}
