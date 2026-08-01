# Admin operations runbook

Phase 2 has no in-app role management (D-177). Admin membership is seeded at startup and revoked only by an explicit operator action. Agents must never execute the SQL in this runbook; an authorized human operator runs it and records the result.

## Bootstrap the production administrator

1. Register and fully onboard the owner account.
2. Obtain its stable domain `UserId` through an operator-controlled source.
3. Configure `Admin__SeedUserIds__0=<user-id>` on the production app container and restart the app.

`Admin:SeedUserIds` is preferred over `Admin:SeedEmails`. The email fallback grants only when the matched address is confirmed. If an unconfirmed match ever occurs, `admin_seed_refusals` records a durable refusal for that account.

**Current deployment gap:** `deploy/compose.yaml` does not pass `Admin__SeedUserIds__0` into `app`. Setting it only in the VM `.env` has no effect until Compose maps the variable into the service. Admin endpoints intentionally return 404 for everyone until the app receives a valid seed id.

## Recover from an email-seed refusal

Do not delete or edit `admin_seed_refusals`. Configure the account's stable id under `Admin:SeedUserIds` and restart the app. Refusal rows bind only the `SeedEmails` fallback; the user-id path is the deliberate operator recovery mechanism and grants idempotently.

## Revoke an admin role (A-039)

First remove the user's `Admin__SeedUserIds__N`/`Admin__SeedEmails__N` configuration so the next restart cannot grant the role again. Then have the authorized database operator run this transaction with the target id substituted:

```sql
BEGIN;

WITH admin_role AS (
    SELECT id
      FROM auth_roles
     WHERE normalized_name = 'ADMIN'
)
DELETE FROM auth_user_roles AS user_role
 USING admin_role
 WHERE user_role.user_id = '<user-id>'::uuid
   AND user_role.role_id = admin_role.id;

UPDATE auth_users
   SET security_stamp = gen_random_uuid()::text
 WHERE id = '<user-id>'::uuid;

COMMIT;
```

The role-row deletion takes effect on the next request because `RequireAdmin` reads `is_admin` from the per-request projection rather than trusting the cookie's role claim. Rotating `security_stamp` also rejects every pre-existing session on its next request, matching the grant path's sign-out behavior. Verify with that user's existing session: an admin endpoint must return 404, then confirm a fresh sign-in also has no admin access.
