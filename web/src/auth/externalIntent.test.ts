import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { startExternalProviderWithRetry } from './externalIntent'

describe('startExternalProviderWithRetry', () => {
  it('re-mints and retries exactly once for intent_required', async () => {
    const mintIntent = vi.fn()
      .mockResolvedValueOnce({ intent: 'first' })
      .mockResolvedValueOnce({ intent: 'second' })
    const startProvider = vi.fn()
      .mockRejectedValueOnce(new ApiError(400, 'intent_required'))
      .mockResolvedValueOnce(undefined)

    await startExternalProviderWithRetry(mintIntent, startProvider)

    expect(mintIntent).toHaveBeenCalledTimes(2)
    expect(startProvider).toHaveBeenNthCalledWith(1, 'first')
    expect(startProvider).toHaveBeenNthCalledWith(2, 'second')
  })

  it('does not retry another provider error', async () => {
    const mintIntent = vi.fn().mockResolvedValue({ intent: 'first' })
    const startProvider = vi.fn().mockRejectedValue(new ApiError(400, 'provider_unavailable'))

    await expect(startExternalProviderWithRetry(mintIntent, startProvider)).rejects.toMatchObject({ code: 'provider_unavailable' })

    expect(mintIntent).toHaveBeenCalledTimes(1)
    expect(startProvider).toHaveBeenCalledTimes(1)
  })
})
