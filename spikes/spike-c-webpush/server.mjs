// Spike C server: static files + subscription store + push sender.
// Plain node:http on :5180. The real sender will be .NET in Phase 7 —
// this only de-risks the client side.
import http from "node:http";
import { existsSync, readFile, readFileSync, writeFileSync } from "node:fs";
import { dirname, extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";
import webpush from "web-push";

const PORT = 5180;
const dir = dirname(fileURLToPath(import.meta.url));
const staticDir = join(dir, "static");
const vapidFile = join(dir, "vapid.json");
const subsFile = join(dir, "subscriptions.json");

if (!existsSync(vapidFile)) {
  console.error("[spike-c] vapid.json not found — run `npm run keys` first.");
  process.exit(1);
}
const vapid = JSON.parse(readFileSync(vapidFile, "utf8"));
webpush.setVapidDetails("mailto:dev@partyup.social", vapid.publicKey, vapid.privateKey);

let subscriptions = existsSync(subsFile)
  ? JSON.parse(readFileSync(subsFile, "utf8"))
  : [];

function saveSubs() {
  writeFileSync(subsFile, JSON.stringify(subscriptions, null, 2) + "\n");
}

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

function sendJson(res, status, obj) {
  res.writeHead(status, { "content-type": "application/json; charset=utf-8" });
  res.end(JSON.stringify(obj));
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

const server = http.createServer(async (req, res) => {
  const { pathname } = new URL(req.url, `http://localhost:${PORT}`);
  console.log(`[spike-c] ${req.method} ${pathname}`);
  try {
    if (req.method === "GET" && pathname === "/vapid-public-key") {
      return sendJson(res, 200, { publicKey: vapid.publicKey });
    }

    if (req.method === "POST" && pathname === "/subscribe") {
      const sub = JSON.parse(await readBody(req));
      if (!sub || typeof sub.endpoint !== "string") {
        return sendJson(res, 400, { error: "subscription must have an endpoint" });
      }
      subscriptions = subscriptions.filter((s) => s.endpoint !== sub.endpoint);
      subscriptions.push(sub);
      saveSubs();
      console.log(`[spike-c] subscribed ${new URL(sub.endpoint).origin} (${subscriptions.length} total)`);
      return sendJson(res, 200, { ok: true, count: subscriptions.length });
    }

    if (req.method === "POST" && pathname === "/unsubscribe") {
      const { endpoint } = JSON.parse(await readBody(req));
      const before = subscriptions.length;
      subscriptions = subscriptions.filter((s) => s.endpoint !== endpoint);
      saveSubs();
      console.log(`[spike-c] unsubscribed (${subscriptions.length} total)`);
      return sendJson(res, 200, { ok: true, removed: before - subscriptions.length, count: subscriptions.length });
    }

    if (req.method === "POST" && pathname === "/send") {
      const { title = "PartyUp", body = "" } = JSON.parse((await readBody(req)) || "{}");
      const payload = JSON.stringify({ title, body, url: "/" });
      const results = [];
      const expired = [];
      for (const sub of subscriptions) {
        const origin = new URL(sub.endpoint).origin;
        try {
          const r = await webpush.sendNotification(sub, payload);
          results.push({ endpoint: origin, status: "delivered", statusCode: r.statusCode });
        } catch (err) {
          if (err.statusCode === 404 || err.statusCode === 410) {
            expired.push(sub.endpoint);
            results.push({ endpoint: origin, status: "expired-removed", statusCode: err.statusCode });
          } else {
            results.push({ endpoint: origin, status: "failed", statusCode: err.statusCode ?? null, error: err.message });
          }
        }
      }
      if (expired.length > 0) {
        subscriptions = subscriptions.filter((s) => !expired.includes(s.endpoint));
        saveSubs();
      }
      const summary = results.map((r) => `${r.endpoint}=${r.status}`).join(", ") || "no subscriptions";
      console.log(`[spike-c] send "${title}": ${summary}`);
      return sendJson(res, 200, {
        delivered: results.filter((r) => r.status === "delivered").length,
        total: results.length,
        results,
      });
    }

    if (req.method === "GET") {
      const rel = pathname === "/" ? "index.html" : pathname.slice(1);
      const filePath = normalize(join(staticDir, rel));
      const type = MIME[extname(filePath)];
      if (!filePath.startsWith(staticDir + sep) || !type) {
        return sendJson(res, 404, { error: "not found" });
      }
      return readFile(filePath, (err, data) => {
        if (err) return sendJson(res, 404, { error: "not found" });
        res.writeHead(200, { "content-type": type });
        res.end(data);
      });
    }

    return sendJson(res, 404, { error: "not found" });
  } catch (err) {
    console.error("[spike-c] request error:", err);
    return sendJson(res, 500, { error: err.message });
  }
});

server.listen(PORT, () => {
  console.log(`[spike-c] listening on http://localhost:${PORT} — ${subscriptions.length} stored subscription(s)`);
});
