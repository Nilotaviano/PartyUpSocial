import { describe, expect, it } from 'vitest'
import {
  isWellFormedFriendCode,
  normalizeFriendCode,
  validateFriendContinuation,
  withFriendContinuation,
} from './friendContinuation'

describe('friend continuation D-031', () => {
  it.each([
    ['/u/Player.One', '/u/player.one'],
    ['/u/Álvaro_7', '/u/álvaro_7'],
    ['/add/23456789ab', '/add/23456789AB'],
    ['/add/2345-6789-AB', '/add/23456789AB'],
    ['/add/2345 6789 AB', '/add/23456789AB'],
  ])('canonicalizes %s', (candidate, expected) => {
    expect(validateFriendContinuation(candidate)).toBe(expected)
  })

  it.each([
    null,
    '',
    '/app',
    'https://partyup.social/u/player',
    '//partyup.social/u/player',
    '/u/player/extra',
    '/u/player?tab=friends',
    '/u/player#fragment',
    '/u/player%2Fextra',
    '/u/player\\extra',
    '/u/../player',
    '/u/-player',
    '/u/player...',
    '/add/23456789AB/extra',
    '/add/23456789AI',
  ])('drops hostile or unrelated value %s', (candidate) => {
    expect(validateFriendContinuation(candidate)).toBeUndefined()
  })

  it('normalizes only ASCII spaces and hyphens in a friend code', () => {
    expect(normalizeFriendCode(' 2345-6789 ab ')).toBe('23456789AB')
    expect(isWellFormedFriendCode('2345 6789 ab')).toBe(true)
    expect(isWellFormedFriendCode('2345\u00a06789AB')).toBe(false)
  })

  it('encodes the validated value into the next query parameter', () => {
    expect(withFriendContinuation('/login', '/u/player.one')).toBe(
      '/login?next=%2Fu%2Fplayer.one',
    )
  })
})
