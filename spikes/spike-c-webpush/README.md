# Spike C — Web Push prototype

Proves the client side of Web Push: VAPID subscribe + deliver on Chrome / Firefox / Android Chrome, a service worker skeleton, and the D-159 permission flow. Throwaway code — the real sender is .NET in Phase 7; this spike only de-risks the browser.

## Run

```sh
cd spikes/spike-c-webpush
npm install
npm run keys       # writes vapid.json (gitignored); no-op if it already exists
npm start          # server on http://localhost:5180
```

Open http://localhost:5180, click **Enable notifications**, accept the browser dialog. Then either click **Send test push** in the page, or from a second terminal:

```sh
npm run send                                # default title/body
node send.mjs "Session confirmed" "Friday 20:00"
```

`localhost` is a secure context, so no HTTPS is needed on desktop.

Subscriptions persist in `subscriptions.json` (gitignored). The server drops any subscription whose push service answers 404/410 on send (expired).

## Android Chrome

The phone must reach the dev server as `localhost` (secure-context requirement). With USB debugging enabled:

```sh
adb reverse tcp:5180 tcp:5180
```

Then open http://localhost:5180 in Chrome **on the phone** and run the same flow. Note: delivery on Android goes through FCM; the phone needs Google Play services and a network path to FCM.

## D-159 flow (PRODUCT_SPEC.md:644)

The app presents a **contextual in-page card first** ("Get notified when a session is confirmed" + Enable button). The browser's native permission dialog is only invoked from the Enable click — never on page load. Concretely, `main.js` is a four-state machine:

| State | Shown when | Behavior |
|---|---|---|
| `unsupported` | no `serviceWorker` / `PushManager` / `Notification` | explains push is unavailable |
| `prompt` | permission is `default`, or granted but not subscribed | contextual card + **Enable** button; click → register `sw.js` → `Notification.requestPermission()` → `pushManager.subscribe({userVisibleOnly, applicationServerKey})` → `POST /subscribe` |
| `subscribed` | permission granted + live subscription | shows push-service origin, **Send test push**, **Unsubscribe** |
| `denied` | permission is `denied` | explains how to reset in browser site settings |

Page load only calls `serviceWorker.getRegistration()` / `pushManager.getSubscription()` to detect existing state — nothing that can trigger a prompt.

## Test matrix (interactive pass — fill in results)

| Browser | Subscribe (D-159 flow) | Deliver (notification shows) | Click focuses/opens tab | Notes |
|---|---|---|---|---|
| Chromium (Playwright, automated, 2026-07-30) | pass | pass | not testable (no synthetic notification click) | subscribed against real FCM (`fcm.googleapis.com`); `/send` → FCM 201 → sw `push` → notification confirmed via `registration.getNotifications()`; contextual card verified to render before any prompt |
| Chrome desktop (branded, human) | | | | |
| Firefox desktop | | | | |
| Android Chrome (adb reverse) | | | | |

Per cell: pass / fail + anything odd (delay, payload truncation, focus behavior).

**Finding (automated pass):** after a browser-side `sub.unsubscribe()`, FCM kept answering **201** for the dead endpoint (two consecutive sends) instead of 404/410 — expiry is eventually-consistent. Phase 7's send-log/cleanup must not assume a dead subscription fails fast; pair 404/410 handling with client-side `pushsubscriptionchange` + resubscribe-on-load reconciliation.

## Files

- `keys.mjs` — generates VAPID keypair → `vapid.json` (no npx)
- `server.mjs` — node:http on :5180: static files, `GET /vapid-public-key`, `POST /subscribe|/unsubscribe|/send`
- `send.mjs` — CLI trigger for `/send`
- `static/index.html` + `static/main.js` — D-159 state machine + on-page event log
- `static/sw.js` — install/activate (skipWaiting + clients.claim), `push` → showNotification, `notificationclick` → focus/open
