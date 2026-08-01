import { describe, expect, it } from 'vitest'
import { consumeReauthProof, setReauthProof } from './reauthProof'

describe('reauthProof', () => {
  it('consumes a stored proof once', () => {
    setReauthProof('proof')

    expect(consumeReauthProof()).toBe('proof')
    expect(consumeReauthProof()).toBeNull()
  })

  it('returns null when no proof was stored', () => {
    expect(consumeReauthProof()).toBeNull()
  })
})
