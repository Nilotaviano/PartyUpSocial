// Generates a VAPID keypair and writes it to vapid.json (next to this file).
// npx-free on purpose: uses the web-push library API directly.
import { existsSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import webpush from "web-push";

const dir = dirname(fileURLToPath(import.meta.url));
const file = join(dir, "vapid.json");

if (existsSync(file)) {
  console.log(`[spike-c] ${file} already exists — delete it to regenerate.`);
  process.exit(0);
}

const keys = webpush.generateVAPIDKeys();
writeFileSync(file, JSON.stringify(keys, null, 2) + "\n");
console.log(`[spike-c] wrote ${file}`);
console.log(`[spike-c] public key: ${keys.publicKey}`);
