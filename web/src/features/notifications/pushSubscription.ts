import { client } from '../../api/client'
import type { components } from '../../api/schema'

export type PushConfig = components['schemas']['PushConfigResponse']
export type CreatePushSubscriptionRequest =
  components['schemas']['CreatePushSubscriptionRequest']

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getPushConfig() {
  const { data } = await client.GET('/api/v1/push/config')
  if (!data) throw new Error('Push config response was empty.')
  return data
}

export async function createPushSubscription(body: CreatePushSubscriptionRequest) {
  const { data } = await client.POST('/api/v1/push/subscriptions', {
    params: { header: csrf },
    body,
  })
  if (!data) throw new Error('Create push subscription response was empty.')
  return data
}

export async function deletePushSubscription(subscriptionId: string) {
  await client.DELETE('/api/v1/push/subscriptions/{subscriptionId}', {
    params: { header: csrf, path: { subscriptionId } },
  })
}

// Ruling 4 / D-181 idiom (theme.ts's own precedent): whether THIS browser has been asked,
// dismissed or enabled is per-device browser-local state, never a server call — a second device
// re-asks independently of this one.
const ASKED_STORAGE_KEY = 'partyup:push-asked'
const SUBSCRIPTION_ID_STORAGE_KEY = 'partyup:push-subscription-id'

export function hasAskedForPushInThisBrowser(): boolean {
  if (typeof localStorage === 'undefined') return false
  return localStorage.getItem(ASKED_STORAGE_KEY) === '1'
}

export function markPushAskedInThisBrowser() {
  if (typeof localStorage === 'undefined') return
  localStorage.setItem(ASKED_STORAGE_KEY, '1')
}

export function getStoredPushSubscriptionId(): string | null {
  if (typeof localStorage === 'undefined') return null
  return localStorage.getItem(SUBSCRIPTION_ID_STORAGE_KEY)
}

export function storePushSubscriptionId(id: string) {
  if (typeof localStorage === 'undefined') return
  localStorage.setItem(SUBSCRIPTION_ID_STORAGE_KEY, id)
}

export function clearStoredPushSubscriptionId() {
  if (typeof localStorage === 'undefined') return
  localStorage.removeItem(SUBSCRIPTION_ID_STORAGE_KEY)
}

export function isPushSupported(): boolean {
  return (
    typeof navigator !== 'undefined' &&
    'serviceWorker' in navigator &&
    typeof window !== 'undefined' &&
    'PushManager' in window &&
    typeof Notification !== 'undefined'
  )
}

export function currentNotificationPermission(): NotificationPermission {
  return typeof Notification === 'undefined' ? 'denied' : Notification.permission
}

export function urlBase64ToUint8Array(base64String: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4)
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/')
  const rawData = atob(base64)
  const outputArray = new Uint8Array(new ArrayBuffer(rawData.length))
  for (let i = 0; i < rawData.length; i++) {
    outputArray[i] = rawData.charCodeAt(i)
  }
  return outputArray
}

export class PushPermissionDeniedError extends Error {
  constructor() {
    super('Push notification permission was not granted.')
    this.name = 'PushPermissionDeniedError'
  }
}

export class PushNotConfiguredError extends Error {
  constructor() {
    super('Push is not configured on the server.')
    this.name = 'PushNotConfiguredError'
  }
}

/**
 * Registers `sw.js` (idempotent — D-218 already registered the same URL at page load in
 * production, and this call is what covers dev and any browser that loaded before it),
 * subscribes through the browser's real `PushManager` against the server's VAPID public key, and
 * POSTs the subscription, storing the returned id for this browser (design §5, D-147/D-159).
 * Throws {@link PushPermissionDeniedError} on anything but an explicit grant (D-175's fallback
 * path covers both an absent `PushManager` and a denied/dismissed permission prompt identically)
 * and {@link PushNotConfiguredError} when the server has no VAPID key configured (§8).
 */
export async function enablePushInThisBrowser(): Promise<{ id: string }> {
  if (!isPushSupported()) {
    throw new PushPermissionDeniedError()
  }

  const permission = await Notification.requestPermission()
  if (permission !== 'granted') {
    throw new PushPermissionDeniedError()
  }

  const config = await getPushConfig()
  if (!config.pushEnabled || !config.vapidPublicKey) {
    throw new PushNotConfiguredError()
  }

  const registration = await navigator.serviceWorker.register('/sw.js')
  await navigator.serviceWorker.ready
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(config.vapidPublicKey),
  })
  const json = subscription.toJSON()

  const created = await createPushSubscription({
    channel: 'webpush',
    endpoint: json.endpoint ?? subscription.endpoint,
    publicKey: json.keys?.p256dh ?? '',
    authSecret: json.keys?.auth ?? '',
  })
  storePushSubscriptionId(created.id)
  return created
}

/**
 * The settings-page "off" path (design §5): unsubscribes the browser's real `PushManager`
 * subscription (best-effort — an already-gone subscription is not an error) and DELETEs the
 * server-side row this browser stored the id for.
 */
export async function disablePushInThisBrowser(): Promise<void> {
  if (isPushSupported()) {
    try {
      const registration = await navigator.serviceWorker.getRegistration('/sw.js')
      const subscription = await registration?.pushManager.getSubscription()
      await subscription?.unsubscribe()
    } catch {
      // Best-effort: the browser-side unsubscribe is not the source of truth — the server DELETE
      // below is what actually stops dispatch, so a browser-API failure here is not fatal.
    }
  }

  const subscriptionId = getStoredPushSubscriptionId()
  if (subscriptionId) {
    await deletePushSubscription(subscriptionId)
  }
  clearStoredPushSubscriptionId()
}

/**
 * Whether this browser currently believes itself subscribed — permission granted and a stored
 * subscription id from a prior successful enable (design §5's per-browser on/off read).
 */
export function isPushEnabledInThisBrowser(): boolean {
  return currentNotificationPermission() === 'granted' && getStoredPushSubscriptionId() !== null
}
