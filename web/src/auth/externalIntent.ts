import { isApiError } from '../api/client'
import type { components } from '../api/schema'

type IntentResponse = components['schemas']['ExternalIntentResponse']

export async function startExternalProviderWithRetry(
  mintIntent: () => Promise<IntentResponse>,
  startProvider: (intent: string) => Promise<void>,
) {
  const first = await mintIntent()

  try {
    await startProvider(first.intent)
  } catch (error) {
    if (!isApiError(error) || error.code !== 'intent_required') {
      throw error
    }

    const retry = await mintIntent()
    await startProvider(retry.intent)
  }
}
