# D-189 — invite/approval email — design

Status: ratified 2026-08-11 after one adversarial review round (3 lenses, findings applied).
Binds the implementation of PRODUCT_SPEC.md D-189 using the shipped email pipeline.

## 1. Scope — five sites, driven by the dequeued events

Email dispatch keys off the same domain events the notification materializers consume:
`SessionInvitationSent`, `PollInvitationSent`, `SessionJoinRequestAccepted`,
`PollJoinRequestAccepted`. Five use cases dequeue them: **`CreateSessionUseCase`** (creation-time
invitees — the primary invite path), `SessionInvitationUseCases.InviteRegisteredUsersAsync`,
`CreatePollUseCase` (poll invites are creation-time only), and the two
`ApproveJoinRequestAsync`es. Each passes its committed aggregate plus the dequeued event list to
the email service after commit, so any future path raising these events fails loudly in review
rather than silently skipping email. Waitlist promotion stays unwired (owner-rejected).
A Declined invitee who is re-invited gets a fresh `SessionInvitationSent` and therefore a fresh
email each time — accepted: targets are accepted friends only, and the recipient can unsubscribe;
a test pins the behavior.

No new transport: `IEmailDispatcher` → `ChannelEmailDispatcher` → `EmailDispatchHostedService`
(A-016 post-commit). **Fan-out must not block the request thread**: the invite path uses a new
non-blocking `IEmailDispatcher.TryDispatch(EmailMessage)` (`Channel.Writer.TryWrite`) that drops
and Error-logs on a full channel — mail here is best-effort by decision (the A-016 accepted
trade), and an uncancellable `WriteAsync(…, CancellationToken.None)` park behind a slow SMTP
drain is strictly worse than a dropped mail. Channel capacity rises 100 → 500 as a secondary
cushion. Auth flows keep the blocking `DispatchAsync` semantics unchanged.

## 2. The account setting

`User.EmailNotificationsEnabled` (bool, default **true**), a plain aggregate property in the
`ReminderDefault` idiom: `SetEmailNotifications(bool)` guarded by `RequireProfileMutable` for the
settings page. Migration adds `email_notifications_enabled boolean not null default true`.

