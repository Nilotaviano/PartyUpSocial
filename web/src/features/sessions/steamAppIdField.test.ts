import { describe, expect, it } from 'vitest'
import { extractSteamAppId } from './steamAppIdField'

describe('extractSteamAppId', () => {
  it('extracts the numeric app id from a pasted Steam store URL', () => {
    expect(
      extractSteamAppId('https://store.steampowered.com/app/730/CounterStrike_2/'),
    ).toBe('730')
  })

  it('passes a hand-typed numeric app id through unchanged', () => {
    expect(extractSteamAppId('730')).toBe('730')
  })

  it('passes non-matching input through unchanged', () => {
    expect(extractSteamAppId('abc')).toBe('abc')
  })

  it('passes an empty string through unchanged', () => {
    expect(extractSteamAppId('')).toBe('')
  })
})
