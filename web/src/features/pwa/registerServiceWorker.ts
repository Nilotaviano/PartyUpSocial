/**
 * D-218: installs the offline shell worker for every visitor at page load, amending the Phase 7
 * push-opt-in-only posture. Failures are swallowed because the app is fully functional without a
 * worker. The push opt-in flow registers this same '/sw.js' URL on grant — registration is
 * idempotent, so the two never conflict.
 *
 * The caller decides *when* to register (src/main.tsx gates on `import.meta.env.PROD`); this
 * module only decides whether the browser can.
 */
export function registerServiceWorker(): void {
  if (!('serviceWorker' in navigator)) {
    return
  }

  void navigator.serviceWorker.register('/sw.js').catch(() => undefined)
}
