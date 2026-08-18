// The app's only service worker (served at /sw.js). It carries two unrelated jobs: the
// push/notification handlers the notification feature registers it for, and the minimal
// offline shell precache. Workbox is used ONLY as a build-time manifest injector
// (`self.__WB_MANIFEST` below) — no workbox runtime is imported, the caching logic here
// is hand-rolled so the worker stays readable and dependency-free at runtime.

declare const self: ServiceWorkerGlobalScope & {
  __WB_MANIFEST: Array<{ url: string; revision: string | null }>
}

const PRECACHE_PREFIX = 'partyup-shell-'

/**
 * Prefixes served by the API container, never by this worker: the SPA fallback sits behind
 * them in prod (`MapFallbackToFile` with an /api/v1 404 guard), and a cached or
 * offline-substituted response for any of them would be a wrong answer, not a degraded one.
 */
const PASSTHROUGH_PREFIXES = ['/api', '/g/', '/a/', '/health', '/openapi']

/** Cheap FNV-1a over the injected manifest: the cache name changes exactly when the build does. */
function fingerprint(
  entries: Array<{ url: string; revision: string | null }>,
): string {
  let hash = 0x811c9dc5
  const source = entries
    .map((entry) => `${entry.url}|${entry.revision ?? ''}`)
    .join('\n')
  for (let i = 0; i < source.length; i++) {
    hash ^= source.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193)
  }
  return (hash >>> 0).toString(36)
}

const manifest = self.__WB_MANIFEST
const PRECACHE_NAME = PRECACHE_PREFIX + fingerprint(manifest)
const scope = self.registration.scope
const PRECACHED_PATHS = new Set(
  manifest.map((entry) => new URL(entry.url, scope).pathname),
)
const INDEX_PATH = new URL('index.html', scope).pathname

function isPassthrough(pathname: string): boolean {
  return PASSTHROUGH_PREFIXES.some((prefix) => pathname.startsWith(prefix))
}

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(PRECACHE_NAME)
      .then((cache) => cache.addAll([...PRECACHED_PATHS]))
      .then(() => self.skipWaiting()),
  )
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter(
              (key) => key.startsWith(PRECACHE_PREFIX) && key !== PRECACHE_NAME,
            )
            .map((key) => caches.delete(key)),
        ),
      )
      .then(() => self.clients.claim()),
  )
})

self.addEventListener('fetch', (event) => {
  const request = event.request
  if (request.method !== 'GET') return

  const url = new URL(request.url)
  if (url.origin !== self.location.origin) return
  if (isPassthrough(url.pathname)) return

  // Build assets are content-hashed (or revisioned in the manifest), so a precache hit is
  // always the response this build wants; the network is only the repair path.
  if (PRECACHED_PATHS.has(url.pathname)) {
    event.respondWith(
      caches
        .open(PRECACHE_NAME)
        .then((cache) => cache.match(request))
        .then((hit) => hit ?? fetch(request)),
    )
    return
  }

  // Every other same-origin navigation is an SPA route: online it must reach the server (auth
  // redirects, guest-link handoffs), offline it gets the cached shell to boot the router.
  if (request.mode === 'navigate') {
    event.respondWith(
      fetch(request).catch(() =>
        caches
          .open(PRECACHE_NAME)
          .then((cache) => cache.match(INDEX_PATH))
          .then((hit) => hit ?? Response.error()),
      ),
    )
  }
})

// PHASE7_DESIGN.md §5/§6 (D-147/D-159/D-175): this worker is registered for every production
// visitor at page load (D-218), but a push only ever arrives here for a browser that took the
// explicit opt-in (PushEnableCard / NotificationSettingsPage) — permission and subscription are
// still the user's to grant, and an unsubscribed registration receives nothing. Ruling 5 — a push payload
// never carries more than `GET /notifications` would return the same recipient — so this worker
// only ever renders {title, body, tag, url}, exactly the shape `LoggingPushSender`/`WebPushSender`
// serialize server-side.
self.addEventListener('push', (event) => {
  let data: { title?: string; body?: string; tag?: string; url?: string } = {}
  if (event.data) {
    try {
      data = event.data.json()
    } catch {
      data = {}
    }
  }

  const title = data.title || 'PartyUp'
  const options = {
    body: data.body || '',
    tag: data.tag,
    // Ruling 2: OS-tray collapsing is per-subject `tag`, never a re-alert on an already-shown tag.
    renotify: false,
    data: { url: data.url || '/' },
  }

  event.waitUntil(self.registration.showNotification(title, options))
})

// Focuses an already-open client if one exists, otherwise opens the deep link — never both.
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = (event.notification.data && event.notification.data.url) || '/'

  event.waitUntil(
    self.clients
      .matchAll({ type: 'window', includeUncontrolled: true })
      .then((clientList) => {
        for (const client of clientList) {
          if ('focus' in client) {
            return client.focus()
          }
        }
        if (self.clients.openWindow) {
          return self.clients.openWindow(url)
        }
        return undefined
      }),
  )
})

export {}
