# Account-deletion operations runbook

Normal deletion requests use the shipped product paths:

- authenticated users: `POST /api/v1/me/deletion` after A-049 reauthentication;
- public address proof: `/account-deletion` → confirmation email → `/account-deletion/confirm`; and
- public Steam proof for address-less accounts: the same page starts the Steam assertion and returns to the confirmation page.

All variants enter the same seven-day grace. A successful login cancels during grace; the due-deletion service executes elapsed requests.

## Admin visibility

`GET /api/v1/admin/deletion-journal?since=<instant>&limit=<1..1000>` is the A-017 read feed. `POST /api/v1/admin/maintenance/deletion-sweep` ships pending journal rows, purges retained auth tokens and executes accounts already due.

## Legacy/import fallback

PHASE2_DESIGN §7.4 reserves an operator fallback for data that predates the two reachable public identity proofs or arrives by import. The shipped API has no targeted request/execute endpoint for an arbitrary `userId`; the maintenance sweep cannot create a missing pending-deletion state. Therefore the fallback is **not operational in Phase 2**. Do not improvise direct database mutations. Escalate for a product-code path, keep the incident/account restricted as appropriate, and use `RUNBOOK-restore.md` when the case comes from restore replay.
