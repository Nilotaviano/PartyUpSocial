# Avatars — design

Status: ratified 2026-08-11. Supersedes nothing; extends D-032/D-050/D-127/D-188.

An avatar is one of two things: a glyph the user picked from a fixed catalog, or a picture
supplied by an account they linked. Users never upload bytes (D-032). This document binds the
three waves that make that real, the four spec/architecture amendments they need, and the
security posture the picture path is required to hold.

## 1. Amendments this design depends on

| # | Decision |
| --- | --- |
| D-190 | Avatar sources include picture-bearing **auth** accounts (Google), not only gaming platform links. D-050's "linked platform account" reads "linked account". |
| D-191 | A PartyUp user's avatar picture is served only through the first-party proxy, from bytes re-encoded server-side at capture. Upstream URLs are never emitted to clients. **Carve-out:** `SteamUnmatchedFriendResponse.AvatarUrl` (`FriendEndpoints.cs:709`) keeps its raw steamstatic URL — those people have no `User` row and therefore no avatar source. |
| D-192 | The blocked-users list renders a neutral disc seeded from the stored username snapshot and carries no avatar. Amends D-188, which listed it as an avatar surface. |
| A-084 | Avatar proxy tokens are plaintext capability slugs, carved out of A-015. SixLabors.ImageSharp is admitted under A-013 for capture-time re-encoding. |

**A-084 rationale, stated plainly because the consequence is permanent:** an avatar proxy token
is minted once per source row, never rotated, and stored in plaintext. Possession of the URL is
the authorization. An avatar picture is therefore *public-once-seen* — a viewer who was ever
authorized keeps a working URL until the source row is deleted. Deletion of the source row
(unlink, re-link, ban, account deletion) is the sole revocation authority, the way
`guest_links.state` is for A-065. This is a deliberate trade of revocability for a pure,
join-free read path; it is not an oversight, and the picture is the only thing exposed —
never a name, a user id, or a relationship.

D-192 exists because the blocked-users list is the one surface that renders a blocked
counterpart. `IBlockRepository` stores only `BlockedUsernameSnapshot` with no join to the
blocked `User` precisely so a blocker gets no live read. A live avatar there would turn blocking
into a monitoring channel — the D-076 leak class, pointed the other way.

## 2. Wave A — emoji picker and the D-188 fan-out

No new entity, no proxy, no picture pipeline. Every account today has an emoji, so this wave
carries the visible value on its own and ships independently.

### 2.1 Catalog and picker

A shared catalog of ~24 glyphs — one screen, no scrolling. The picker is a 1-D wrapping
`radiogroup` reusing the arrow-key handling already shipped in `ThemeToggle`/`VotingGrid`. A 2-D
roving-tabindex grid is rejected: `WeekGrid` is the only precedent and the catalog is small
enough not to need one. Per-glyph labels are localized in en, pt-BR and es; `i18n.keys.test.ts`
enforces parity.

The catalog is the **server-side allowlist**, validated in the layer that already runs
`ITextPolicy`, so `ChangeAvatarUseCase` and `CompleteOnboardingUseCase` both reject off-catalog
glyphs. `Avatar.FromEmoji` accepts any ≤16-char string today and `ITextPolicy` never covered
this field, which is how `ProfileSettingsPage.tsx:125` was able to ship the literal default
`"game"`.

Onboarding (D-127 step 5) is **emoji-only** and renders no source chooser: `ChangeAvatar`
requires `Lifecycle.Active`, platform linking comes later in the wizard, and no avatar source
can exist at that point.

### 2.2 Migration of existing rows

Existing `avatar_emoji` values are not preserved. Each existing user is assigned a catalog glyph
chosen **deterministically by hashing the user id** — reproducible and re-runnable, not random.
Any `avatar_kind = Platform` row is rewritten to the same deterministic glyph; the shipped
`fk_users_platform_links_avatar_source_platform_link_id` is `ON DELETE SET NULL`, so such rows
may already carry a null source, a state `Avatar` cannot construct.

### 2.3 Fan-out surfaces

`gen:api` is rerun after the DTO changes, and existing inventory assertions are **extended,
never loosened**.

