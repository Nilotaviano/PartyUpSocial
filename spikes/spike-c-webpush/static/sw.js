// Service worker skeleton for Spike C.
self.addEventListener("install", () => {
  console.log("[spike-c][sw] install");
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  console.log("[spike-c][sw] activate");
  event.waitUntil(self.clients.claim());
});

self.addEventListener("push", (event) => {
  let data = { title: "PartyUp", body: "(empty push payload)" };
  try {
    if (event.data) data = event.data.json();
  } catch {
    data.body = event.data.text();
  }
  console.log("[spike-c][sw] push received:", data);
  event.waitUntil(
    self.registration.showNotification(data.title, { body: data.body, data })
  );
});

self.addEventListener("notificationclick", (event) => {
  console.log("[spike-c][sw] notification clicked");
  event.notification.close();
  event.waitUntil(
    (async () => {
      const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
      const client = clients.find((c) => "focus" in c);
      if (client) return client.focus();
      return self.clients.openWindow(event.notification.data?.url ?? "/");
    })()
  );
});
