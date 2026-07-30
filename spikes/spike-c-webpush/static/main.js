// D-159 flow state machine.
// States: unsupported | prompt (contextual card) | subscribed | denied.
// The browser permission dialog is only ever triggered from the Enable click.
const $ = (id) => document.getElementById(id);
const logList = $("log");

function log(msg) {
  console.log(`[spike-c] ${msg}`);
  const li = document.createElement("li");
  li.textContent = `${new Date().toLocaleTimeString()} — ${msg}`;
  logList.prepend(li);
}

const STATES = ["unsupported", "prompt", "subscribed", "denied"];
function show(state) {
  for (const s of STATES) $(`state-${s}`).hidden = s !== state;
  log(`state -> ${state}`);
}

function urlBase64ToUint8Array(base64) {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, "+").replace(/_/g, "/"));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

function supported() {
  return "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
}

async function currentSubscription() {
  // getRegistration (not register) so page load never triggers anything.
  const reg = await navigator.serviceWorker.getRegistration();
  return reg ? await reg.pushManager.getSubscription() : null;
}

function showSubscribed(sub) {
  $("endpoint-origin").textContent = new URL(sub.endpoint).origin;
  show("subscribed");
}

async function init() {
  if (!supported()) {
    show("unsupported");
    return;
  }
  log(`Notification.permission = ${Notification.permission}`);
  if (Notification.permission === "denied") {
    show("denied");
    return;
  }
  const sub = await currentSubscription();
  if (sub && Notification.permission === "granted") {
    log("existing push subscription found");
    showSubscribed(sub);
  } else {
    show("prompt");
  }
}

$("enable").addEventListener("click", async () => {
  try {
    log("Enable clicked — registering service worker");
    const reg = await navigator.serviceWorker.register("/sw.js");
    await navigator.serviceWorker.ready;
    log("service worker ready — requesting Notification permission (browser dialog)");
    const permission = await Notification.requestPermission();
    log(`permission result: ${permission}`);
    if (permission !== "granted") {
      show(permission === "denied" ? "denied" : "prompt");
      return;
    }
    const { publicKey } = await (await fetch("/vapid-public-key")).json();
    log("subscribing with VAPID applicationServerKey");
    const sub = await reg.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(publicKey),
    });
    log(`subscribed: ${new URL(sub.endpoint).origin}`);
    const res = await fetch("/subscribe", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(sub),
    });
    log(`POST /subscribe -> ${res.status}`);
    showSubscribed(sub);
  } catch (err) {
    log(`enable failed: ${err.message}`);
    console.error(err);
  }
});

$("unsubscribe").addEventListener("click", async () => {
  try {
    const sub = await currentSubscription();
    if (!sub) {
      log("no subscription to remove");
      show("prompt");
      return;
    }
    const endpoint = sub.endpoint;
    await sub.unsubscribe();
    log("browser subscription removed");
    const res = await fetch("/unsubscribe", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ endpoint }),
    });
    log(`POST /unsubscribe -> ${res.status}`);
    show("prompt");
  } catch (err) {
    log(`unsubscribe failed: ${err.message}`);
    console.error(err);
  }
});

$("send-test").addEventListener("click", async () => {
  try {
    const res = await fetch("/send", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ title: "Session confirmed", body: "Friday 20:00 — Deep Rock Galactic" }),
    });
    const summary = await res.json();
    log(`POST /send -> ${res.status}: delivered ${summary.delivered}/${summary.total}`);
    for (const r of summary.results ?? []) {
      log(`  ${r.endpoint}: ${r.status}${r.statusCode ? ` (${r.statusCode})` : ""}`);
    }
  } catch (err) {
    log(`send failed: ${err.message}`);
    console.error(err);
  }
});

init();