`AvatarResponse` is added to `RosterEntryResponse`, `PollRosterEntryResponse`,
`PollFinalizationPreviewRosterEntryResponse`, `NotificationResponse` and
`SteamMatchedUserResponse`. `BlockedUserResponse` is excluded by D-192 and keeps its three
fields.

### 2.4 Masking — structural, not procedural

The avatar rides `MaskedIdentity`, whose `Anonymous()` already carries a null avatar, so a
mapper cannot forget to mask it. Where a read path does not go through `MaskedIdentity`, the
avatar is nulled **in the same expression that nulls the display name**:
`SessionReadUseCases.cs:73` (`entry with { MaskedForViewer = true, DisplayName = null }`) and
`PollReadUseCases.cs:63-76`. `NotificationResponse` derives its avatar from `row.Actor.Avatar`,
never from a second lookup keyed on `row.ActorUserId`.

The finalization preview is organizer-only to *reach* (`poll.OrganizerId != organizerId` is a 404),
but a registered row in it can still be a blocked counterpart — masking is presentation, not
removal (D-086) — so it is not exempt from masking. `PollFinalizationPreviewUseCase` builds its
own `Viewer` from the organizer's friend/block pairs (mirroring `PollReadUseCases.BuildViewerAsync`)
and nulls `DisplayName`/`Avatar` together for a blocked counterpart, exactly like every other
surface. The wire mapper (`PollFinalizationPreviewRosterEntryResponse.From`) also re-masks `UserId`
and `Avatar` against the same `MaskedForViewer` flag, matching the `RosterEntryResponse`/
`PollRosterEntryResponse` defense-in-depth pattern.

Guest poll rows carry **no avatar at all**. `PollRosterEntryResponse` is shared with
`GuestPollContracts.cs:41,64`, and `GuestPollReadUseCases` builds every row unmasked by
construction (D-150) — guests sit outside the visibility engine, and a guest link has no expiry.

`MaskedParticipant` stays excluded and is unchanged; it was never the leak vector.

Each masked surface gains a `_D188`/`_D076` test asserting the avatar key is **absent**, not
null. The shipped masking tests (`SessionRosterMaskingWireTests.cs:80-94`,
`PollEndpointTests.cs:193-220`) assert only userId and displayName absence and would stay green
through an avatar leak.

## 3. Wave B — avatar sources and the picture proxy

### 3.1 AvatarSource

An `AvatarSource` is an aggregate-internal collection on `User`, one row per linked account that
can supply a picture:

| field | note |
| --- | --- |
| `AvatarSourceId` | GuidV7 |
| `Kind` | `SteamPlatform` \| `GoogleAccount` |
| `Token` | plaintext capability slug, ≥256-bit CSPRNG, minted once, never rotated (A-084), unique index |
| `Bytes` | `bytea`, re-encoded WebP, hard-capped |
| `ContentType` | server-chosen, from a closed allowlist |
| `FetchedAt` | staleness for the refresh job |

`Avatar` becomes `Emoji | External(AvatarSourceId)`, both arms stored as complex-property columns
on the `users` row. This keeps `AvatarResponse.From(Avatar)` (`AuthEndpoints.cs:984`) a pure
static mapper with no I/O — every fan-out read path stays join-free, and `MaskedIdentity` keeps
carrying only the domain VO.

`steam_identities.avatar_url` is **dropped in the same migration**. No gateway read path returns
it, so `AvatarSource` becomes the sole owner of picture provenance and the two stores cannot
drift.

Bytes live in `bytea`, not on disk: `deploy/compose.yaml` mounts no `/data` volume, so a
filesystem cache is destroyed on every deploy *and* escapes `UserDataScope` accounting. A bytea
column cascades with the row inside the same transaction as `MarkDeleted`.

### 3.2 Capture — at write time, never on request

The upstream URL is already in hand inside `CompleteExternalSignInUseCase.UpsertSteamFactsAsync`
and at the Google callback, both already inside a `UnitOfWork`. Capture happens there:

1. https only, host in an explicit allowlist (`*.steamstatic.com`, `*.googleusercontent.com`)
2. a **dedicated** `SocketsHttpHandler` with `AllowAutoRedirect = false`, 3s connect / 5s total,
   `MaxResponseContentBufferSize` set — never the shared singleton `HttpClient`
   (`DependencyInjection.cs:173`), which Steam OpenID verification also uses
