// PHASE7_DESIGN.md §5/§6 (D-147/D-159/D-175): registered only after the user's explicit opt-in
// (PushEnableCard / NotificationSettingsPage), never on plain page load. Ruling 5 — a push payload
// never carries more than `GET /notifications` would return the same recipient — so this worker
// only ever renders {title, body, tag, url}, exactly the shape `LoggingPushSender`/`WebPushSender`
// serialize server-side.
self.addEventListener('push', (event) => {
  let data = {}
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
