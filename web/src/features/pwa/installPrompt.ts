/**
 * Browser-local install state for the D-220 install promotion. Whether THIS browser has
 * dismissed the promotion is per-device state (the `partyup:push-asked` idiom in
 * `notifications/pushSubscription.ts`), never a server call — a second device promotes
 * independently of this one.
 */
const DISMISSED_STORAGE_KEY = 'partyup:install-dismissed'

export function hasDismissedInstallInThisBrowser(): boolean {
  if (typeof localStorage === 'undefined') return false
  return localStorage.getItem(DISMISSED_STORAGE_KEY) === '1'
}

export function markInstallDismissedInThisBrowser() {
  if (typeof localStorage === 'undefined') return
  localStorage.setItem(DISMISSED_STORAGE_KEY, '1')
}

/** Chromium's install event; not in lib.dom, so the two members we use are declared here. */
export interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

/**
 * `beforeinstallprompt` fires once, early, and is only usable if its default was prevented —
 * so it is captured at module scope rather than from a component effect: by the time a banner
 * mounts the event has already come and gone, and a missed capture means no install path at all.
 */
let deferredPrompt: BeforeInstallPromptEvent | null = null
const subscribers = new Set<() => void>()

function emit() {
  for (const notify of subscribers) notify()
}

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (event) => {
    event.preventDefault()
    deferredPrompt = event as BeforeInstallPromptEvent
    emit()
  })
  window.addEventListener('appinstalled', () => {
    deferredPrompt = null
    emit()
  })
}

export function subscribeToInstallPrompt(onStoreChange: () => void) {
  subscribers.add(onStoreChange)
  return () => {
    subscribers.delete(onStoreChange)
  }
}

export function getDeferredInstallPrompt(): BeforeInstallPromptEvent | null {
  return deferredPrompt
}

export type InstallOutcome = 'accepted' | 'dismissed' | 'unavailable'

let installAccepted = false
const acceptedSubscribers = new Set<() => void>()

function emitAccepted() {
  for (const notify of acceptedSubscribers) notify()
}

/**
 * Shared "the native dialog was just accepted" flag, set by `showNativeInstallPrompt` regardless
 * of which caller (InstallBanner or the AccountMenu row) triggered it, so one confirmation notice
 * mounted once at the app root (`InstallAcceptedNotice`) covers both.
 */
export function subscribeToInstallAccepted(onStoreChange: () => void) {
  acceptedSubscribers.add(onStoreChange)
  return () => {
    acceptedSubscribers.delete(onStoreChange)
  }
}

export function getInstallAccepted(): boolean {
  return installAccepted
}

export function clearInstallAccepted() {
  if (!installAccepted) return
  installAccepted = false
  emitAccepted()
}

function markInstallAccepted() {
  installAccepted = true
  emitAccepted()
}

/**
 * Shows the browser's own install dialog. The captured event is single-use: it is released
 * whatever the outcome, which collapses `canPromptNative` so no surface re-offers a dead prompt.
 */
export async function showNativeInstallPrompt(): Promise<InstallOutcome> {
  const event = deferredPrompt
  if (!event) return 'unavailable'
  deferredPrompt = null
  emit()
  await event.prompt()
  const { outcome } = await event.userChoice
  if (outcome === 'accepted') markInstallAccepted()
  return outcome
}

export function isStandaloneDisplay(): boolean {
  if (typeof window === 'undefined') return false
  const iosStandalone =
    typeof navigator !== 'undefined' &&
    (navigator as Navigator & { standalone?: boolean }).standalone === true
  const displayMode =
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(display-mode: standalone)').matches
  return displayMode || iosStandalone
}

/**
 * iOS never fires `beforeinstallprompt`; the only install path is the Safari share sheet, so
 * that platform is detected by user agent and offered instructions instead of a prompt. iPadOS
 * reports a desktop UA, hence the touch-point check. Non-Safari iOS shells (Chrome, Firefox,
 * Edge on iOS) have no "Add to Home Screen" item, so they are excluded rather than told to look
 * for one.
 */
export function isIosSafari(): boolean {
  if (typeof navigator === 'undefined') return false
  const ua = navigator.userAgent
  const ios =
    /iPad|iPhone|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)
  if (!ios) return false
  return /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS|OPiOS|OPT\//.test(ua)
}