3. resolve the host and reject loopback, link-local, private and ULA addresses before connecting
4. read at most 512 KiB, abort past the cap
5. magic-byte sniff against `{PNG, JPEG, WebP}` — **never SVG**
6. re-encode to 128px WebP with ImageSharp and store the result

**The proxy makes no outbound call.** That is the point of capturing at write time: it removes
SSRF, the thundering herd on a cold roster, and the upstream-latency-per-request amplification
in one move. It also satisfies A-064's rule that a token-bearing GET makes no database write.

Steps 1–5 are what keep D-032 honest in effect and not merely in letter. Without re-encoding,
an account owner could set arbitrary bytes as their Steam avatar and obtain a stable
`partyup.social` URL serving them; the re-encode destroys polyglots and caps the served size.

### 3.3 Proxy endpoint

Mapped on the `WebApplication` at its own prefix, `ExcludeFromDescription`, following the
`/g/{token}` precedent (A-064) — **not** inside `/api/v1`. The `/api/v1` `default` rate-limit
dimension is 300/min/IP with `CoversEveryApiOperation: true`; a 150-friend list would burn ~150
permits and 429 the user's real API calls. The proxy gets its own dimension sized for page
loads, and `RateLimitTests`' dimension count and route map move in the same commit.

Response headers: server-chosen `Content-Type` (never the upstream's), `X-Content-Type-Options:
nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Content-Disposition: inline`,
`Cache-Control: private`. Neither `nosniff` nor a CSP exists anywhere in `src/`,
`web/index.html` or `deploy/Caddyfile` today, so this endpoint sets them itself. Every rejection
— unknown token, deleted source, missing bytes — collapses to the same 404, per A-064's
indistinguishable-outcome rule.

The path prefix is added to **both** `FriendPathLogSanitizer.Families` and the two
`deploy/Caddyfile` regexps (`request>uri` and `Location`) in the same commit as the route.
Redaction is opt-in per prefix in two independent places; without both, the token is written to
logs in cleartext twice.

### 3.4 Lifecycle

`User.RemoveAvatarSource(AvatarSourceId)` is added to the aggregate: it removes the row and, if
the avatar draws from it, resets to `Avatar.Fallback`. `UnlinkPlatform` delegates to it, and
`UnlinkAuthMethodUseCase`'s Google branch calls it inside its existing `UnitOfWork` — today that
branch never touches the aggregate, so a Google-sourced avatar would dangle after unlink.

Deletion: `DeletionExecutor` reports the source rows and `DeletionResultApplier` deletes them
inside the deletion transaction, so purge accounting stays honest.

Refresh: a daily Hangfire job (`Cron.Daily`, the push-send-log-prune cadence — not one of the
five minutely jobs) re-reads Steam avatars from `steamId64` via `ISteamProfileClient` with a
bounded batch size in the `DeletionSweepOptions` idiom. Google is capture-time only; its picture
claim is read at sign-in.

### 3.5 Client

`AvatarDisc` gains an `<img>` arm inside its existing `aria-hidden` span — it stays decoration
and never becomes a control's accessible name. It takes an `onError` fallback to the glyph:
without one, a failed image renders `'●'`, which is exactly the masked treatment, and a
transient failure would read as an anonymized user.

The settings picker gains a source chooser listing the user's avatar sources alongside the
emoji catalog. Onboarding does not (§2.1).

## 4. Wave C — Google picture capture

`GoogleAuthSetup.cs` requests the profile scope and maps the `picture` claim; the external-login
callback captures it into an `AvatarSource` of kind `GoogleAccount`. Folded into wave B if it
does not delay it; otherwise its own wave. Nothing else depends on it.

## 5. Test obligations

- `_D190` — a Google-sourced avatar survives sign-in and is served by the proxy.
- `_D191` — no response body anywhere emits an upstream avatar URL, except the named
  `SteamUnmatchedFriendResponse` carve-out.
- `_D192` — `BlockedUserResponse` carries no avatar property.
- `_D188`/`_D076` — the avatar key is **absent** on every masked roster row, on every guest poll
  row, and on `MaskedParticipant`.
- Capture rejects SVG, oversize bodies, off-allowlist hosts and redirects.
- The proxy returns an identical 404 for unknown token, deleted source and missing bytes.
- `FriendPathLogSanitizer` rewrites the proxy path.
