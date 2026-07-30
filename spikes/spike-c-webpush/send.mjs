// CLI: node send.mjs "title" "body" — asks the running server to push to
// every stored subscription and prints the per-endpoint summary.
const [title = "PartyUp test", body = "Hello from spike C"] = process.argv.slice(2);

try {
  const res = await fetch("http://localhost:5180/send", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ title, body }),
  });
  const summary = await res.json();
  console.log(`[spike-c] /send → HTTP ${res.status}`);
  console.log(JSON.stringify(summary, null, 2));
} catch (err) {
  console.error(`[spike-c] could not reach http://localhost:5180 — is the server running? (npm start)`);
  console.error(`[spike-c] ${err.message}`);
  process.exit(1);
}