API: `PUT /api/v1/me/email-notifications` (`RequireOnboarded`, 204/Problem via
`ExecuteProfileMutationAsync`), field added to `MyProfileResponse`, **rerun `npm run gen:api`**.
Web: a fourth section on `NotificationSettingsPage` using the existing `aria-pressed` toggle
pattern, i18n in en/pt-BR/es. The section renders for D-078 social-only accounts too, where it
has no effect (owner's call).

## 3. Unsubscribe token — one per delivered email, all dying together on use

A single per-account hashed token cannot be re-emitted raw into later emails, and re-minting per
send leaves older emails carrying dead links — a user unsubscribing from last week's email would
get a false success. So each **email** carries its own token through the existing
`ISecurityTokenService`/`auth_tokens` machinery: new `AuthTokenPurpose.EmailUnsubscribe = 7`,
CSPRNG, stored hashed, never logged (A-015), TTL 365 days (interpretation note — the spec is
silent on duration; the bound keeps `PurgeAsync` able to reclaim rows; growth is emails/day ×
365 rows, trivially small at current volume). Every delivered email's link works until **any**
one is redeemed; redemption disables the setting and **consumes every outstanding
`EmailUnsubscribe` token for that user** — that is the rotation: forwarded and cached links die
on first use, and the link can only ever disable. Re-enabling is the signed-in toggle.

**The PRODUCT_SPEC.md D-189 row is amended in the same commit** — "per-account token" becomes
"a token per delivered email; redeeming one consumes every outstanding unsubscribe token for the
account" — flagged in the handoff for owner ratification (it preserves every functional promise
of the confirmed text; only the token's cardinality changes).

Two `SecurityTokenService` rules, each pinned by a test:
- `EmailUnsubscribe` must **not** join `IssueAsync`'s consume-outstanding purpose list
  (`SecurityTokenService.cs:39-40`) — issuing email N+1's token must not kill email N's link;
  test: two consecutively issued tokens are both live.
- `RedeemAsync` gains a purpose-scoped bulk consume at **redemption** time (second UPDATE keyed
  on the candidate's `user_id` + purpose, in the same transaction), gated so the six existing
  purposes are byte-identical; test: two outstanding tokens, redeem one, both are dead and the
  setting is off.

Issuance runs in one small post-commit `unitOfWork.ExecuteAsync` (one INSERT per recipient in
one transaction — accepted for ≤50 rows; inserts contend on nothing, so ordering is irrelevant).
The consume path skips lifecycle guards — an unsubscribe must not fail on account state — except
a tombstoned/deleted user, where redemption no-ops behind the same generic outcome.

Token travels in the URL **fragment** (`{PublicBaseUrl}/email-unsubscribe#token=…`), the A-044
convention. Confirm surface: `POST /api/v1/email-notifications/unsubscribe` (no auth, body
`{token}`) — redeem, consume-all, disable; **204 regardless of outcome** except a malformed
body (indistinguishable, the A-064 posture). A dead token means either already-unsubscribed
(the 204 is then truthful) or expired/forged (rare; the generic copy claims no more than "your
preference has been recorded"). It
rides the `default` rate-limit dimension on the `ConfirmAccountDeletion` precedent (both are
unauthenticated token-confirm POSTs); stated here so the choice is deliberate. Web page
`/email-unsubscribe` copies `PublicDeletionConfirmPage.tsx` exactly: hash-read +
`history.replaceState`, no mutation on mount, explicit button fires the POST. There is no GET
route to mutate. Dev note: `LoggingEmailSender` logs full bodies and writes `.eml` drops — in
Development those carry raw tokens; that sink is dev-gated and mail-drop files are not shared.

## 4. Recipient resolution

New Application port `IEmailRecipientReader.GetAsync(IReadOnlyList<UserId>)` →
`EmailRecipient(UserId, Email, DisplayName, Locale?, DateTimeZone?, EmailNotificationsEnabled)`;
Infrastructure implements it as one batched query joining the identity store and `users` (the
50-invitee path must not do 50 round trips). The organizer's id is appended to the same batch to
source `OrganizerName`. Every requested user row returns (the organizer contributes only a
display name — a mail-less organizer must not suppress the batch), but `Email` is null unless
the account holds a **confirmed** address and only non-null emails are mailable: an unconfirmed
address may belong to someone who never consented, and repeated invites would let an attacker
spam a victim address through us (interpretation note — spec is silent; recorded here and in
TRACEABILITY). Null locale falls back through `CanonicalizeLocale` to `en` (D-146); null zone
falls back to UTC. Guests never appear: the events carry registered `UserId`s only (D-150).
The reader is the **only** per-recipient data source — the dispatch path loads no `User` rows
(`IUserRepository` has no batch method and must not be called in a 50-iteration loop).

## 5. Dispatch service

One Application service, `InviteEmailService`, injected into the five use cases and called after
their `unitOfWork.ExecuteAsync` returns success, wrapped in catch-and-log: a post-commit email
failure must never fail the already-committed request. Inputs are the **committed aggregate the
use case already holds and the dequeued events — never a post-commit re-read**, so the D-047
privacy window is commit-to-send only (the accepted, recorded leak). Per call: filter events to
the four types, batch-resolve recipients + organizer via `IEmailRecipientReader`, filter to
enabled-with-confirmed-email, issue tokens (one transaction of INSERTs), `TryDispatch` one
`EmailMessage` per recipient in the recipient's locale. A drop or crash between issuance and
dispatch strands an unused token row until expiry and loses that mail — previously delivered
emails' links stay live, so no recipient is ever left without a working unsubscribe path.

Abuse ceiling: session-write and poll-write at 30/min/user bound a single account to
~1500 mails/min in theory; targets are accepted friends only, and the per-sender outbound
ceiling + suppression list are the already-filed bounce-handling work, not this design.

## 6. Templates and escaping

`EmailTemplate` appends `SessionInvitation = 14`, `PollInvitation = 15`,
`SessionJoinRequestApproved = 16`, `PollJoinRequestApproved = 17` (dense, append-only). Each has
9 embedded files (`{Template}.{en|pt-BR|es}.{subject.txt|text|html}`) — 36 files.
`EmailTemplateRendererTests.CreateMessage`'s shared model dictionary **gains the new keys** (the
enum loops throw on any missing placeholder — they do not pass for free).

Approval copy is **status-neutral**: `Session.ApproveJoinRequest` on a full session seats the
requester on the waitlist while still raising `SessionJoinRequestAccepted` (Session.cs:917), so
the mail says the request was approved and links to the session/poll for current status — it
never claims a seat. Model keys: `RecipientName`, `OrganizerName`, `Title`, `Notes`, `Link`
(detail page), `PublicBaseUrl`, `Token`; session invites add `StartTime` formatted server-side
in the recipient's zone and locale — `PushPayloadFactory`'s private `FormatInstant`/
`ResolveCulture` are **extracted to a shared helper** both call, rather than copied into a
second locale map. `Notes` is always present in the model (empty
string when none — the renderer throws on missing keys and has no conditionals); templates
render it in a visually quoted block attributed to `{{OrganizerName}}`, and an empty block is
the accepted cosmetic cost. Notes ship in full — the accepted permanent leak, recorded in D-189.
Phishing residual, recorded: notes/title are attacker-authored free text (≤500 chars, Trim-only
normalization) delivered in a first-party DKIM-signed message that mail clients autolink; the
quoted-block attribution is the mitigation, the friends-only invite edge is the bound.

Escaping is load-bearing (D-189):
- **Subject**: `Substitute` grows a mode (`Subject | Text | Html`) — subject mode strips
  `\r`/`\n` from every substituted value, then `Render` rejects (throws on) CR/LF in the final
  subject **after the existing `.Trim()`** (every shipped `*.subject.txt` ends in a newline — a
  pre-Trim check would throw for all 14 existing templates): header injection is structurally impossible, the throw is the template-regression
  tripwire (in prod it degrades to a dropped mail + Error log via the pump's catch — the unit
  test is the actual guard). MimeKit's header encoding in `SmtpEmailSender.BuildMessage` is the
  structural backstop. Html mode behavior asserted unchanged by the existing `<Steam>` test.
- **HTML body**: existing `HtmlEncoder` per-placeholder encoding (unchanged).
- **URLs**: no user-authored value is ever substituted inside an `href` — links are built from
  `PublicBaseUrl` + entity GUID + token only. A renderer test asserts the invariant for the four
  new templates.

## 7. Tests (all `_D189` unless noted) and inventories

- Domain: `EmailNotificationsEnabled` default-true; set/disable behavior.
- SecurityTokenService (integration): two consecutively issued `EmailUnsubscribe` tokens are
  both live (issuance never consumes); redeeming one consumes every outstanding one for that
  user; the six existing purposes' redeem behavior is byte-identical.
- Application: each of the five use cases dispatches the right template to the right recipients
  post-commit; setting off → no dispatch; no confirmed email → no dispatch; re-invite of a
  Declined invitee re-mails (pins §1); subject CR/LF strip + final-subject reject (post-Trim);
  no-user-content-in-href; renderer parity rides the `_D060` loop with the extended model.
- Integration: session create with invitees → `CapturingEmailSender.WaitForAsync` asserts
  template, recipient, locale, model; unsubscribe round-trip (POST token → 204 → setting off →
  same token again → still 204, no change); tombstoned-user token → 204, no mutation;
  social-only account toggle round-trip.
- Web: toggle mutate-and-invalidate; unsubscribe page inert on mount, button fires POST; i18n
  parity via the existing suite.
- Inventories (extend, never loosen): `AntiforgeryTests.UnsafeRoutes` gains both new routes;
  `ApiOperationIds` gains both names; both ride the `default` rate-limit dimension so the
  dimension count stays and only the route map grows; `npm run gen:api` after the DTO change.

TRACEABILITY.md D-189 row moves to shipped with the test names; TODO.md item closes with a
pointer here. Out of scope (already filed): bounce/spam-complaint handling, per-sender outbound
ceiling.
