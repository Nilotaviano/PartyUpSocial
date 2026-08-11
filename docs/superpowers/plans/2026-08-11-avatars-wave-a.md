# Avatars Wave A Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a curated 24-glyph emoji avatar picker and render the D-050 avatar on every D-188 Tier-2 surface, with blocked-pair masking made structural rather than per-mapper.

**Architecture:** A closed emoji catalog lives in Application (`AvatarCatalog`) as the server-side allowlist and in `web/src/features/profile/avatarCatalog.ts` as the picker's render order; the two are pinned to each other by an ordinal test, and a migration deterministically reassigns every existing user a catalog glyph. The existing `AvatarResponse` is fanned out to five response types — no new entity, no proxy, no `<img>` arm, all of which are wave B. Masking rides `MaskedIdentity`, whose `Anonymous()` already carries a null avatar, so a mapper cannot forget it; where a read path bypasses `MaskedIdentity`, the avatar is nulled in the same expression that nulls the display name.

**Tech Stack:** .NET 10 · EF Core + Npgsql + NodaTime · Postgres · React 19 + TypeScript + Vite · TanStack Query · i18next (en, pt-BR, es) · Vitest · xUnit · Playwright + axe-core.

## Global Constraints

- Build is 0 warnings (`TreatWarningsAsErrors`). `dotnet build PartyUp.sln`.
- Unit lane: `dotnet test PartyUp.sln --filter "Category!=Integration"`. Full suite incl. integration (MANDATORY before any push): `wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts"` — redirect to a log file, never pipe into the tool call.
- Web: from `web/`, `npm run lint && npm run build && npm test`; single file `npx vitest run <path>`.
- `npm run gen:api` regenerates `web/src/api/schema.d.ts` from a **running** API on `:5000` — start `dotnet run --project src/PartyUp.Api` in a separate terminal first. Never hand-edit `schema.d.ts`.
- Dependency rule: `Domain → nothing` (NodaTime only) · `Application → Domain` · `Infrastructure → Application, Domain` · `Api → all`.
- NodaTime everywhere in Domain/Application; `IClock` injected; BCL `DateTime`/`DateTimeOffset` forbidden there.
- Tests proving a spec rule carry its D-number in the name (`..._D188`); keyboard-behaviour tests carry `_APG`.
- Property-inventory assertions are **extended, never loosened**.
- All user-facing strings go through i18next in en, pt-BR and es from the first commit; `i18n.keys.test.ts` enforces key parity mechanically.
- Central Package Management: versions only in `Directory.Packages.props`.
- No changelog comments in code — rationale goes in the commit message. Conventional Commits, imperative, subject ≤72 chars, no session trailer.
- Emoji glyphs in source are written as explicit codepoint escapes (`"\U0001F3AE"` in C#, `'\u{1F3AE}'` in TS, `E'\U0001F3AE'` in SQL), never pasted literals.

**Wave scope:** this plan is wave A of `AVATARS_DESIGN.md`. `AvatarSource`, the picture proxy, the `AvatarDisc` `<img>` arm and Google capture are waves B and C and are explicitly out of scope here. D-192 excludes the blocked-users list; `MaskedParticipant` and friend-request cards stay avatar-free.

**Known-red window:** the allowlist lands in Task 1 but the picker that satisfies it lands in Task 4, so the opt-in browser lane is knowingly red across Tasks 1–3. `dotnet test PartyUp.sln` stays green throughout (the lane is opt-in via `-p:PartyUpBrowserTests=true`). **Nothing in Tasks 1–3 may be pushed alone** — the push gate is Task 4.

---

### Task 1: Server-side emoji catalog and allowlist validation

**Files:**
- Create: `src/PartyUp.Application/Profile/AvatarCatalog.cs`
- Create: `web/src/features/profile/avatarCatalog.ts`
- Modify: `src/PartyUp.Application/Onboarding/ValidateProfileDraftUseCase.cs:1-5` (usings) and `:57-66` (`ToAvatar`)
- Modify: `src/PartyUp.Application/Profile/ChangeAvatarUseCase.cs:10-29`
- Modify (shipped fixtures that post an off-catalog glyph and assert success): `tests/PartyUp.Application.Tests/Onboarding/CompleteOnboardingUseCaseTests.cs:23,84`; `tests/PartyUp.Api.IntegrationTests/RegistrationFlowTests.cs:63`; `tests/PartyUp.Api.IntegrationTests/UsernameClaimConcurrencyTests.cs:111`
- Test: `tests/PartyUp.Application.Tests/Profile/AvatarCatalogTests.cs`

**Interfaces:**
- Consumes (already in the repo, verified): `Avatar.FromEmoji(string? emoji)` and `Avatar.Emoji` (`src/PartyUp.Domain/Users/Avatar.cs`); `AvatarDraft(string Kind, string? Emoji, Guid? PlatformLinkId)` (`ValidateProfileDraftUseCase.cs:118`); `ProfileMutationResult.Failed(string code, Instant? retryAt = null)` (`src/PartyUp.Application/Profile/ChangeUsernameUseCase.cs:81`); `DomainException(DomainError.ValidationFailed, string)`.
- Produces: `PartyUp.Application.Profile.AvatarCatalog` — `public static ImmutableArray<string> Glyphs { get; }`, `public static bool Contains(string glyph)`. Task 2 and the API tasks depend on these exact names.
- Produces: `AVATAR_CATALOG` in `web/src/features/profile/avatarCatalog.ts` — `readonly { glyph: string; slug: string }[]`, 24 entries in `AvatarCatalog.Glyphs` order. Tasks 3, 4 and 6 import it by that path and name, and the `avatar` i18n namespace keys its labels off the slugs. It ships in this task, not a later one, so the C# array and the TypeScript array land in one commit and their shared order is fixed once.

**Error shape actually used by the two paths (read, not guessed):**
- Onboarding (`POST /api/v1/onboarding/profile` via `ValidateProfileDraftUseCase.Execute`, `:31-40`): a `DomainException` out of `ToAvatar` is caught and turned into `errors["avatar"] = ["errors.avatar.invalid"]`, surfaced inside `ProfileDraftValidation.Errors` / `OnboardingCompletionResult.Invalid`. **Unchanged — an off-catalog glyph reuses that existing key.**
- `PUT /api/v1/me/avatar` (`MeEndpoints.ChangeAvatarAsync:207-217` → `ExecuteProfileMutationAsync` → `ProfileMutationProblem`, `MeEndpoints.cs:712-723`): a `ProfileMutationResult` whose `Code` starts with `"errors."` already maps to **400 Bad Request** with `code` in the problem body (`MeEndpoints.cs:719-720`). So this task returns `ProfileMutationResult.Failed("errors.avatar.invalid")` — the same key the onboarding path emits and a status the endpoint already produces. (Today an invalid avatar escapes as an unhandled `DomainException` caught by `DomainExceptionMapper`, giving 400 with `code: "validation_failed"`; folding it into `ProfileMutationResult` makes both paths speak one key. No existing test asserts the old code — verified: nothing under `tests/` references `me/avatar` except `AntiforgeryTests.cs:109`, which only checks CSRF.)

**Shipped fixtures the allowlist breaks (verified by reading them, all fixed in Step 7):**
Each one carries the same off-catalog glyph, U+1F642 (slightly smiling face), written in the shipped files as a raw literal.

- `tests/PartyUp.Application.Tests/Onboarding/CompleteOnboardingUseCaseTests.cs:23` and `:84` build `new AvatarDraft("emoji", "<U+1F642>", null)`. `CompleteOnboardingUseCase.ExecuteAsync` runs `ValidateProfileDraftUseCase.Execute` first and returns `OnboardingCompletionResult.Invalid` before it ever reaches `ToAvatar` (`CompleteOnboardingUseCase.cs:43`), so `:84`'s `CompleteDraft` — used by `CompleteOnboardingUseCaseTests` and by `SocialOnlyAccountTests` in the same file (`:94`) — starts failing the moment the allowlist lands. This is a certainty, not a risk.
- `tests/PartyUp.Api.IntegrationTests/RegistrationFlowTests.cs:63` POSTs `avatar = new { kind = "emoji", emoji = "<U+1F642>" }` to `/api/v1/onboarding/complete` and asserts `HttpStatusCode.OK` at `:68`.
- `tests/PartyUp.Api.IntegrationTests/UsernameClaimConcurrencyTests.cs:111` POSTs the same off-catalog glyph from its shared `CompleteAsync` helper, whose callers assert a successful claim.
- `tests/PartyUp.Api.IntegrationTests/AntiforgeryTests.cs:101` (`POST /api/v1/onboarding/complete`) and `:110` (`PUT /api/v1/me/avatar`) need **no** change: the first sweep sends the auth cookie without the antiforgery token and asserts `400 antiforgery_failed`, the second sends the token without the cookie and asserts `401 unauthorized` (`AntiforgeryTests.cs:400-435`). Neither request is ever authorized, so the body never reaches avatar validation. Leave both literals alone.
- `Avatar.FromEmoji(...)` calls elsewhere under `tests/` (e.g. `PartyUpApiFactory.cs:99`, `GameSessionEndpointTests.cs:1466`) construct the value object directly and bypass `ToAvatar`, which is where the allowlist lives. They stay green and must not be touched.

**Browser lane: knowingly red from this commit until Task 4.** `web/src/features/onboarding/OnboardingWizard.tsx:202,206` still offers the three placeholder options `'game' | 'dice' | 'arcade'` and defaults to the literal string `'game'`; `tests/PartyUp.BrowserTests/PlaywrightFixture.cs:544-551` completes the avatar step by accepting that default and clicking `Continue`, so `RegisterAndOnboardAsync` submits `emoji: "game"` and onboarding now fails. No fixture-only edit fixes it — every option the old DOM offers is off-catalog, so clicking a different radio submits an equally invalid glyph — and the wizard's option list is Task 4's file. Accept the red and gate on it:
- The lane is opt-in (`tests/PartyUp.BrowserTests/PartyUp.BrowserTests.csproj` defaults `PartyUpBrowserTests` to `false`, so `IsTestProject` is false), so `dotnet test PartyUp.sln` — the WSL pre-push gate — discovers zero tests there and stays green across Tasks 1-3.
- CI's `browser` job (`.github/workflows/ci.yml:53`, `needs: dotnet`) runs only on `push`/`pull_request` against `main`, so it only ever sees the tree at push time.
- **Push gate: nothing in Tasks 1-3 may be pushed on its own.** The first push happens after Task 4 lands the real picker, and it must be preceded by Task 4's browser-lane run (`web-picker.md` Task 4, Step 10), which is what turns the lane green again.

- [ ] **Step 1: Write the failing test**

Create `tests/PartyUp.Application.Tests/Profile/AvatarCatalogTests.cs`:

```csharp
using NodaTime;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Profile;

public sealed class AvatarCatalogTests
{
    private const string OffCatalogGlyph = "\U0001F346";

    [Fact]
    public void Catalog_HoldsTwentyFourGlyphs_D050()
    {
        Assert.Equal(24, AvatarCatalog.Glyphs.Length);
        Assert.Equal(AvatarCatalog.Glyphs.Length, AvatarCatalog.Glyphs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("\U0001F3AE", AvatarCatalog.Glyphs[0]);
        Assert.Equal("\U0001F579\uFE0F", AvatarCatalog.Glyphs[2]);
        Assert.Equal("\u265F\uFE0F", AvatarCatalog.Glyphs[5]);
        Assert.False(AvatarCatalog.Contains(OffCatalogGlyph));
    }

    [Fact]
    public void Validate_AcceptsEveryCatalogGlyph_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        foreach (string glyph in AvatarCatalog.Glyphs)
        {
            ProfileDraftValidation validation = validator.Execute(
                new ProfileDraft("player", "Player", new AvatarDraft("emoji", glyph, null), "America/Fortaleza", "en"));

            Assert.True(validation.IsValid, $"catalog glyph {glyph} was rejected.");
        }
    }

    [Fact]
    public void Validate_RejectsAnOffCatalogGlyph_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        ProfileDraftValidation validation = validator.Execute(
            new ProfileDraft("player", "Player", new AvatarDraft("emoji", OffCatalogGlyph, null), "America/Fortaleza", "en"));

        Assert.Equal(["errors.avatar.invalid"], validation.Errors["avatar"]);
    }

    [Fact]
    public async Task ChangeAvatar_RejectsAnOffCatalogGlyph_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("emoji", OffCatalogGlyph, null),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("errors.avatar.invalid", result.Code);
        Assert.Equal("\U0001F642", user.Avatar!.Value.Emoji);
    }

    [Fact]
    public async Task ChangeAvatar_AcceptsACatalogGlyph_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("emoji", AvatarCatalog.Glyphs[3], null),
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(AvatarCatalog.Glyphs[3], user.Avatar!.Value.Emoji);
    }

    private static User ActiveUser(IClock clock)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("\U0001F642"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        return user;
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/PartyUp.Application.Tests/PartyUp.Application.Tests.csproj --filter "FullyQualifiedName~AvatarCatalogTests"`

Expected: FAIL at compile time — `error CS0103: The name 'AvatarCatalog' does not exist in the current context` (repeated per usage). `AvatarCatalog.cs` does not exist yet.

- [ ] **Step 3: Create the catalog**

Create `src/PartyUp.Application/Profile/AvatarCatalog.cs`:

```csharp
using System.Collections.Immutable;

namespace PartyUp.Application.Profile;

/// <summary>
/// AVATARS_DESIGN.md §2.1: the closed 24-glyph catalog is the server-side allowlist for emoji
/// avatars (D-050). It lives in Application, not Domain: <see cref="PartyUp.Domain.Users.Avatar"/>
/// documents charset policy as application-layer, and Domain may reference nothing but NodaTime.
/// The order is the picker's render order and the migration's hash-index order — do not reorder.
/// </summary>
public static class AvatarCatalog
{
    public static ImmutableArray<string> Glyphs { get; } =
    [
        "\U0001F3AE", // gameController
        "\U0001F3B2", // dice
        "\U0001F579\uFE0F", // joystick
        "\U0001F47E", // alien
        "\U0001F0CF", // jokerCard
        "\u265F\uFE0F", // chessPawn
        "\U0001F3AF", // dart
        "\U0001F3C6", // trophy
        "\U0001F680", // rocket
        "\U0001F409", // dragon
        "\U0001F98A", // fox
        "\U0001F419", // octopus
        "\U0001F427", // penguin
        "\U0001F989", // owl
        "\U0001F41D", // bee
        "\U0001F335", // cactus
        "\U0001F344", // mushroom
        "\U0001F319", // moon
        "\u2B50", // star
        "\U0001F525", // fire
        "\u26A1", // bolt
        "\U0001F3A7", // headphones
        "\U0001F355", // pizza
        "\u2615", // coffee
    ];

    public static bool Contains(string glyph) => Glyphs.Contains(glyph, StringComparer.Ordinal);
}
```

Every entry is a codepoint escape rather than a pasted glyph: two of them (joystick, chess pawn) carry an invisible U+FE0F variation selector that a copy-paste silently drops, and the rest are surrogate pairs a mis-decoded editor mangles just as silently. Indices 0, 2 and 5 are asserted in the test. The trailing comments are the slugs, and they must stay identical to the `slug` values in `web/src/features/profile/avatarCatalog.ts` (Step 4) and to the `avatar` i18n keys, so a reorder that misses one is visible in review.

- [ ] **Step 4: Create the web catalog beside it**

The picker (Task 3), the wizard and the profile page (Task 4) and the i18n label check (Task 6) all import this module, and the `avatar` namespace keys its labels off these slugs. It ships here, in the same commit as the C# array, so the two are written from one source and their shared order is fixed once.

Create `web/src/features/profile/avatarCatalog.ts`:

```ts
/**
 * AVATARS_DESIGN.md §2.1 (D-050): the closed 24-glyph catalog, mirroring
 * `AvatarCatalog.Glyphs` (`src/PartyUp.Application/Profile/AvatarCatalog.cs`) index for index.
 * The server rejects any glyph outside it, so neither the order nor the glyphs may drift.
 * `slug` is never rendered: it is the i18n key suffix, `t('avatar:catalog.<slug>')`.
 */
export const AVATAR_CATALOG = [
  { glyph: '\u{1F3AE}', slug: 'gameController' },
  { glyph: '\u{1F3B2}', slug: 'dice' },
  { glyph: '\u{1F579}\uFE0F', slug: 'joystick' },
  { glyph: '\u{1F47E}', slug: 'alien' },
  { glyph: '\u{1F0CF}', slug: 'jokerCard' },
  { glyph: '\u265F\uFE0F', slug: 'chessPawn' },
  { glyph: '\u{1F3AF}', slug: 'dart' },
  { glyph: '\u{1F3C6}', slug: 'trophy' },
  { glyph: '\u{1F680}', slug: 'rocket' },
  { glyph: '\u{1F409}', slug: 'dragon' },
  { glyph: '\u{1F98A}', slug: 'fox' },
  { glyph: '\u{1F419}', slug: 'octopus' },
  { glyph: '\u{1F427}', slug: 'penguin' },
  { glyph: '\u{1F989}', slug: 'owl' },
  { glyph: '\u{1F41D}', slug: 'bee' },
  { glyph: '\u{1F335}', slug: 'cactus' },
  { glyph: '\u{1F344}', slug: 'mushroom' },
  { glyph: '\u{1F319}', slug: 'moon' },
  { glyph: '\u2B50', slug: 'star' },
  { glyph: '\u{1F525}', slug: 'fire' },
  { glyph: '\u26A1', slug: 'bolt' },
  { glyph: '\u{1F3A7}', slug: 'headphones' },
  { glyph: '\u{1F355}', slug: 'pizza' },
  { glyph: '\u2615', slug: 'coffee' },
] as const
```

The `\u{...}` form is ES2015 codepoint-escape syntax and `web/tsconfig.app.json` targets `es2023`, so it compiles as written. The escapes exist for the same reason the C# ones do: a pasted glyph drops U+FE0F or gets mangled by a mis-decoded editor, and one codepoint of drift here is a 400 from the server on every save. Slug order and spelling must match the trailing comments in `AvatarCatalog.cs` exactly.

Nothing imports the module yet, so the check that matters now is that it type-checks and lints:

```sh
cd web && npm run lint && npm run build
```

Expected: PASS. `noUnusedLocals` flags unused locals, not an unreferenced export, so a catalog with no consumers until Task 3 is clean.

- [ ] **Step 5: Wire the allowlist into the single parse point**

Edit `src/PartyUp.Application/Onboarding/ValidateProfileDraftUseCase.cs`. Add the namespace import to the using block at `:1-5`:

```csharp
using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
```

Replace `:57-66` with:

```csharp
    internal static Avatar ToAvatar(AvatarDraft draft) =>
        draft.Kind.Trim().ToLowerInvariant() switch
        {
            "emoji" => FromCatalogEmoji(draft.Emoji),
            "platform" when draft.PlatformLinkId is { } id =>
                Avatar.FromPlatformLink(PlatformLinkId.From(id)),
            _ => throw new DomainException(
                DomainError.ValidationFailed,
                "The avatar shape is invalid."),
        };

    /// <summary>
    /// <see cref="Avatar.FromEmoji"/> trims, so the catalog check runs on the stored glyph.
    /// </summary>
    private static Avatar FromCatalogEmoji(string? emoji)
    {
        Avatar avatar = Avatar.FromEmoji(emoji);
        return AvatarCatalog.Contains(avatar.Emoji)
            ? avatar
            : throw new DomainException(
                DomainError.ValidationFailed,
                "The avatar glyph is not in the catalog.");
    }
```

This is the only avatar parse point in the application layer: `CompleteOnboardingUseCase.cs:41` and `ChangeAvatarUseCase.cs:15` both call it, so onboarding is covered with no further change (`Execute` at `:31-40` already converts the throw to `errors.avatar.invalid`).

- [ ] **Step 6: Turn the mutation path's throw into the 400 the endpoint already maps**

Replace the body of `src/PartyUp.Application/Profile/ChangeAvatarUseCase.cs:10-29` with:

```csharp
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        AvatarDraft draft,
        CancellationToken cancellationToken)
    {
        Avatar avatar;
        try
        {
            avatar = ValidateProfileDraftUseCase.ToAvatar(draft);
        }
        catch (DomainException)
        {
            return Task.FromResult(ProfileMutationResult.Failed("errors.avatar.invalid"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.ChangeAvatar(avatar);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
```

`MeEndpoints.ProfileMutationProblem` (`:716-722`) sends any `errors.`-prefixed code as 400 — no endpoint change is needed.

- [ ] **Step 7: Repoint the three shipped fixtures that post an off-catalog glyph**

Three literals, all U+1F642, all replaced with the catalog's first glyph. Write the replacement as an escape, not a pasted emoji.

In `tests/PartyUp.Application.Tests/Onboarding/CompleteOnboardingUseCaseTests.cs`, at `:23` (inside `Execute_ActivatesTheAccountOnlyWithACompleteProfile_D127`) and at `:84` (inside the shared `CompleteDraft` helper that `SocialOnlyAccountTests` also uses), change both avatar drafts to:

```csharp
                new AvatarDraft("emoji", "\U0001F3AE", null),
```

In `tests/PartyUp.Api.IntegrationTests/RegistrationFlowTests.cs:63` and
`tests/PartyUp.Api.IntegrationTests/UsernameClaimConcurrencyTests.cs:111`, change the onboarding body's avatar to:

```csharp
                avatar = new { kind = "emoji", emoji = "\U0001F3AE" },
```

Nothing else moves. In particular do not "fix" `AntiforgeryTests.cs:101`/`:110` (never authorized, never validated) and do not loosen the catalog to re-admit U+1F642 — AVATARS_DESIGN.md §2.1 fixes the set at 24.

- [ ] **Step 8: Run tests**

Unit lane first, on the Windows host:

```sh
dotnet build PartyUp.sln
dotnet test PartyUp.sln --filter "Category!=Integration"
```

Expected: build with 0 warnings; PASS — `Catalog_HoldsTwentyFourGlyphs_D050`, `Validate_AcceptsEveryCatalogGlyph_D050`, `Validate_RejectsAnOffCatalogGlyph_D050`, `ChangeAvatar_RejectsAnOffCatalogGlyph_D050`, `ChangeAvatar_AcceptsACatalogGlyph_D050`, and `CompleteOnboardingUseCaseTests` + `SocialOnlyAccountTests` green again on the Step 7 literals.

Then the integration lane, which is the only place the two HTTP onboarding fixtures run and therefore the only run that can see the breakage Step 7 fixes (Docker lives in WSL; redirect to a log rather than piping):

```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests/PartyUp.Api.IntegrationTests.csproj -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~RegistrationFlowTests|FullyQualifiedName~UsernameClaimConcurrencyTests|FullyQualifiedName~AntiforgeryTests' > /tmp/avatar-catalog-int.log 2>&1; tail -40 /tmp/avatar-catalog-int.log"
```

Expected: PASS — `RegistrationFlowTests` still reaches `200 OK` on `/api/v1/onboarding/complete`, `UsernameClaimConcurrencyTests` still claims, and `AntiforgeryTests` is untouched proof that the CSRF sweep never depended on the avatar body.

The browser lane is knowingly red here and is not run; see the note above Step 1.

- [ ] **Step 9: Commit**

```bash
git add src/PartyUp.Application/Profile/AvatarCatalog.cs \
        src/PartyUp.Application/Onboarding/ValidateProfileDraftUseCase.cs \
        src/PartyUp.Application/Profile/ChangeAvatarUseCase.cs \
        web/src/features/profile/avatarCatalog.ts \
        tests/PartyUp.Application.Tests/Profile/AvatarCatalogTests.cs \
        tests/PartyUp.Application.Tests/Onboarding/CompleteOnboardingUseCaseTests.cs \
        tests/PartyUp.Api.IntegrationTests/RegistrationFlowTests.cs \
        tests/PartyUp.Api.IntegrationTests/UsernameClaimConcurrencyTests.cs
git commit -m "feat(profile): enforce the 24-glyph avatar catalog server-side

AVATARS_DESIGN.md 2.1 (D-050) closes the emoji set. ToAvatar is the one
parse point both onboarding and PUT /api/v1/me/avatar go through, so the
allowlist lands there and both paths now speak a single key,
errors.avatar.invalid, which the endpoint already maps to 400. The
TypeScript catalog ships in the same commit so the two arrays cannot be
ordered independently. Fixtures that posted an off-catalog glyph move to
the catalog first entry."
```

---

### Task 2: Reassign every existing user's emoji deterministically in a migration

**Files:**
- Create: `src/PartyUp.Infrastructure/Migrations/<UTC-STAMP>_AssignCatalogAvatars.cs` (generator-named, e.g. `20260811103000_AssignCatalogAvatars.cs`)
- Create (generator output, commit it): `src/PartyUp.Infrastructure/Migrations/<UTC-STAMP>_AssignCatalogAvatars.Designer.cs`
- Test: `tests/PartyUp.Api.IntegrationTests/Profile/AvatarCatalogMigrationTests.cs`

**Interfaces:**
- Consumes: `AvatarCatalog.Glyphs` / `AvatarCatalog.Contains(string)` from Task 1; columns `users.avatar_kind`, `users.avatar_emoji`, `users.avatar_source_platform_link_id` (`20260731044546_AddUserAggregate.cs:37-39`, mapped at `UserConfiguration.cs:48-55`); `AvatarKind.Emoji = 0`, `AvatarKind.Platform = 1` (`src/PartyUp.Domain/Users/ProfileEnums.cs:6-10`).
- Produces: migration id `<UTC-STAMP>_AssignCatalogAvatars` as the new head; the previous head is `20260810022735_AddTourCompletedAt`.
- Agreement between the two catalogs is proven, not asserted in prose: the test reads the shipped migration's own SQL back out of the migrations assembly, has Postgres unnest the 24-element array literal out of that exact text, and compares it to `AvatarCatalog.Glyphs` element for element with ordinal string equality. There is no second, hand-copied transcription of the statement anywhere in the tree.

**Decisions this task fixes (state them in the commit body):**
- **`avatar_source_platform_link_id` must be cleared.** Rewriting `avatar_kind = 1` (Platform) to `0` (Emoji) while leaving a source id behind produces a row that is both shapes at once; `Avatar` guarantees they are mutually exclusive (`Avatar.cs` doc comment, private ctor). The `UPDATE` nulls it for every row.
- **The FK survives wave A.** `fk_users_platform_links_avatar_source_platform_link_id` is `ON DELETE SET NULL` (`20260731044546_AddUserAggregate.cs:86-92`) and `MigrationTests.Migrate_CreatesEverySchemaObject` asserts it exists. `AvatarSource` is wave B, so nothing is dropped or renamed here — only data changes. Do not touch that constraint.
- **`Down()` is a no-op.** The pre-migration glyphs are overwritten and not journalled anywhere, so a down-migration cannot restore them; inventing a fallback glyph on the way down would be a second unrecoverable rewrite. An empty `Down()` is already the house precedent for a data-repair migration (`20260731142906_RepairStoredEmailCase.cs:21-25`).
- **Every row is rewritten, unconditionally** (AVATARS_DESIGN.md §2.2: values are not preserved). That includes mid-onboarding rows whose avatar columns are null; `User.IsProfileComplete` (`User.cs:102-103`) still requires username, display name, time zone and locale, so a mid-onboarding account is not accidentally promoted. Re-running the `UPDATE` is a no-op in effect because the glyph is a pure function of `id`.

- [ ] **Step 1: Write the failing test**

Create `tests/PartyUp.Api.IntegrationTests/Profile/AvatarCatalogMigrationTests.cs`:

```csharp
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using PartyUp.Application.Profile;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Profile;

/// <summary>
/// AVATARS_DESIGN.md §2.2 (D-050): existing avatar values are not preserved — every users row ends
/// on a catalog glyph chosen deterministically from its id, with avatar_kind = Emoji and no avatar
/// source. Proven by migrating to the pre-catalog head, seeding rows the old way (the
/// TourCompletedAtBackfillMigrationTests idiom,
/// tests/PartyUp.Api.IntegrationTests/Profile/TourCompletionTests.cs:132-145), then migrating
/// forward. The statement under test is read back out of the migrations assembly rather than
/// re-typed here, so the array it ships and the array this test checks cannot drift apart.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvatarCatalogBackfillMigrationTests : IAsyncLifetime
{
    private const string PreCatalogHead = "20260810022735_AddTourCompletedAt";
    private const string OffCatalogGlyph = "\U0001F642";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_AssignsACatalogGlyphToEveryUser_D050()
    {
        await using AppDbContext context = CreateContext();
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreCatalogHead);

        Guid offCatalogEmojiId = await SeedRawUserAsync(context, avatarKind: 0, avatarEmoji: OffCatalogGlyph);
        Guid literalGameId = await SeedRawUserAsync(context, avatarKind: 0, avatarEmoji: "game");
        Guid unsetId = await SeedRawUserAsync(context, avatarKind: null, avatarEmoji: null);
        Guid orphanedPlatformId = await SeedRawUserAsync(context, avatarKind: 1, avatarEmoji: null);
        Guid linkedPlatformId = await SeedRawUserAsync(context, avatarKind: 1, avatarEmoji: null);
        Guid platformLinkId = await SeedPlatformLinkAsync(context, linkedPlatformId);
        await SetAvatarSourceAsync(context, linkedPlatformId, platformLinkId);

        await migrator.MigrateAsync();

        Guid[] seeded =
        [
            offCatalogEmojiId,
            literalGameId,
            unsetId,
            orphanedPlatformId,
            linkedPlatformId,
        ];

        foreach (Guid id in seeded)
        {
            (int? kind, string? emoji, Guid? sourceId) = await ReadAvatarAsync(context, id);
            Assert.Equal(0, kind);
            Assert.NotNull(emoji);
            Assert.True(AvatarCatalog.Contains(emoji!), $"{emoji} is not a catalog glyph.");
            Assert.Null(sourceId);
        }

        (_, string? first, _) = await ReadAvatarAsync(context, offCatalogEmojiId);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE users SET avatar_emoji = 'stale', avatar_kind = 1 WHERE id = @id",
            new Npgsql.NpgsqlParameter("id", offCatalogEmojiId));
        await context.Database.ExecuteSqlRawAsync(BackfillSql(context));
        (int? rerunKind, string? rerun, _) = await ReadAvatarAsync(context, offCatalogEmojiId);
        Assert.Equal(0, rerunKind);
        Assert.Equal(first, rerun);
    }

    /// <summary>
    /// The 24 glyphs the migration hands out must be the 24 glyphs the allowlist admits, in the same
    /// order: the assignment is `glyphs[hash(id) % 24]`, so one transposed or variation-selector-less
    /// entry silently assigns a glyph the API then rejects on the user's next save. Postgres unnests
    /// the array expression lifted out of the shipped statement, which is the only copy of it.
    /// </summary>
    [Fact]
    public async Task MigrationArray_IsTheCatalog_GlyphForGlyph_D050()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();

        var shipped = new List<string>();
        DbConnection connection = await OpenAsync(context);
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT glyph
                FROM unnest({CatalogArrayExpression(context)}) WITH ORDINALITY AS t(glyph, ord)
                ORDER BY ord
                """;
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                shipped.Add(reader.GetString(0));
            }
        }

        Assert.Equal(AvatarCatalog.Glyphs.ToArray(), shipped.ToArray());
    }

    /// <summary>
    /// The verbatim text of <c>AssignCatalogAvatars.Up</c>'s single <see cref="SqlOperation"/>, read
    /// off the compiled migration. Re-applying this proves idempotence against the statement that
    /// actually ships, not against a paraphrase of it that nothing keeps in sync.
    /// </summary>
    private static string BackfillSql(AppDbContext context)
    {
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        TypeInfo migrationType = migrations.Migrations
            .Single(entry => entry.Key.EndsWith("_AssignCatalogAvatars", StringComparison.Ordinal))
            .Value;
        Migration migration = migrations.CreateMigration(
            migrationType,
            context.GetService<IDatabaseProvider>().Name);
        return migration.UpOperations.OfType<SqlOperation>().Single().Sql;
    }

    private static string CatalogArrayExpression(AppDbContext context)
    {
        string sql = BackfillSql(context);
        int start = sql.IndexOf("ARRAY[", StringComparison.Ordinal);
        int end = sql.IndexOf("])[", StringComparison.Ordinal);
        Assert.True(
            start >= 0 && end > start,
            "The backfill statement no longer contains a single ARRAY[...] subscript expression.");
        return sql[start..(end + 1)];
    }

    private static async Task<(int? Kind, string? Emoji, Guid? SourceId)> ReadAvatarAsync(
        AppDbContext context,
        Guid userId)
    {
        DbConnection connection = await OpenAsync(context);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT avatar_kind, avatar_emoji, avatar_source_platform_link_id FROM users WHERE id = @id";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = userId;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<DbConnection> OpenAsync(AppDbContext context)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private static async Task<Guid> SeedRawUserAsync(
        AppDbContext context,
        int? avatarKind,
        string? avatarEmoji)
    {
        Guid id = Guid.CreateVersion7();
        string friendCode = $"PU{Guid.NewGuid():N}"[..9].ToUpperInvariant();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users
                (id, lifecycle, username, display_name, time_zone_id, locale, friend_code,
                 age_gate_passed, age_gate_source, reminder_default, created_at,
                 avatar_kind, avatar_emoji, avatar_source_platform_link_id)
            VALUES
                ({id}, 1, {friendCode}, 'Player', 'America/Fortaleza', 'en', {friendCode},
                 true, 0, 3, now(),
                 {avatarKind}, {avatarEmoji}, NULL)
            """);
        return id;
    }

    private static async Task<Guid> SeedPlatformLinkAsync(AppDbContext context, Guid userId)
    {
        Guid id = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_links (id, user_id, platform, handle)
            VALUES ({id}, {userId}, 0, 'handle')
            """);
        return id;
    }

    private static Task SetAvatarSourceAsync(AppDbContext context, Guid userId, Guid platformLinkId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE users SET avatar_source_platform_link_id = {platformLinkId} WHERE id = {userId}
            """);

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
```

`username` is seeded with the friend-code value only to keep it unique and non-null; only `ux_users_friend_code` is a unique index on this table. The seeded `OffCatalogGlyph` (U+1F642) is deliberately *not* one of the 24 — it is the glyph the shipped fixtures used before Task 1 — and `"game"` is the literal the pre-catalog onboarding wizard submitted, so between them the two rows cover both shapes of stale data actually in the database.

- [ ] **Step 2: Run it and watch it fail**

Run (integration lane lives in WSL; redirect to a log rather than piping):

```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests/PartyUp.Api.IntegrationTests.csproj -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~AvatarCatalogBackfillMigrationTests' > /tmp/avatar-migration.log 2>&1; tail -40 /tmp/avatar-migration.log"
```

Expected: both tests FAIL. `Migrate_AssignsACatalogGlyphToEveryUser_D050` fails first at `Assert.Equal(0, kind)` — `Assert.Equal() Failure: Values differ. Expected: 0, Actual: (null)` on the `unsetId` row (equivalently `Actual: 1` on the Platform rows) — because the migration does not exist yet and `MigrateAsync()` leaves the seeded rows untouched. `MigrationArray_IsTheCatalog_GlyphForGlyph_D050` fails in `BackfillSql` with `InvalidOperationException: Sequence contains no matching element`, for the same reason.

- [ ] **Step 3: Generate the migration**

Run from the repo root:

```sh
dotnet dotnet-ef migrations add AssignCatalogAvatars --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api
```

This creates `src/PartyUp.Infrastructure/Migrations/<UTC-STAMP>_AssignCatalogAvatars.cs` with empty `Up`/`Down` bodies plus `<UTC-STAMP>_AssignCatalogAvatars.Designer.cs`. No model changed, so `AppDbContextModelSnapshot.cs` is rewritten identically — `git diff` on it must come back empty; if it does not, something else in the model drifted and must be dealt with separately. Confirm the id and ordering:

```sh
dotnet dotnet-ef migrations list --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api --no-connect
```

Expected: `AssignCatalogAvatars` last, directly after `AddTourCompletedAt`.

- [ ] **Step 4: Write the `Up()` body**

Replace the generated `<UTC-STAMP>_AssignCatalogAvatars.cs` with:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssignCatalogAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AVATARS_DESIGN.md §2.2 (D-050): every row lands on a glyph that is a pure function of
            // its id, so the statement is re-runnable and yields the same catalog assignment every
            // time. ('x' || 8 hex digits)::bit(32) is Postgres' hex bit-string literal syntax;
            // abs() guards the bit(32) -> bigint conversion against a negative high bit, and the
            // +1 lands on Postgres' 1-based array subscript. The array order is AvatarCatalog.Glyphs
            // order, asserted glyph for glyph by AvatarCatalogBackfillMigrationTests.
            // avatar_source_platform_link_id is nulled because an Emoji avatar and a source link are
            // mutually exclusive shapes; the ON DELETE SET NULL foreign key itself is untouched and
            // stays for wave B.
            migrationBuilder.Sql("""
                UPDATE users
                   SET avatar_kind = 0,
                       avatar_emoji = (ARRAY[
                           E'\U0001F3AE', E'\U0001F3B2', E'\U0001F579\uFE0F', E'\U0001F47E',
                           E'\U0001F0CF', E'\u265F\uFE0F', E'\U0001F3AF', E'\U0001F3C6',
                           E'\U0001F680', E'\U0001F409', E'\U0001F98A', E'\U0001F419',
                           E'\U0001F427', E'\U0001F989', E'\U0001F41D', E'\U0001F335',
                           E'\U0001F344', E'\U0001F319', E'\u2B50', E'\U0001F525',
                           E'\u26A1', E'\U0001F3A7', E'\U0001F355', E'\u2615'
                       ])[(abs(('x' || substr(md5(id::text), 1, 8))::bit(32)::bigint) % 24) + 1],
                       avatar_source_platform_link_id = NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The overwritten glyphs are not recorded anywhere, so down cannot restore them.
        }
    }
}
```

Every glyph is a Postgres `E'...'` escape rather than a pasted character. Inside an escape-string constant Postgres reads `\U` + eight hex digits and `\u` + four hex digits as Unicode escapes (they require a UTF-8 server encoding, which the Testcontainers image and the production database both have), so the two variation-selector entries — joystick and chess pawn — keep their U+FE0F through any copy-paste. Step 1's `MigrationArray_IsTheCatalog_GlyphForGlyph_D050` is what enforces that these 24 decode to `AvatarCatalog.Glyphs` exactly. C# raw string literals do not process backslashes, so the text reaches Postgres as written. Keep the file's UTF-8 BOM that the generator wrote.

- [ ] **Step 5: Run tests**

Run:

```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests/PartyUp.Api.IntegrationTests.csproj -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~AvatarCatalogBackfillMigrationTests|FullyQualifiedName~MigrationTests' > /tmp/avatar-migration.log 2>&1; tail -40 /tmp/avatar-migration.log"
```

Expected: PASS — `Migrate_AssignsACatalogGlyphToEveryUser_D050`, `MigrationArray_IsTheCatalog_GlyphForGlyph_D050`, plus `MigrationTests.Migrate_CreatesEverySchemaObject` still green (it asserts `fk_users_platform_links_avatar_source_platform_link_id` survives).

Then the whole suite, since this is a schema-lane change:

```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts > /tmp/partyup-full.log 2>&1; tail -30 /tmp/partyup-full.log"
```

Expected: PASS, 0 warnings. The browser lane is still opt-in and still knowingly red until Task 4 (see Task 1); `PartyUp.sln` does not run it.

- [ ] **Step 6: Commit**

```bash
git add src/PartyUp.Infrastructure/Migrations/ \
        tests/PartyUp.Api.IntegrationTests/Profile/AvatarCatalogMigrationTests.cs
git commit -m "feat(db): reassign every avatar to a catalog glyph by id hash

AVATARS_DESIGN.md 2.2: pre-catalog glyphs are not preserved. The new
glyph is a pure function of users.id, so the statement is re-runnable
and reproducible across environments. Platform rows are rewritten to
Emoji and lose avatar_source_platform_link_id, because the two avatar
shapes are mutually exclusive; the ON DELETE SET NULL foreign key stays
until wave B introduces AvatarSource. Down is a no-op: the overwritten
glyphs are not journalled and cannot be restored."
```


### Task 3: `AvatarPicker` component + the `avatar` i18n namespace

**Files:**
- Create: `web/src/features/profile/AvatarPicker.tsx`
- Create: `web/src/features/profile/AvatarPicker.test.tsx`
- Create: `web/src/i18n/en/avatar.json`
- Create: `web/src/i18n/pt-BR/avatar.json`
- Create: `web/src/i18n/es/avatar.json`
- Modify: `web/src/i18n/index.ts:5-115` (imports at 5-43, `resources` at 46-92, `ns` array at 101-115)
- Modify: `web/src/i18n/i18n.keys.test.ts:1-21` (import + `namespaces` list) and append a new `describe` at end of file (file is 340 lines)
- Test: `web/src/features/profile/AvatarPicker.test.tsx`, `web/src/i18n/i18n.keys.test.ts`

**Interfaces:**
- Consumes: `AVATAR_CATALOG` from `web/src/features/profile/avatarCatalog.ts` — `readonly { glyph: string; slug: string }[]`, 24 entries in the shared-contract order (Task 1).
- Produces: `export function AvatarPicker({ value, onChange, idPrefix, labelledBy }: { value: string; onChange: (glyph: string) => void; idPrefix: string; labelledBy?: string }): JSX.Element` in `web/src/features/profile/AvatarPicker.tsx`; the registered `avatar` namespace with keys `picker.label` and `catalog.<slug>` (24 keys) in all three locales.

**Who names the radiogroup:** exactly one of two things, never both. Without `labelledBy` the group names itself `aria-label={t('picker.label')}` — the onboarding step, which has no visible field label of its own. With `labelledBy` the group takes `aria-labelledby` and drops `aria-label`, so a page that already prints a visible field label (the profile page's `{t('fields.avatar')}`) makes that visible text the group's accessible name instead of hiding a second, different name behind it.

- [ ] **Step 1: Write the failing test**

Create `web/src/features/profile/AvatarPicker.test.tsx`:

```tsx
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'

import '../../i18n'
import { AVATAR_CATALOG } from './avatarCatalog'
import { AvatarPicker } from './AvatarPicker'

function Harness({ initial }: { initial: string }) {
  const [value, setValue] = useState(initial)
  return <AvatarPicker idPrefix="test-avatar" value={value} onChange={setValue} />
}

afterEach(cleanup)

describe('AvatarPicker', () => {
  it('moves selection and focus with ArrowRight and ArrowDown_APG', () => {
    render(<Harness initial="🎮" />)

    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveAttribute('aria-checked', 'true')
    expect(first).toHaveAttribute('tabindex', '0')

    fireEvent.keyDown(first, { key: 'ArrowRight' })
    const second = screen.getByRole('radio', { name: 'Dice' })
    expect(second).toHaveAttribute('aria-checked', 'true')
    expect(second).toHaveFocus()
    expect(first).toHaveAttribute('tabindex', '-1')

    fireEvent.keyDown(second, { key: 'ArrowDown' })
    const third = screen.getByRole('radio', { name: 'Joystick' })
    expect(third).toHaveAttribute('aria-checked', 'true')
    expect(third).toHaveFocus()
  })

  it('moves selection backwards with ArrowLeft and ArrowUp_APG', () => {
    render(<Harness initial="🕹️" />)

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Joystick' }), { key: 'ArrowLeft' })
    expect(screen.getByRole('radio', { name: 'Dice' })).toHaveFocus()

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Dice' }), { key: 'ArrowUp' })
    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveFocus()
    expect(first).toHaveAttribute('aria-checked', 'true')
  })

  it('wraps at both ends of the catalog_APG', () => {
    render(<Harness initial="🎮" />)

    fireEvent.keyDown(screen.getByRole('radio', { name: 'Game controller' }), { key: 'ArrowLeft' })
    const last = screen.getByRole('radio', { name: 'Coffee' })
    expect(last).toHaveAttribute('aria-checked', 'true')
    expect(last).toHaveFocus()

    fireEvent.keyDown(last, { key: 'ArrowRight' })
    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveAttribute('aria-checked', 'true')
    expect(first).toHaveFocus()
  })

  it('ignores keys that are not arrows_APG', () => {
    render(<Harness initial="🎮" />)
    const first = screen.getByRole('radio', { name: 'Game controller' })
    fireEvent.keyDown(first, { key: 'a' })
    expect(first).toHaveAttribute('aria-checked', 'true')
  })

  it('names every radio from its catalog label and keeps the glyph decorative_D188', () => {
    render(<Harness initial="🎮" />)

    expect(screen.getByRole('radiogroup', { name: 'Choose an avatar' })).toBeInTheDocument()
    const radios = screen.getAllByRole('radio')
    expect(radios).toHaveLength(AVATAR_CATALOG.length)
    radios.forEach((radio, index) => {
      const entry = AVATAR_CATALOG[index]
      expect(radio.getAttribute('aria-label')).toBeTruthy()
      expect(radio.getAttribute('aria-label')).not.toContain(entry.glyph)
      expect(radio.querySelector('span[aria-hidden="true"]')).toHaveTextContent(entry.glyph)
    })
    expect(screen.getByRole('radio', { name: 'Dragon' })).toHaveTextContent('🐉')
    expect(screen.queryByRole('radio', { name: '🐉' })).toBeNull()
  })

  it('lets the caller name the group from visible page text instead_APG', () => {
    render(
      <>
        <p id="page-avatar-label">Avatar</p>
        <AvatarPicker
          idPrefix="test-avatar"
          labelledBy="page-avatar-label"
          value="🎮"
          onChange={() => {}}
        />
      </>,
    )

    expect(screen.getByRole('radiogroup', { name: 'Avatar' })).not.toHaveAttribute('aria-label')
    expect(screen.queryByRole('radiogroup', { name: 'Choose an avatar' })).toBeNull()
  })

  it('reports the chosen glyph through onChange on click', () => {
    render(<Harness initial="🎮" />)
    fireEvent.click(screen.getByRole('radio', { name: 'Fox' }))
    expect(screen.getByRole('radio', { name: 'Fox' })).toHaveAttribute('aria-checked', 'true')
  })
})
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd web && npx vitest run src/features/profile/AvatarPicker.test.tsx`

Expected: FAIL with `Failed to resolve import "./AvatarPicker" from "src/features/profile/AvatarPicker.test.tsx"`.

- [ ] **Step 3: Write the three locale files**

Create `web/src/i18n/en/avatar.json`:

```json
{
  "picker": { "label": "Choose an avatar" },
  "catalog": {
    "gameController": "Game controller",
    "dice": "Dice",
    "joystick": "Joystick",
    "alien": "Alien",
    "jokerCard": "Joker card",
    "chessPawn": "Chess pawn",
    "dart": "Dart",
    "trophy": "Trophy",
    "rocket": "Rocket",
    "dragon": "Dragon",
    "fox": "Fox",
    "octopus": "Octopus",
    "penguin": "Penguin",
    "owl": "Owl",
    "bee": "Bee",
    "cactus": "Cactus",
    "mushroom": "Mushroom",
    "moon": "Moon",
    "star": "Star",
    "fire": "Fire",
    "bolt": "Lightning bolt",
    "headphones": "Headphones",
    "pizza": "Pizza",
    "coffee": "Coffee"
  }
}
```

Create `web/src/i18n/pt-BR/avatar.json`:

```json
{
  "picker": { "label": "Escolha um avatar" },
  "catalog": {
    "gameController": "Controle de videogame",
    "dice": "Dado",
    "joystick": "Joystick",
    "alien": "Alienígena",
    "jokerCard": "Carta coringa",
    "chessPawn": "Peão de xadrez",
    "dart": "Dardo",
    "trophy": "Troféu",
    "rocket": "Foguete",
    "dragon": "Dragão",
    "fox": "Raposa",
    "octopus": "Polvo",
    "penguin": "Pinguim",
    "owl": "Coruja",
    "bee": "Abelha",
    "cactus": "Cacto",
    "mushroom": "Cogumelo",
    "moon": "Lua",
    "star": "Estrela",
    "fire": "Fogo",
    "bolt": "Raio",
    "headphones": "Fones de ouvido",
    "pizza": "Pizza",
    "coffee": "Café"
  }
}
```

Create `web/src/i18n/es/avatar.json`:

```json
{
  "picker": { "label": "Elige un avatar" },
  "catalog": {
    "gameController": "Mando de videojuegos",
    "dice": "Dado",
    "joystick": "Palanca de mando",
    "alien": "Alienígena",
    "jokerCard": "Comodín",
    "chessPawn": "Peón de ajedrez",
    "dart": "Dardo",
    "trophy": "Trofeo",
    "rocket": "Cohete",
    "dragon": "Dragón",
    "fox": "Zorro",
    "octopus": "Pulpo",
    "penguin": "Pingüino",
    "owl": "Búho",
    "bee": "Abeja",
    "cactus": "Cactus",
    "mushroom": "Seta",
    "moon": "Luna",
    "star": "Estrella",
    "fire": "Fuego",
    "bolt": "Rayo",
    "headphones": "Auriculares",
    "pizza": "Pizza",
    "coffee": "Café"
  }
}
```

- [ ] **Step 4: Register the namespace in `web/src/i18n/index.ts`**

Three edits to the existing file, alphabetical within each import block (the file's existing order is alphabetical by namespace per locale):

Add the three imports — after line 4 (`import LanguageDetector …`) the `en` block starts at line 5; put `enAvatar` between `enAuth` (line 5) and `enAvailability` (line 6) is wrong alphabetically for the shipped order (`auth`, `availability`, `common`, …), so insert `enAvatar` after `enAvailability`:

```ts
import enAvailability from './en/availability.json'
import enAvatar from './en/avatar.json'
```

Mirror it in the `es` block (after `import esAvailability …`) and the `pt-BR` block (after `import ptBRAvailability …`):

```ts
import esAvatar from './es/avatar.json'
import ptBRAvatar from './pt-BR/avatar.json'
```

Add the namespace to each of the three `resources` locale objects (lines 47-61, 62-76, 77-91) — put it directly after the `onboarding:` entry so the three objects stay identical in shape:

```ts
  en: {
    common: enCommon,
    auth: enAuth,
    onboarding: enOnboarding,
    avatar: enAvatar,
    profile: enProfile,
```

```ts
  'pt-BR': {
    common: ptBRCommon,
    auth: ptBRAuth,
    onboarding: ptBROnboarding,
    avatar: ptBRAvatar,
    profile: ptBRProfile,
```

```ts
  es: {
    common: esCommon,
    auth: esAuth,
    onboarding: esOnboarding,
    avatar: esAvatar,
    profile: esProfile,
```

And add it to the runtime `ns` list (lines 101-115), in the same position:

```ts
    ns: [
      'common',
      'auth',
      'onboarding',
      'avatar',
      'profile',
      'settings',
      'errors',
      'friends',
      'sessions',
      'availability',
      'home',
      'polls',
      'notifications',
      'tour',
    ],
```

- [ ] **Step 5: Implement the picker**

Create `web/src/features/profile/AvatarPicker.tsx`:

```tsx
import { type JSX, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'

import { AVATAR_CATALOG } from './avatarCatalog'

/**
 * AVATARS_DESIGN.md §2.1: a 1-D wrapping ARIA APG radiogroup, the same arrow-key
 * idiom as ThemeToggle/VotingGrid. A 2-D roving grid is deliberately not used —
 * the catalog is one screen and needs no row/column model.
 *
 * `labelledBy` points at page text that already names the field; passing it
 * suppresses `picker.label` so the group never carries two competing names.
 */
export function AvatarPicker({
  value,
  onChange,
  idPrefix,
  labelledBy,
}: {
  value: string
  onChange: (glyph: string) => void
  idPrefix: string
  labelledBy?: string
}): JSX.Element {
  const { t } = useTranslation('avatar')
  const selectedIndex = AVATAR_CATALOG.findIndex((entry) => entry.glyph === value)

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    const delta =
      event.key === 'ArrowRight' || event.key === 'ArrowDown'
        ? 1
        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
          ? -1
          : 0
    if (delta === 0) return
    event.preventDefault()
    const from = selectedIndex < 0 ? 0 : selectedIndex
    const nextIndex =
      (from + delta + AVATAR_CATALOG.length) % AVATAR_CATALOG.length
    onChange(AVATAR_CATALOG[nextIndex].glyph)
    const radios =
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>(
        '[role=radio]',
      )
    radios?.[nextIndex]?.focus()
  }

  return (
    <div
      aria-label={labelledBy ? undefined : t('picker.label')}
      aria-labelledby={labelledBy}
      className="flex flex-wrap gap-2"
      data-testid={`${idPrefix}-picker`}
      role="radiogroup"
    >
      {AVATAR_CATALOG.map((entry, index) => {
        const checked = entry.glyph === value
        return (
          <button
            key={entry.slug}
            aria-checked={checked}
            aria-label={t(`catalog.${entry.slug}`)}
            className="min-h-11 min-w-11 rounded-control border-2 border-ink bg-card px-3 py-2 text-lg text-ink aria-checked:bg-accent aria-checked:text-accent-ink dark:aria-checked:border-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
            data-testid={`${idPrefix}-${entry.slug}`}
            id={`${idPrefix}-${entry.slug}`}
            role="radio"
            tabIndex={checked || (selectedIndex < 0 && index === 0) ? 0 : -1}
            type="button"
            onClick={() => onChange(entry.glyph)}
            onKeyDown={onKeyDown}
          >
            <span aria-hidden="true">{entry.glyph}</span>
          </button>
        )
      })}
    </div>
  )
}
```

- [ ] **Step 6: Run the picker tests**

Run: `cd web && npx vitest run src/features/profile/AvatarPicker.test.tsx`

Expected: PASS (7 tests).

- [ ] **Step 7: Extend the i18n inventory test**

In `web/src/i18n/i18n.keys.test.ts`, add the import after line 2 and add the namespace to the `namespaces` tuple (lines 7-21) in the same position used in `index.ts`:

```ts
import { AVATAR_CATALOG } from '../features/profile/avatarCatalog'
```

```ts
const namespaces = [
  'common',
  'auth',
  'onboarding',
  'avatar',
  'profile',
  'settings',
  'errors',
  'friends',
  'sessions',
  'availability',
  'home',
  'polls',
  'notifications',
  'tour',
] as const
```

Then append this describe block at the end of the file, in the shipped closed-inventory idiom (`registeredResources.en.<ns>` key set compared with `toEqual` against the closed list — extended, never loosened):

```ts
describe('avatar namespace parity with the closed emoji catalog (AVATARS_DESIGN.md §2.1)', () => {
  it('en catalog.* is exactly the AVATAR_CATALOG slug set', () => {
    const labels = Object.keys(
      (registeredResources.en.avatar as { catalog: Record<string, string> })
        .catalog,
    ).sort()
    expect(labels).toEqual(AVATAR_CATALOG.map((entry) => entry.slug).sort())
  })

  for (const locale of locales) {
    it(locale + ' names every catalog glyph with a non-empty distinct label', async () => {
      await i18n.changeLanguage(locale)
      const labels = AVATAR_CATALOG.map((entry) =>
        i18n.t(`catalog.${entry.slug}`, { ns: 'avatar' }),
      )
      for (const label of labels) expect(label.trim().length).toBeGreaterThan(0)
      expect(new Set(labels).size).toBe(AVATAR_CATALOG.length)
    })

    it(locale + ' has a radiogroup label for the picker', async () => {
      await i18n.changeLanguage(locale)
      expect(i18n.t('picker.label', { ns: 'avatar' })).toBe(
        (registeredResources[locale].avatar as { picker: { label: string } })
          .picker.label,
      )
    })
  }
})
```

- [ ] **Step 8: Run the i18n tests**

Run: `cd web && npx vitest run src/i18n/i18n.keys.test.ts`

Expected: PASS — including the pre-existing `registers every locale and namespace at runtime` and the deep-key parity loop, which now cover `avatar`.

- [ ] **Step 9: Lint, typecheck, full web suite**

Run: `cd web && npm run lint && npm run build && npm test`

Expected: PASS, no lint errors, `tsc` clean.

- [ ] **Step 10: Commit**

```bash
git add web/src/features/profile/AvatarPicker.tsx \
        web/src/features/profile/AvatarPicker.test.tsx \
        web/src/i18n/en/avatar.json \
        web/src/i18n/pt-BR/avatar.json \
        web/src/i18n/es/avatar.json \
        web/src/i18n/index.ts \
        web/src/i18n/i18n.keys.test.ts
git commit -m "feat(web): add curated emoji AvatarPicker and avatar i18n namespace"
```

---

### Task 4: wire `AvatarPicker` into the profile page and the onboarding wizard

**Files:**
- Modify: `web/src/features/profile/ProfileSettingsPage.tsx:1-11` (imports), `:123-127` (the avatar form)
- Modify: `web/src/features/onboarding/OnboardingWizard.tsx:1-25` (imports), `:194-227` (`AvatarStep`)
- Test: `web/src/features/profile/ProfileSettingsPage.test.tsx` (mock profile fixtures at lines 50, 79, 110, 134; new avatar test)
- Test: `web/src/features/onboarding/OnboardingWizard.test.tsx` (`completeDraft` at line 67, `activeSession` at line 117, the walk at lines 180-228, the time-zone round trip at lines 295-329 — its draft fixture at line 301 and its asserted POST body at line 324)

**Interfaces:**
- Consumes: `AvatarPicker({ value, onChange, idPrefix, labelledBy })` (Task 3); `AVATAR_CATALOG` (Task 1); the existing `save(event, '/api/v1/me/avatar')` path in `ProfileSettingsPage.tsx:70-71`, which reads `String(values.get('emoji') ?? '')` out of `FormData` — that path is NOT changed.
- Produces: no new exported API. `AvatarDraft` (`components['schemas']['AvatarDraft']`) emitted by `AvatarStep`'s `onNext` now always carries a catalog glyph.

**Scope note (AVATARS_DESIGN.md §2.1):** onboarding is **emoji-only** and renders **no source chooser**. `ChangeAvatar` requires `Lifecycle.Active` and platform linking is a later wizard step, so no avatar source can exist at step 5. `AvatarStep` therefore renders `AvatarPicker` alone and keeps emitting `{ kind: 'emoji', emoji, platformLinkId: null }` through `onNext` — it still issues no request of its own.

**Browser-lane note (verified, no change required):** `tests/PartyUp.BrowserTests/PlaywrightFixture.cs:544-551` is the whole avatar leg of `RegisterAndOnboardAsync` — it waits for the URL to end in `/onboarding/avatar`, invokes the `onOnboardingStep("avatar", page)` callback that `AxeAuditTests.cs` uses to capture the axe run, then clicks `GetByRole(AriaRole.Button, new() { Name = "Continue" })`. It never clicks the old `game`/`dice`/`arcade` options, and the 24 picker controls expose `role="radio"`, not `role="button"`, so the `Continue` lookup stays unambiguous after the swap. Leave `PlaywrightFixture.cs` untouched. The axe capture does tighten on both audited routes that now hold a picker — `onboarding-avatar` and `settings-profile` (`tests/PartyUp.BrowserTests/Accessibility/AuditedRoutes.cs:59` and `:68`): every radio must have an `aria-label`, and the group must be named — by `picker.label` in onboarding, by the `aria-labelledby` target on the profile page. Task 3 supplies both, which is why this task must not ship without Task 3.

- [ ] **Step 1: Update the onboarding wizard tests to the new control**

In `web/src/features/onboarding/OnboardingWizard.test.tsx`, replace all **four** `emoji: 'game'` literals with a real catalog glyph — line 67 (`completeDraft`), line 117 (`activeSession().profile.avatar`), and lines 301 and 324, which are the two halves of the single test `lets the time-zone step reach a zone that is neither the browser zone nor UTC`: 301 seeds the sessionStorage draft, 324 asserts the exact body POSTed to `/api/v1/onboarding/complete`. Change 301 without 324 and that test fails at Step 4 on the body comparison.

```ts
const completeDraft = {
  username: 'partyup',
  displayName: 'Party Up',
  avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
  timeZone: 'UTC',
  locale: null,
}
```

```ts
      avatar: { kind: 'emoji', emoji: '🎮', sourcePlatformLinkId: null },
```

Line 301, the seeded draft:

```ts
        avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
```

Line 324, the same glyph in the asserted completion body at the end of that test:

```ts
    expect(client.POST).toHaveBeenCalledWith('/api/v1/onboarding/complete', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: {
        username: 'partyup',
        displayName: 'Party Up',
        avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
        timeZone: 'America/Sao_Paulo',
        locale: 'en',
      },
    })
```

Then, in `advances in D-127 order and activates before rendering friend tools_D127` (line 180), replace the bare avatar-step `Continue` click at line 199-200 with a real picker interaction plus a draft assertion:

```tsx
    await expectHeading('Choose an avatar')

    expect(
      screen.getByRole('radiogroup', { name: 'Choose an avatar' }),
    ).toBeInTheDocument()
    expect(screen.getAllByRole('radio')).toHaveLength(24)
    fireEvent.click(screen.getByRole('radio', { name: 'Fox' }))
    expect(screen.getByRole('radio', { name: 'Fox' })).toHaveAttribute(
      'aria-checked',
      'true',
    )
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Choose your time zone')
    expect(
      JSON.parse(sessionStorage.getItem(storageKey)!) as {
        avatar: { emoji: string }
      },
    ).toMatchObject({ avatar: { kind: 'emoji', emoji: '🦊', platformLinkId: null } })
```

Add a dedicated keyboard case after that test:

```tsx
  it('lets the avatar step be driven entirely from the keyboard_APG', async () => {
    sessionStorage.setItem(
      storageKey,
      JSON.stringify({ username: 'partyup', displayName: 'Party Up' }),
    )
    renderWizard('/onboarding/avatar')

    await expectHeading('Choose an avatar')
    const first = screen.getByRole('radio', { name: 'Game controller' })
    expect(first).toHaveAttribute('tabindex', '0')
    fireEvent.keyDown(first, { key: 'ArrowLeft' })

    const last = screen.getByRole('radio', { name: 'Coffee' })
    expect(last).toHaveAttribute('aria-checked', 'true')
    expect(last).toHaveFocus()

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await expectHeading('Choose your time zone')
    expect(
      JSON.parse(sessionStorage.getItem(storageKey)!) as {
        avatar: { emoji: string }
      },
    ).toMatchObject({ avatar: { kind: 'emoji', emoji: '☕', platformLinkId: null } })
  })
```

- [ ] **Step 2: Run the onboarding tests and watch them fail**

Run: `cd web && npx vitest run src/features/onboarding/OnboardingWizard.test.tsx`

Expected: FAIL — `Unable to find an accessible element with the role "radio" and name "Fox"` (the step still renders the three untranslated `game`/`dice`/`arcade` buttons).

- [ ] **Step 3: Replace `AvatarStep` in the wizard**

In `web/src/features/onboarding/OnboardingWizard.tsx`, add the imports next to the other feature imports (after `import { ShareCard } from '../friends/ShareCard'` at line 25):

```ts
import { AVATAR_CATALOG } from '../profile/avatarCatalog'
import { AvatarPicker } from '../profile/AvatarPicker'
```

Replace lines 194-227 (`function AvatarStep(…) { … }`) with:

```tsx
function AvatarStep({
  value,
  onNext,
}: {
  value: AvatarDraft | null
  onNext: (avatar: AvatarDraft) => void
}) {
  const { t } = useTranslation('onboarding')
  const [emoji, setEmoji] = useState(value?.emoji ?? AVATAR_CATALOG[0].glyph)
  return (
    <div className="space-y-4">
      <AvatarPicker idPrefix="onboarding-avatar" value={emoji} onChange={setEmoji} />
      <Button
        variant="primary"
        onClick={() => onNext({ kind: 'emoji', emoji, platformLinkId: null })}
      >
        {t('actions.continue')}
      </Button>
    </div>
  )
}
```

The step's own `role="radiogroup"`/`aria-label={t('fields.avatar')}` wrapper is gone: the picker owns the group, and nesting a radiogroup inside a radiogroup would be an axe violation on `/onboarding/avatar`. No `labelledBy` is passed here — the step prints no visible field label, so the group names itself from `picker.label` in the `avatar` namespace.

- [ ] **Step 4: Run the onboarding tests**

Run: `cd web && npx vitest run src/features/onboarding/OnboardingWizard.test.tsx`

Expected: PASS (7 tests).

- [ ] **Step 5: Update the profile-page tests**

In `web/src/features/profile/ProfileSettingsPage.test.tsx`, replace `emoji: 'game'` with `emoji: '🎮'` in all four `/api/v1/me` mocks (lines 50, 79, 110, 134), then add this test inside the `describe('ProfileSettingsPage')` block:

```tsx
  it('saves a catalog glyph chosen in the avatar picker through the existing form path', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: {
          username: 'player',
          displayName: 'Player',
          avatar: { kind: 'emoji', emoji: '🎮', platformLinkId: null },
          timeZone: 'America/Fortaleza',
          platformLinks: [],
        } })
      }
      return Promise.resolve({ data: { methods: [] } })
    })
    api.put.mockResolvedValue({ data: {} })

    renderPage()

    const group = await screen.findByRole('radiogroup', { name: 'Avatar' })
    expect(group).toHaveAttribute('aria-labelledby', 'profile-avatar-label')
    expect(screen.getByRole('radio', { name: 'Game controller' })).toHaveAttribute('aria-checked', 'true')
    expect(screen.queryByRole('textbox', { name: 'Avatar' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: 'Dragon' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save avatar' }))

    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/v1/me/avatar', {
      params: { header: { 'X-CSRF-TOKEN': '' } },
      body: { kind: 'emoji', emoji: '🐉', platformLinkId: null },
    }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
```

- [ ] **Step 6: Run the profile tests and watch them fail**

Run: `cd web && npx vitest run src/features/profile/ProfileSettingsPage.test.tsx`

Expected: FAIL — `Unable to find an accessible element with the role "radiogroup" and name "Avatar"` (the page still renders the free-text `<Input name="emoji">` under its `<Label htmlFor="profile-avatar">`).

- [ ] **Step 7: Replace the free-text input on the profile page**

In `web/src/features/profile/ProfileSettingsPage.tsx`, add the imports after line 11:

```ts
import { AVATAR_CATALOG } from './avatarCatalog'
import { AvatarPicker } from './AvatarPicker'
```

Add this component above `export function ProfileSettingsPage()` (i.e. after `ProfileError`, which ends at line 35). It holds the picker's selection locally and mirrors it into a hidden input, so `save()`'s `new FormData(event.currentTarget)` / `values.get('emoji')` path at lines 55-71 is untouched. It is mounted only after the profile query resolves (the `isPending` early return at line 105 sits above the form), so `defaultGlyph` is always the real stored glyph. `labelledBy` hands the group's accessible name to the form's own visible `Avatar` text, which is what the other fields on this page do through `<Label htmlFor>`:

```tsx
function AvatarField({ defaultGlyph }: { defaultGlyph: string }) {
  const [glyph, setGlyph] = useState(defaultGlyph)
  return (
    <>
      <AvatarPicker
        idPrefix="profile-avatar"
        labelledBy="profile-avatar-label"
        value={glyph}
        onChange={setGlyph}
      />
      <input name="emoji" type="hidden" value={glyph} />
    </>
  )
}
```

Replace lines 123-127 (the avatar `<form>`) with:

```tsx
      <form className="space-y-2" onSubmit={(event) => void save(event, '/api/v1/me/avatar')}>
        <p className="font-display text-sm font-bold text-ink" id="profile-avatar-label">{t('fields.avatar')}</p>
        <AvatarField defaultGlyph={profile?.avatar?.emoji ?? AVATAR_CATALOG[0].glyph} />
        <Button disabled={saving} type="submit" variant="neutral">{t('actions.saveAvatar')}</Button>
      </form>
```

The visible `Avatar` text keeps its `id` because `AvatarField` points the radiogroup at it with `aria-labelledby`; a `<Label htmlFor>` cannot be used here because there is no single labelable control to point at — the field is a 24-radio group. With `labelledBy` set, `picker.label` is suppressed, so `Avatar` is the group's one accessible name.

`Label` and `Input` imports stay — both are still used by the username and display-name forms above.

- [ ] **Step 8: Run the profile tests**

Run: `cd web && npx vitest run src/features/profile/ProfileSettingsPage.test.tsx`

Expected: PASS (5 tests).

- [ ] **Step 9: Lint, build, full web suite**

Run: `cd web && npm run lint && npm run build && npm test`

Expected: PASS. `npm run lint` must be clean — in particular no unused-import error for `Label`/`Input` in `ProfileSettingsPage.tsx`.

- [ ] **Step 10: Run the browser lane against a fresh bundle**

The Playwright fixture serves `src/PartyUp.Api/bin/Debug/net10.0/wwwroot` and ignores `ArtifactsPath`, so the SPA must be rebuilt into that exact path before the lane runs, or the audit tests exercise the old avatar step:

```bash
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet build src/PartyUp.Api/PartyUp.Api.csproj && dotnet test tests/PartyUp.BrowserTests/PartyUp.BrowserTests.csproj --filter 'FullyQualifiedName~AxeAuditTests'"
```

Expected: PASS — `onboarding-avatar` and `settings-profile` both audit clean with zero serious/critical violations; no change to `PlaywrightFixture.cs` was needed because its avatar leg only clicks `Continue`.

- [ ] **Step 11: Commit**

```bash
git add web/src/features/profile/ProfileSettingsPage.tsx \
        web/src/features/profile/ProfileSettingsPage.test.tsx \
        web/src/features/onboarding/OnboardingWizard.tsx \
        web/src/features/onboarding/OnboardingWizard.test.tsx
git commit -m "feat(web): pick avatars from the catalog in profile and onboarding"
```


### Task 5A: Fan `AvatarResponse` onto the session roster + waitlist

**Files:**
- Modify: `src/PartyUp.Application/Sessions/SessionReadModels.cs:57-65` (add `Avatar? Avatar = null` to `RosterEntry`)
- Modify: `src/PartyUp.Infrastructure/Persistence/Repositories/SessionReadRepository.cs:416-454` (`ToDetailAsync` resolves the avatar alongside the display name)
- Modify: `src/PartyUp.Application/Sessions/SessionReadUseCases.cs:71-74` (`MaskBlockedPairRoster` nulls the avatar in the same `with` expression)
- Modify: `src/PartyUp.Api/Contracts/Sessions/SessionContracts.cs:1-8` (using) and `:246-271` (`RosterEntryResponse` + `From`)
- Test: `tests/PartyUp.Api.IntegrationTests/Sessions/SessionRosterMaskingWireTests.cs` (replace the assertion block at `:81-90`, add two new tests)

**Interfaces:**
- Consumes: `AvatarResponse(string Kind, string? Emoji, Guid? SourcePlatformLinkId)` and `AvatarResponse.From(Avatar avatar)` â€” shipped, `src/PartyUp.Api/Endpoints/AuthEndpoints.cs:979-987`, namespace `PartyUp.Api.Endpoints`. `Avatar` is `PartyUp.Domain.Users.Avatar`, a `readonly record struct`; `User.Avatar` is `Avatar?` and is set to `null` by `User.MarkDeleted` (`src/PartyUp.Domain/Users/User.cs:431`), so a tombstoned participant has no avatar for free.
- Produces: `RosterEntry(..., ReminderOffset? ReminderChoice, bool MaskedForViewer = false, Avatar? Avatar = null)` and wire field `rosterEntry.avatar: AvatarResponse | absent`. Task 5B and 5C do not depend on either; they are independent surfaces.

- [ ] **Step 1: Extend the shipped masking test so an avatar leak cannot stay green**

`tests/PartyUp.Api.IntegrationTests/Sessions/SessionRosterMaskingWireTests.cs`, inside `AMaskedEntryHasNoUserIdPropertyWhileATombstonedOneKeepsIt_D076`, replace the assertion block that currently reads:

```csharp
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _)
                && !entry.TryGetProperty("displayName", out _));
        Assert.False(maskedEntry.TryGetProperty("userId", out _));

        JsonElement tombstonedEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == tombstonedId.Value);
        Assert.True(tombstonedEntry.TryGetProperty("userId", out JsonElement tombstonedUserIdElement));
        Assert.Equal(tombstonedId.Value, tombstonedUserIdElement.GetGuid());
        Assert.False(tombstonedEntry.TryGetProperty("displayName", out _));
```

with:

```csharp
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _)
                && !entry.TryGetProperty("displayName", out _));
        Assert.False(maskedEntry.TryGetProperty("userId", out _));
        // D-188/D-076: the avatar is a third identity channel and must be ABSENT, not null.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));

        JsonElement tombstonedEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == tombstonedId.Value);
        Assert.True(tombstonedEntry.TryGetProperty("userId", out JsonElement tombstonedUserIdElement));
        Assert.Equal(tombstonedId.Value, tombstonedUserIdElement.GetGuid());
        Assert.False(tombstonedEntry.TryGetProperty("displayName", out _));
        // D-092: MarkDeleted nulls User.Avatar, so a tombstoned row has no avatar either.
        Assert.False(tombstonedEntry.TryGetProperty("avatar", out _));
```

- [ ] **Step 2: Add the two positive tests â€” the roster carries the avatar, `MaskedParticipant` does not**

Append both to the same class, after `AMaskedEntryHasNoUserIdPropertyWhileATombstonedOneKeepsIt_D076`. They create their session over HTTP with the same `POST /api/v1/sessions` body `JoinRequestResponsesCarryTheResolvedRequesterNameOnTheSummaryPathOnly_D103` uses at `:110-122`: `SessionEndpointTestSupport` has no session-creating helper (`GameSessionEndpointTests.cs:1447` exposes `SeedAsync`, `MakeFriendsAsync`, `SignInAsync`, `SuccessJsonAsync`, `AssertProblemAsync`, `SeedSessionAsync`, `RunDueSweepAsync`, `Iso` â€” and `SeedSessionAsync` bypasses HTTP and invites nobody).

```csharp
    /// <summary>
    /// D-188: every unmasked roster row carries the participant's avatar as an object, so the roster
    /// can render a face without a second round trip. Seeded users carry Avatar.FromEmoji("🙂")
    /// (PartyUpApiFactory.SeedUserAsync:99).
    /// </summary>
    [Fact]
    public async Task AnUnmaskedRosterEntryCarriesTheParticipantAvatar_D188()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"av-org-{Guid.NewGuid():N}@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: $"av-part-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, participantId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Avatar fan-out fixture",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { participantId.Value },
            }));
        Guid sessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));

        JsonElement roster = detail.RootElement.GetProperty("roster");
        JsonElement organizerEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == organizerId.Value);
        JsonElement avatar = organizerEntry.GetProperty("avatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());

        JsonElement inviteeEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == participantId.Value);
        Assert.Equal("emoji", inviteeEntry.GetProperty("avatar").GetProperty("kind").GetString());
    }

    /// <summary>
    /// AVATARS_DESIGN.md Â§2.4/Â§5 (D-188/D-076): MaskedParticipant stays excluded from the fan-out. It
    /// is the ordinal-plus-optional-name shape a viewer gets for someone they may not see, and a face
    /// there would defeat the ordinal that exists in its place. This pins the omission on the
    /// session-list organizer so a later wave cannot quietly add one.
    /// </summary>
    [Fact]
    public async Task TheSessionListOrganizerMaskedParticipantCarriesNoAvatar_D188_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"av-mp-org-{Guid.NewGuid():N}@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: $"av-mp-part-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, participantId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "MaskedParticipant fixture",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { participantId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/sessions?scope=upcoming"));
        JsonElement organizer = list.RootElement.GetProperty("items").EnumerateArray()
            .Single().GetProperty("organizer");
        Assert.True(organizer.TryGetProperty("ordinal", out _));
        Assert.False(organizer.TryGetProperty("avatar", out _));
    }
```

- [ ] **Step 3: Run it and watch it fail**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~SessionRosterMaskingWireTests' > /tmp/5a.log 2>&1; tail -40 /tmp/5a.log"
```
Expected: FAIL â€” `AnUnmaskedRosterEntryCarriesTheParticipantAvatar_D188` throws `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.` from `organizerEntry.GetProperty("avatar")` (the JSON has no `avatar` member yet). `TheSessionListOrganizerMaskedParticipantCarriesNoAvatar_D188_D076` PASSES already, and must keep passing through the whole task â€” it is the regression pin on a type this wave deliberately does not touch. The two extended `Assert.False(...TryGetProperty("avatar"...))` assertions likewise pass vacuously today â€” that is exactly the leak class they exist to catch later.

- [ ] **Step 4: Carry the avatar on the read model**

`src/PartyUp.Application/Sessions/SessionReadModels.cs` â€” add the using and the field:

```csharp
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
```

```csharp
public sealed record RosterEntry(
    ParticipationId ParticipationId,
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    ParticipationOrigin Origin,
    ReminderOffset? ReminderChoice,
    bool MaskedForViewer = false,
    Avatar? Avatar = null);
```

`Avatar` is trailing and defaulted so the two shipped positional call sites â€” `SessionReadRepository.cs:446` and `tests/PartyUp.Application.Tests/Sessions/SessionCommandTransactionTests.cs:658` â€” keep compiling untouched.

- [ ] **Step 5: Resolve the avatar in the projection**

`src/PartyUp.Infrastructure/Persistence/Repositories/SessionReadRepository.cs:416-454` â€” replace the method signature, the display-name batch and the roster loop, up to and including the loop's closing brace at `:454`:

```csharp
    private async Task<SessionDetail> ToDetailAsync(Session session, uint xmin, CancellationToken cancellationToken)
    {
        Dictionary<Guid, User> participantUsers = [];
        List<UserId> registeredIds = [.. session.Participations
            .Where(p => p.UserId.HasValue)
            .Select(p => p.UserId!.Value)];

        if (registeredIds.Count > 0)
        {
            List<User> users = await context.DomainUsers
                .AsNoTracking()
                .Where(u => registeredIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            foreach (User user in users)
            {
                participantUsers[user.Id.Value] = user;
            }
        }

        List<RosterEntry> roster = [];
        foreach (Participation participation in session.Participations)
        {
            User? participantUser = participation.UserId is { } userId
                ? participantUsers.GetValueOrDefault(userId.Value)
                : null;

            DisplayName? displayName = participation.UserId is not null
                ? participantUser?.DisplayName
                : participation.GuestIdentityId is { } guestId
                    ? session.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            roster.Add(new RosterEntry(
                participation.Id,
                participation.Participant,
                displayName,
                participation.Status,
                participation.IsWaitlisted,
                participation.Origin,
                participation.ReminderChoice,
                Avatar: participantUser?.Avatar));
        }
```

Leave the `return new SessionDetail(...)` at `:456-471` untouched. The `.Select(u => new { u.Id, u.DisplayName })` narrowing is dropped deliberately: `User.Avatar` is a **nullable complex property** (`UserConfiguration.cs:48`, columns `avatar_kind`/`avatar_emoji`/`avatar_source_platform_link_id`) and a complex type in a projection is not a translation guarantee worth betting a read path on. The set is roster-bounded, `AsNoTracking`, and one query either way. Put that rationale in the commit body, not in a comment.

`Avatar:` is a named argument after positional ones â€” legal C#, and it skips the defaulted `MaskedForViewer` without asserting a value the repository has no business setting (that flag is the use case's, per the `RosterEntry` doc comment).

- [ ] **Step 6: Null the avatar in the same expression that nulls the name**

`src/PartyUp.Application/Sessions/SessionReadUseCases.cs:71-74` â€” the collection expression only; the `return detail with { Roster = roster };` at `:76` stays as it is:

```csharp
        List<RosterEntry> roster = [.. detail.Roster.Select(entry =>
            entry.Participant.UserId is { } userId && viewer.BlockedCounterpartIds.Contains(userId)
                ? entry with { MaskedForViewer = true, DisplayName = null, Avatar = null }
                : entry)];
```

- [ ] **Step 7: Add the wire field**

`src/PartyUp.Api/Contracts/Sessions/SessionContracts.cs` â€” add the using for `AvatarResponse` (namespace `PartyUp.Api.Endpoints`):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
```

then `:246-271`:

```csharp
public sealed record RosterEntryResponse(
    Guid ParticipationId,
    // PHASE5_DESIGN.md Â§5.1 (D-076): a masked entry's UserId is null, and this attribute is what makes
    // the property ABSENT rather than present-as-null â€” the same rule also makes a guest row's
    // always-null userId absent, since its identity is already carried by guestIdentityId.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    string Status,
    bool IsWaitlisted,
    string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReminderChoice,
    // D-188: absent for a masked row, a tombstoned row and every guest row (D-017: guests have no avatar).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar)
{
    public static RosterEntryResponse From(RosterEntry entry) =>
        new(
            entry.ParticipationId.Value,
            // PHASE5_DESIGN.md Â§5.1 (D-076): a blocked-pair-masked entry drops its userId on the wire,
            // distinct from a tombstoned entry (null displayName, userId still present).
            entry.MaskedForViewer ? null : entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            SessionWireFormat.ParticipationStatus(entry.Status),
            entry.IsWaitlisted,
            SessionWireFormat.Origin(entry.Origin),
            SessionWireFormat.ReminderChoice(entry.ReminderChoice),
            entry.Avatar is { } avatar ? AvatarResponse.From(avatar) : null);
}
```

The mapper trusts the read model exactly the way `entry.DisplayName?.Value` already does â€” masking happened in Step 6, in one expression, so no second policy lives here.

- [ ] **Step 8: Build with zero warnings**

Run:
```sh
dotnet build PartyUp.sln
```
Expected: PASS, `0 Warning(s)` (TreatWarningsAsErrors would fail the build otherwise).

- [ ] **Step 9: Run the tests**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~SessionRosterMaskingWireTests|FullyQualifiedName~GameSessionEndpointTests|FullyQualifiedName~SessionCommandTransactionTests' > /tmp/5a.log 2>&1; tail -40 /tmp/5a.log"
```
Expected: PASS.

- [ ] **Step 10: Regenerate the API types**

The generator reads the OpenAPI document from a **running** API, so this needs two terminals. Terminal A (leave running):
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && docker compose -f deploy/compose.dev.yml up -d"
dotnet run --project src/PartyUp.Api
```
Terminal B, once `http://localhost:5000/health` answers:
```sh
cd web
npm run gen:api
npm run lint && npm run build && npm test
```
Expected: `web/src/api/schema.d.ts` gains an optional `avatar?: components["schemas"]["AvatarResponse"]` on `RosterEntryResponse`. Never hand-edit that file. Terminal A stays up for Tasks 5B/5C.

- [ ] **Step 11: Commit**

```bash
git add src/PartyUp.Application/Sessions/SessionReadModels.cs \
        src/PartyUp.Application/Sessions/SessionReadUseCases.cs \
        src/PartyUp.Infrastructure/Persistence/Repositories/SessionReadRepository.cs \
        src/PartyUp.Api/Contracts/Sessions/SessionContracts.cs \
        tests/PartyUp.Api.IntegrationTests/Sessions/SessionRosterMaskingWireTests.cs \
        web/src/api/schema.d.ts
git commit -m "feat(api): carry the avatar on session roster entries" -m "D-188 fan-out, wave A. RosterEntry gains a trailing Avatar? so the two
shipped positional call sites keep compiling, and MaskBlockedPairRoster nulls
it in the same 'with' expression that nulls DisplayName â€” the avatar cannot
outlive the name it is masked with. A tombstoned participant needs no rule:
MarkDeleted already nulls User.Avatar.

The roster projection materializes the participant User rows instead of
projecting an anonymous type: Avatar is a nullable complex property, and a
complex type in a Select is not a translation guarantee worth putting on a
read path. The set is roster-bounded and it is one query either way."
```

---

### Task 5B: Fan `AvatarResponse` onto the poll roster and the finalization preview â€” and prove the guest arm carries none

**Files:**
- Modify: `src/PartyUp.Application/Polls/PollReadModels.cs:1-4` (using), `:26-31` (`PollRosterEntry`), `:102-106` (`PollFinalizationPreviewRosterEntry`), plus a new `UserNameplate` record
- Modify: `src/PartyUp.Application/Abstractions/IPollReadRepository.cs:19-21` (`FindDisplayNamesAsync` â†’ `FindNameplatesAsync`)
- Modify: `src/PartyUp.Infrastructure/Persistence/Repositories/PollReadRepository.cs:22-45`
- Modify: `src/PartyUp.Application/Polls/PollReadUseCases.cs:1-5` (using), `:49-76` (masking site)
- Modify: `src/PartyUp.Application/Polls/GuestPollReadUseCases.cs:41-57` (rename only â€” the row stays avatar-free)
- Modify: `src/PartyUp.Application/Polls/PollFinalizationPreviewUseCase.cs:78-79` (prefetch) and `:94-105` (preview roster loop)
- Modify: `src/PartyUp.Api/Contracts/Polls/PollContracts.cs:1-8` (using), `:145-162`, `:264-278`
- Test: `tests/PartyUp.Api.IntegrationTests/Polls/PollEndpointTests.cs` (extend `AMaskedPollRosterEntryHasNoUserIdProperty_D076` at `:193-221` and `RosterEntriesCarryAStableParticipationIdRowKey_F5` at `:283-313`)
- Test: `tests/PartyUp.Api.IntegrationTests/Polls/PollGuestHttpTests.cs` (new `_D150_D188`)
- Test: `tests/PartyUp.Api.IntegrationTests/Polls/PollListAndPreviewTests.cs` (new `_D188`)

**Interfaces:**
- Consumes: `AvatarResponse.From(Avatar)` (`AuthEndpoints.cs:984`). Independent of Task 5A â€” no shared type changes.
- Produces: `public sealed record UserNameplate(DisplayName? DisplayName, Avatar? Avatar)` in namespace `PartyUp.Application.Polls`; `IPollReadRepository.FindNameplatesAsync(IReadOnlyCollection<UserId>, CancellationToken) -> Task<IReadOnlyDictionary<Guid, UserNameplate>>`; `PollRosterEntry(..., bool MaskedForViewer = false, Avatar? Avatar = null)`; `PollFinalizationPreviewRosterEntry(ParticipantRef, DisplayName?, ParticipationStatus, bool, Avatar?)`; wire fields `pollRosterEntry.avatar` and `previewRosterEntry.avatar`.

- [ ] **Step 1: Extend the two shipped poll masking tests**

`tests/PartyUp.Api.IntegrationTests/Polls/PollEndpointTests.cs`, in `AMaskedPollRosterEntryHasNoUserIdProperty_D076` (`:193-221`), after `Assert.False(maskedEntry.TryGetProperty("userId", out _));` at `:216`:

```csharp
        // D-188: the avatar is masked with the name, in one expression â€” absent, not null.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));
```

and replace that test's last assertion (`:220`):

```csharp
        Assert.True(organizerEntry.TryGetProperty("displayName", out _));
        JsonElement organizerAvatar = organizerEntry.GetProperty("avatar");
        Assert.Equal("emoji", organizerAvatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", organizerAvatar.GetProperty("emoji").GetString());
```

The masked-row selector at `:214-215` (`!TryGetProperty("userId") && !TryGetProperty("displayName")`) keeps working unchanged: masking drops a third property, it does not add one.

Then replace the tail of `RosterEntriesCarryAStableParticipationIdRowKey_F5` (`:310-312`) â€” that is the test holding the masked-row participation-id assertion, not `AMaskedSuggesterHasNoUserIdOnTheirSuggestedOption_F3`, which inspects options and never reads the roster:

```csharp
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _) && !entry.TryGetProperty("displayName", out _));
        Assert.NotEqual(Guid.Empty, maskedEntry.GetProperty("participationId").GetGuid());
        // D-188: the row key survives masking; the face does not.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));
```

- [ ] **Step 2: Add the guest-arm test â€” no avatar anywhere, by construction**

`tests/PartyUp.Api.IntegrationTests/Polls/PollGuestHttpTests.cs`, insert as the last `[Fact]`, above the private fixture helpers at `:380`. `SeedPollWithGuestLinkAsync` (`:401`) returns `(Organizer, PollId, OptionId, GuestLinkId, Token)`, and `ClaimAsync` returns the `GuestPollResponse` body, whose `roster` is `PollRosterEntryResponse[]` (`GuestPollContracts.cs:41,64`) â€” the very type this task adds `avatar` to:

```csharp
    /// <summary>
    /// D-150/D-188: PollRosterEntryResponse is shared with the guest poll view, and GuestPollReadUseCases
    /// builds every row unmasked by construction â€” so the guest arm must carry NO avatar at all, on any
    /// row, rather than relying on a masking rule that structurally cannot fire for a guest viewer.
    /// </summary>
    [Fact]
    public async Task GuestPollRoster_CarriesNoAvatarOnAnyRow_D150_D188()
    {
        (_, _, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string cookieHeader = await ExchangeAsync(guestClient, token);

        using JsonDocument claimed = await ClaimAsync(guestClient, guestLinkId, cookieHeader, "Avatarless Guest");
        Assert.Equal("live", claimed.RootElement.GetProperty("state").GetString());

        JsonElement roster = claimed.RootElement.GetProperty("roster");
        Assert.NotEqual(0, roster.GetArrayLength());
        Assert.All(
            roster.EnumerateArray(),
            entry => Assert.False(entry.TryGetProperty("avatar", out _)));
    }
```

- [ ] **Step 3: Add the preview test**

`tests/PartyUp.Api.IntegrationTests/Polls/PollListAndPreviewTests.cs`, insert after `Preview_IsSideEffectFree_OnADueButUnsweptPoll` and above the `// ---- fixture helpers ----` region (`:699`). `CreatePollWithFirstOptionAsync` (`:758`) creates a poll with two options and no invitees, so the preview roster is the organizer alone â€” every row has a real `userId`:

```csharp
    /// <summary>
    /// D-188: the preview roster is organizer-only and never masked, so every registered row carries
    /// the participant's avatar â€” resolved from the same prefetched nameplate dictionary the display
    /// name comes from, not a second lookup.
    /// </summary>
    [Fact]
    public async Task PreviewRosterEntriesCarryTheParticipantAvatar_D188()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-avatar-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        (Guid pollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);

        using JsonDocument preview = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"));

        JsonElement organizerRow = preview.RootElement.GetProperty("options")[0].GetProperty("roster")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("userId").GetGuid() == organizerId.Value);
        JsonElement avatar = organizerRow.GetProperty("avatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());
    }
```

- [ ] **Step 4: Run them and watch them fail**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~PollEndpointTests|FullyQualifiedName~PollGuestHttpTests|FullyQualifiedName~PollListAndPreviewTests' > /tmp/5b.log 2>&1; tail -60 /tmp/5b.log"
```
Expected: FAIL â€” `AMaskedPollRosterEntryHasNoUserIdProperty_D076` and `PreviewRosterEntriesCarryTheParticipantAvatar_D188` both throw `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.` from `GetProperty("avatar")`. `GuestPollRoster_CarriesNoAvatarOnAnyRow_D150_D188` PASSES already â€” it is the regression pin for Step 7, not a red test.

- [ ] **Step 5: Widen the prefetched dictionary from names to nameplates**

`src/PartyUp.Application/Polls/PollReadModels.cs` â€” replace the using block (`:1-4`) and add the record directly above `PollRosterEntry`'s doc comment at `:19`:

```csharp
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;
```

```csharp
/// <summary>
/// How one registered user renders on a poll read: the display name and the avatar, resolved in the
/// same prefetch so a masking site nulls both in one expression instead of two.
/// </summary>
public sealed record UserNameplate(DisplayName? DisplayName, Avatar? Avatar);
```

and extend the two roster shapes, each replacing the record declaration only and leaving its doc comment (`:19-25`, `:96-101`) in place â€” `:26-31`:

```csharp
public sealed record PollRosterEntry(
    PollParticipantId ParticipationId,
    ParticipantRef Participant,
    DisplayName? DisplayName,
    bool IsActive,
    bool MaskedForViewer = false,
    Avatar? Avatar = null);
```

and `:102-106`:

```csharp
public sealed record PollFinalizationPreviewRosterEntry(
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    Avatar? Avatar);
```

`PollRosterEntry.Avatar` is trailing and defaulted so `GuestPollReadUseCases.cs:56`'s four-argument construction keeps compiling **and stays avatar-free by construction** â€” that is the D-150 guarantee, not an omission.

- [ ] **Step 6: Rename the port and widen the repository**

`src/PartyUp.Application/Abstractions/IPollReadRepository.cs:19-21` (the whole member; `PartyUp.Application.Polls` and `PartyUp.Domain.Users` are already imported at `:2`/`:5`):

```csharp
    /// <summary>
    /// Bulk name+avatar resolution for a poll's registered participants. One prefetch, both fields:
    /// the avatar rides the display name so no read path can resolve one without the other.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken);
```

`src/PartyUp.Infrastructure/Persistence/Repositories/PollReadRepository.cs:22-45`:

```csharp
    public async Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserNameplate>();
        }

        List<UserId> ids = [.. userIds.Distinct()];
        List<User> rows = await context.DomainUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, UserNameplate> result = [];
        foreach (User row in rows)
        {
            result[row.Id.Value] = new UserNameplate(row.DisplayName, row.Avatar);
        }

        return result;
    }
```

Same reason as Task 5A for dropping the anonymous projection: `User.Avatar` is a nullable complex property. `PartyUp.Domain.Users` is already imported here (`:10`), as is `PartyUp.Application.Polls` (`:6`), so `UserNameplate` resolves without a new using.

- [ ] **Step 7: Rewrite the three call sites**

`src/PartyUp.Application/Polls/PollReadUseCases.cs` â€” insert `using PartyUp.Domain.Users;` between `PartyUp.Domain.Polls` (`:4`) and `PartyUp.Domain.Visibility` (`:5`) so the block stays sorted, then replace `:49-76`:

```csharp
        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await reads.FindNameplatesAsync(registeredIds, cancellationToken);

        List<PollRosterEntry> roster = [];
        HashSet<UserId> maskedUserIds = [];
        foreach (PollParticipant participant in poll.Participants)
        {
            DisplayName? displayName = participant.UserId is { } userId
                ? nameplates.GetValueOrDefault(userId.Value)?.DisplayName
                : participant.GuestIdentityId is { } guestId
                    ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            // D-017: a guest has no avatar, so this resolves only for the registered arm.
            Avatar? avatar = participant.UserId is { } avatarUserId
                ? nameplates.GetValueOrDefault(avatarUserId.Value)?.Avatar
                : null;

            bool masked = participant.UserId is { } participantUserId &&
                viewer.BlockedCounterpartIds.Contains(participantUserId);

            if (masked && participant.UserId is { } maskedUserId)
            {
                maskedUserIds.Add(maskedUserId);
            }

            roster.Add(new PollRosterEntry(
                participant.Id,
                participant.Participant,
                masked ? null : displayName,
                participant.IsActive,
                masked,
                masked ? null : avatar));
        }
```

`src/PartyUp.Application/Polls/GuestPollReadUseCases.cs:41-57` (prefetch through the loop's closing brace) â€” rename only; the row keeps four arguments and therefore no avatar:

```csharp
        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await reads.FindNameplatesAsync(registeredIds, cancellationToken);

        List<PollRosterEntry> roster = [];
        foreach (PollParticipant participant in poll.Participants)
        {
            DisplayName? displayName = participant.UserId is { } userId
                ? nameplates.GetValueOrDefault(userId.Value)?.DisplayName
                : participant.GuestIdentityId is { } guestId
                    ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                    : null;

            // A guest viewer never blocks or is blocked (D-076 masking is a User-to-User relation
            // only), so MaskedForViewer stays false throughout â€” every row renders exactly as the
            // registered voter-grid view would for an unblocked viewer. Avatar is left unset: a guest
            // read is outside the visibility engine (D-150), so the shared response carries no face.
            roster.Add(new PollRosterEntry(participant.Id, participant.Participant, displayName, participant.IsActive));
        }
```

`src/PartyUp.Application/Polls/PollFinalizationPreviewUseCase.cs:78-79`:

```csharp
        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await reads.FindNameplatesAsync(registeredIds, cancellationToken);
```

and the roster loop at `:94-105`:

```csharp
            List<PollFinalizationPreviewRosterEntry> roster = [];
            foreach (FinalizedParticipation entry in projection.Roster)
            {
                UserNameplate? nameplate = entry.Participant.UserId is { } userId
                    ? nameplates.GetValueOrDefault(userId.Value)
                    : null;

                DisplayName? displayName = entry.Participant.UserId is not null
                    ? nameplate?.DisplayName
                    : entry.Participant.GuestIdentityId is { } guestId
                        ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                        : null;

                roster.Add(new PollFinalizationPreviewRosterEntry(
                    entry.Participant, displayName, entry.Status, entry.IsWaitlisted, nameplate?.Avatar));
            }
```

- [ ] **Step 8: Add the two wire fields**

`src/PartyUp.Api/Contracts/Polls/PollContracts.cs` â€” add `using PartyUp.Api.Endpoints;` after `using PartyUp.Api.Contracts.Sessions;` (`:4`), then replace `:145-162`. Both ranges below start at the record declaration: the `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]` attribute one line above each (`:144`, `:263`) is not part of the replacement and must survive â€” dropping it would silently loosen the wire contract to accept unknown members.

```csharp
public sealed record PollRosterEntryResponse(
    // The stable row key (session precedent RosterEntryResponse.ParticipationId): the PollParticipant
    // id itself, never a user id, so a masked entry stays unkeyable-to-identity.
    Guid ParticipationId,
    // D-076: a masked entry's userId is absent, mirroring RosterEntryResponse.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    bool IsActive,
    // D-188: absent for a masked row, and absent on every row of the guest arm (D-150), which shares
    // this type and builds its rows without an avatar.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar)
{
    public static PollRosterEntryResponse From(PollRosterEntry entry) =>
        new(
            entry.ParticipationId.Value,
            entry.MaskedForViewer ? null : entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            entry.IsActive,
            entry.Avatar is { } avatar ? AvatarResponse.From(avatar) : null);
}
```

and `:264-278`:

```csharp
public sealed record PollFinalizationPreviewRosterEntryResponse(
    Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    string Status,
    bool IsWaitlisted,
    // D-188: organizer-only and never masked, so this is absent only for a guest row or a
    // tombstoned participant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar)
{
    public static PollFinalizationPreviewRosterEntryResponse From(PollFinalizationPreviewRosterEntry entry) =>
        new(
            entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            SessionWireFormat.ParticipationStatus(entry.Status),
            entry.IsWaitlisted,
            entry.Avatar is { } avatar ? AvatarResponse.From(avatar) : null);
}
```

- [ ] **Step 9: Build with zero warnings**

Run:
```sh
dotnet build PartyUp.sln
```
Expected: PASS, `0 Warning(s)`. If the build reports `CS1061: 'IPollReadRepository' does not contain a definition for 'FindDisplayNamesAsync'`, a call site was missed â€” there are exactly three (`PollReadUseCases.cs:50`, `GuestPollReadUseCases.cs:42`, `PollFinalizationPreviewUseCase.cs:79`), and no test double implements this port.

- [ ] **Step 10: Run the tests**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~Polls' > /tmp/5b.log 2>&1; tail -60 /tmp/5b.log"
```
Expected: PASS, including `GuestPollRoster_CarriesNoAvatarOnAnyRow_D150_D188` â€” which is now a real assertion, because the type it inspects has an `avatar` member that the guest path deliberately never fills.

- [ ] **Step 11: Regenerate the API types**

With the API from Task 5A Step 10 still running on `:5000` (restart it so it serves the new contracts â€” `Ctrl+C`, then `dotnet run --project src/PartyUp.Api`):
```sh
cd web
npm run gen:api
npm run lint && npm run build && npm test
```
Expected: `PollRosterEntryResponse` and `PollFinalizationPreviewRosterEntryResponse` each gain an optional `avatar?` in `web/src/api/schema.d.ts`. Do not hand-edit it.

- [ ] **Step 12: Commit**

```bash
git add src/PartyUp.Application/Polls/PollReadModels.cs \
        src/PartyUp.Application/Polls/PollReadUseCases.cs \
        src/PartyUp.Application/Polls/GuestPollReadUseCases.cs \
        src/PartyUp.Application/Polls/PollFinalizationPreviewUseCase.cs \
        src/PartyUp.Application/Abstractions/IPollReadRepository.cs \
        src/PartyUp.Infrastructure/Persistence/Repositories/PollReadRepository.cs \
        src/PartyUp.Api/Contracts/Polls/PollContracts.cs \
        tests/PartyUp.Api.IntegrationTests/Polls/PollEndpointTests.cs \
        tests/PartyUp.Api.IntegrationTests/Polls/PollGuestHttpTests.cs \
        tests/PartyUp.Api.IntegrationTests/Polls/PollListAndPreviewTests.cs \
        web/src/api/schema.d.ts
git commit -m "feat(api): carry the avatar on poll rosters and previews" -m "D-188 fan-out, wave A. FindDisplayNamesAsync becomes FindNameplatesAsync and
returns name+avatar together, so the poll masking site nulls both in the one
expression it already used for the name â€” a mapper cannot resolve an avatar the
name was masked out of.

The guest arm keeps its four-argument PollRosterEntry construction and therefore
carries no avatar at all (D-150): guests sit outside the visibility engine, and
the shared response type must not become a face channel for a bearer token. A
test now pins that absence on every guest row."
```

---

### Task 5C: Fan `AvatarResponse` onto notifications and Steam import matches

**Files:**
- Modify: `src/PartyUp.Api/Contracts/Notifications/NotificationContracts.cs:1-4` (using), `:31-61` (`NotificationResponse` + `From`)
- Modify: `src/PartyUp.Application/Friends/ImportSteamFriendsUseCase.cs:161-163` and `:240` (`SteamMatchedUser`)
- Modify: `src/PartyUp.Api/Endpoints/FriendEndpoints.cs:703-706` (`SteamMatchedUserResponse` â€” the record only; the `[JsonUnmappedMemberHandling]` attribute at `:702` stays where it is)
- Modify: `web/src/features/friends/SteamImportPage.test.tsx:40` and `:144-148` (the four match literals, once `avatar` becomes required on the generated type)
- Test: `tests/PartyUp.Api.IntegrationTests/Notifications/NotificationPushHttpTests.cs` (extend `:315-316`, add one `_D188`)
- Test: `tests/PartyUp.Api.IntegrationTests/Friends/SteamImportEndpointTests.cs` (extend `:88-90`)
- Test: `tests/PartyUp.Api.IntegrationTests/Friends/BlockEndpointTests.cs` (add one `_D192`)

**Interfaces:**
- Consumes: `AvatarResponse.From(Avatar)`; `MaskedIdentity` (`src/PartyUp.Domain/Visibility/MaskedIdentity.cs`) â€” `Anonymous(reason)` already sets `Avatar = null` and `Named(displayName, avatar)` carries it, and `IdentityMasking.ForUser` already feeds `subject.Avatar` in. `NotificationReadModel.Actor` is `MaskedIdentity?` (`NotificationReadModels.cs:33`). **Nothing in the Application layer changes for notifications** â€” the masking is already structural; only the mapper is blind to it.
- Produces: wire field `notification.actorAvatar`; `SteamMatchedUser(string Username, Avatar Avatar)`; wire field `steamMatch.avatar` (always present).
- Deliberately unchanged: `BlockedUserResponse(Guid BlockId, string Username, string CreatedAt)` (`FriendEndpoints.cs:623`) stays at three fields per D-192, and this task adds the test that says so.

- [ ] **Step 1: Extend the shipped notification masking test**

`tests/PartyUp.Api.IntegrationTests/Notifications/NotificationPushHttpTests.cs`, at the end of `NotificationWireShape_BlockedPairActorOmitsBothActorFields_D076_D086` (`:315-316`):

```csharp
        Assert.False(row.TryGetProperty("actorUserId", out _));
        Assert.False(row.TryGetProperty("actorDisplayName", out _));
        // D-188: MaskedIdentity.Anonymous carries a null avatar, so the third channel is absent too.
        Assert.False(row.TryGetProperty("actorAvatar", out _));
```

- [ ] **Step 2: Add the named-actor test**

Append immediately after that test in the same class:

```csharp
    /// <summary>
    /// D-188: an unmasked actor's avatar reaches the notification center from row.Actor.Avatar â€” the
    /// MaskedIdentity the projection already built â€” never from a second lookup keyed on ActorUserId,
    /// which would bypass the masking rule entirely.
    /// </summary>
    [Fact]
    public async Task NotificationWireShape_NamedActorCarriesTheirAvatar_D188()
    {
        SessionHttpUser recipient = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser actor = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession recipientSession = await SessionEndpointTestSupport.SignInAsync(_factory, recipient.Id);
        using SessionHttpSession actorSession = await SessionEndpointTestSupport.SignInAsync(_factory, actor.Id);

        HttpResponseMessage sent = await actorSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = recipient.Username }));
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await recipientSession.GetAsync("/api/v1/notifications?scope=all"));
        JsonElement row = page.RootElement.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "friendRequestReceived");

        Assert.True(row.TryGetProperty("actorUserId", out _));
        JsonElement avatar = row.GetProperty("actorAvatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());
    }
```

`SessionEndpointTestSupport.SeedAsync` onboards with `Avatar.FromEmoji("🙂")` (`GameSessionEndpointTests.cs:1466`), which is where that expectation comes from.

- [ ] **Step 3: Extend the Steam match assertion, and pin the D-192 exclusion**

`tests/PartyUp.Api.IntegrationTests/Friends/SteamImportEndpointTests.cs`, replace `:88-90` (the tail of `ProviderStates_Are200DataNotProblemResponses_D049`):

```csharp
        Assert.Equal(
            candidate.Username,
            readyBody.RootElement.GetProperty("matches")[0].GetProperty("username").GetString());
        // D-188: a matched user is a real, visible PartyUp account (LookupVisibility.CanMatchSteamImport
        // already rejected blocked and incomplete candidates), so the match card carries their avatar.
        JsonElement matchAvatar = readyBody.RootElement.GetProperty("matches")[0].GetProperty("avatar");
        Assert.Equal("emoji", matchAvatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", matchAvatar.GetProperty("emoji").GetString());
```

`SeedLinkedAsync` (`:224`) builds its users through `FriendEndpointTest.SeedAsync`, which onboards with `Avatar.FromEmoji("🙂")` (`ProfileEndpointTests.cs:222`).

Then `tests/PartyUp.Api.IntegrationTests/Friends/BlockEndpointTests.cs`, append to the class after `BlockList_KeepsAtBlockUsernameAfterCounterpartRename_D065` (`:101`):

```csharp
    /// <summary>
    /// D-192: the blocked-users list renders a neutral disc from the stored username snapshot and
    /// carries NO avatar. A live avatar there would turn blocking into a monitoring channel — the
    /// D-076 leak class pointed the other way — so BlockedUserResponse keeps exactly its three
    /// members while every other read surface gains one in this wave.
    /// </summary>
    [Fact]
    public async Task BlockList_CarriesNoAvatarOnAnyRow_D192()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(_factory, actor.Id);

        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = target.Username }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdBody = await FriendEndpointTest.SuccessJsonAsync(created);
        Assert.False(createdBody.RootElement.TryGetProperty("avatar", out _));

        using JsonDocument list = await FriendEndpointTest.SuccessJsonAsync(
            await session.GetAsync("/api/v1/blocks"));
        JsonElement rows = list.RootElement.GetProperty("items");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.All(
            rows.EnumerateArray(),
            row =>
            {
                Assert.Equal(3, row.EnumerateObject().Count());
                Assert.False(row.TryGetProperty("avatar", out _));
            });
    }
```

- [ ] **Step 4: Run them and watch them fail**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~NotificationPushHttpTests|FullyQualifiedName~SteamImportEndpointTests|FullyQualifiedName~BlockEndpointTests' > /tmp/5c.log 2>&1; tail -60 /tmp/5c.log"
```
Expected: FAIL â€” `NotificationWireShape_NamedActorCarriesTheirAvatar_D188` and `ProviderStates_Are200DataNotProblemResponses_D049` both throw `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.` (`actorAvatar` / `avatar` do not exist yet). `BlockList_CarriesNoAvatarOnAnyRow_D192` PASSES already and must still pass at the end of the task â€” it pins a surface this wave deliberately leaves at three fields.

- [ ] **Step 5: Add `actorAvatar`, sourced from the MaskedIdentity**

`src/PartyUp.Api/Contracts/Notifications/NotificationContracts.cs` â€” add the using:

```csharp
using System.Text.Json.Serialization;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Notifications;
using PartyUp.Domain.Notifications;
```

then `:31-61`:

```csharp
public sealed record NotificationResponse(
    Guid Id,
    string Type,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReadAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ActorUserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActorDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? ActorAvatar,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SubjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubjectTitle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OptionStartTime,
    bool Unread)
{
    public static NotificationResponse From(NotificationReadModel row) =>
        new(
            row.Id.Value,
            NotificationWireFormat.Type(row.Type),
            SessionWireFormat.Format(row.CreatedAt),
            row.ReadAt is { } readAt ? SessionWireFormat.Format(readAt) : null,
            // Â§4 wire rule: the actor's masking Reason -- not merely whether an actor object was
            // built -- decides whether the id is present. Blocked drops it; named/tombstoned/
            // profile-incomplete keep it (NotificationReadUseCases.ProjectAsync already encodes this
            // exactly in ActorUserId).
            row.ActorUserId,
            row.Actor is { DisplayName: { } displayName } ? displayName.Value : null,
            // D-188: the avatar comes off the same MaskedIdentity as the name. MaskedIdentity.Anonymous
            // carries a null avatar, so masking here is structural rather than a rule this mapper could
            // forget. Deriving it from row.ActorUserId instead would route around the mask entirely.
            row.Actor is { Avatar: { } actorAvatar } ? AvatarResponse.From(actorAvatar) : null,
            row.HasSubject ? NotificationWireFormat.SubjectKind(row.SubjectKind) : null,
            row.SubjectId,
            row.SubjectTitle?.Value,
            row.OptionStartTime is { } optionStartTime ? SessionWireFormat.Format(optionStartTime) : null,
            row.IsUnread);
}
```

No Application-layer change: `NotificationReadUseCases.ProjectAsync` already stores `IdentityMasking.ForUser(actorUser, viewer)`, and `IdentityMasking.Named` already passes `subject.Avatar` through.

- [ ] **Step 6: Carry the avatar on the Steam match**

`src/PartyUp.Application/Friends/ImportSteamFriendsUseCase.cs:240`:

```csharp
public sealed record SteamMatchedUser(string Username, Avatar Avatar);
```

and `:161-163`, inside the candidate loop:

```csharp
            string username = candidate.User.Username?.Value
                ?? throw new InvalidOperationException("A visible Steam match must have a username.");
            Avatar avatar = candidate.User.Avatar
                ?? throw new InvalidOperationException("A visible Steam match must have an avatar.");
            matches.Add(new SteamMatchedUser(username, avatar));
```

`PartyUp.Domain.Users` is already imported (`:5`). The throw mirrors the shipped username guard exactly: `LookupVisibility.CanMatchSteamImport` has already excluded tombstoned, banned and profile-incomplete candidates, and `User.Avatar` is non-null for every one that survives â€” so a null here is a broken invariant, not a rendering case.

- [ ] **Step 7: Add the wire field**

`src/PartyUp.Api/Endpoints/FriendEndpoints.cs:703-706` â€” the record only. Its `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]` attribute sits at `:702`, outside the range, and is not reproduced below; including it would land a second copy and fail the build on `CS0579`. Same namespace as `AvatarResponse`, so no using is needed:

```csharp
public sealed record SteamMatchedUserResponse(string Username, AvatarResponse Avatar)
{
    public static SteamMatchedUserResponse From(SteamMatchedUser user) =>
        new(user.Username, AvatarResponse.From(user.Avatar));
}
```

This one field is **not** `JsonIgnore(WhenWritingNull)`, and that is deliberate: the match list has no masked arm â€” a blocked or invisible candidate is dropped into `unmatched` before a `SteamMatchedUser` is ever constructed â€” so the field is non-nullable and always present. An optional field here would advertise a masking case that does not exist. D-191's carve-out is untouched: `SteamUnmatchedFriendResponse.AvatarUrl` (`:709`) keeps its raw upstream URL, because those people have no `User` row.

- [ ] **Step 8: Build with zero warnings**

Run:
```sh
dotnet build PartyUp.sln
```
Expected: PASS, `0 Warning(s)`.

- [ ] **Step 9: Run the tests**

Run:
```sh
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts --filter 'FullyQualifiedName~Notifications|FullyQualifiedName~SteamImport|FullyQualifiedName~BlockEndpointTests' > /tmp/5c.log 2>&1; tail -60 /tmp/5c.log"
```
Expected: PASS â€” including `BlockList_CarriesNoAvatarOnAnyRow_D192` (still three fields) and `OperationIds_S4Surface_AddsExactlyElevenIds`, whose `expectedTotalOperationIdCount = 136` (`NotificationPushHttpTests.cs:687`) needs **no bump**: wave A adds fields to existing response shapes and registers no new endpoint. If that test goes red, an endpoint was added by mistake â€” fix the endpoint, do not raise the pin.

- [ ] **Step 10: Regenerate the API types**

Restart the API from Task 5A Step 10 so it serves the new contracts (`Ctrl+C`, then `dotnet run --project src/PartyUp.Api`), then:
```sh
cd web
npm run gen:api
npm run lint && npm run build && npm test
```
Expected: `NotificationResponse` gains optional `actorAvatar?`, `SteamMatchedUserResponse` gains **required** `avatar` (`schema.d.ts` currently emits it as `{ username: string }` at `:2790`). That required field breaks `tsc` in `npm run build` at the only place the web tree builds match literals: `web/src/features/friends/SteamImportPage.test.tsx`, whose `SteamImportResult` is `components['schemas']['SteamFriendImportResponse']` (`friendGraph.ts:11`). Fix the four literals rather than loosening the contract â€” `:40`:

```tsx
        matches: [
          {
            username: 'ready.one',
            avatar: { kind: 'emoji', emoji: '🙂', sourcePlatformLinkId: null },
          },
        ],
```

and `:144-148`:

```tsx
      matches: [
        {
          username: 'player.one',
          avatar: { kind: 'emoji', emoji: '🙂', sourcePlatformLinkId: null },
        },
        {
          username: 'player.one',
          avatar: { kind: 'emoji', emoji: '🙂', sourcePlatformLinkId: null },
        },
        {
          username: 'block.one',
          avatar: { kind: 'emoji', emoji: '🙂', sourcePlatformLinkId: null },
        },
      ],
```

`emoji` and `sourcePlatformLinkId` are both required-but-nullable on the generated `AvatarResponse` (`schema.d.ts:1975-1980`), so all three keys must be present. Re-run `npm run lint && npm run build && npm test` until green. Shut down the API and `docker compose -f deploy/compose.dev.yml down` when the three tasks are done.

- [ ] **Step 11: Commit**

```bash
git add src/PartyUp.Api/Contracts/Notifications/NotificationContracts.cs \
        src/PartyUp.Application/Friends/ImportSteamFriendsUseCase.cs \
        src/PartyUp.Api/Endpoints/FriendEndpoints.cs \
        tests/PartyUp.Api.IntegrationTests/Notifications/NotificationPushHttpTests.cs \
        tests/PartyUp.Api.IntegrationTests/Friends/SteamImportEndpointTests.cs \
        tests/PartyUp.Api.IntegrationTests/Friends/BlockEndpointTests.cs \
        web/src/features/friends/SteamImportPage.test.tsx \
        web/src/api/schema.d.ts
git commit -m "feat(api): carry the avatar on notifications and Steam matches" -m "D-188 fan-out, wave A. The notification actor's avatar is read off the
MaskedIdentity the projection already built, never off ActorUserId: Anonymous()
carries a null avatar, so a blocked actor's face is absent for the same
structural reason their name and id are, and no mapper can opt out.

SteamMatchedUserResponse.Avatar is non-nullable on purpose. The match list has
no masked arm â€” an invisible or blocked candidate becomes an unmatched row
before a SteamMatchedUser exists â€” so an optional field would advertise a case
that cannot occur. The generated type turns the field required, which is why the
SteamImportPage match fixtures gain one. D-191's
SteamUnmatchedFriendResponse.AvatarUrl carve-out is untouched.

BlockedUserResponse is left at three fields and a test now pins that (D-192): a
live avatar on the blocked list would make blocking a monitoring channel. No
endpoint is added, so the pinned OpenAPI operation total stands."
```


### Task 6: render `AvatarDisc` on the wave-A surfaces

**Files:**
- Modify: `web/src/features/sessions/SessionDetailPage.tsx:549-564` (roster `<li>`), `:578-601` (waitlist `<li>`), `:1-9` (import block)
- Modify: `web/src/features/polls/PollDetailPage.tsx:860-871` (roster `<li>`), `:1-9` (import block)
- Modify: `web/src/features/notifications/NotificationCenterPage.tsx:82-84` (the `detail` derivation), `:111-152` (the row body), `:1-6` (import block)
- Modify: `web/src/features/friends/SteamImportPage.tsx:15` (`MatchCard` type), `:47-52` (the `matches` mapping), `:185-187` (the card heading)
- Test: `web/src/features/sessions/SessionDetailPage.test.tsx`, `web/src/features/polls/PollDetailPage.test.tsx`, `web/src/features/notifications/NotificationCenterPage.test.tsx`, `web/src/features/friends/SteamImportPage.test.tsx`

**Interfaces:**
- Consumes (each already through `npm run gen:api` by the task named): `RosterEntryResponse.avatar` — **task 5A**; `PollRosterEntryResponse.avatar` and `PollFinalizationPreviewRosterEntryResponse.avatar` — **task 5B**; `NotificationResponse.actorAvatar` and `SteamMatchedUserResponse.avatar` — **task 5C**. The first four and `actorAvatar` are **optional** on the wire — `avatar?: components["schemas"]["AvatarResponse"]` — because their C# properties carry `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, so a masked, tombstoned or guest row omits the member entirely rather than sending `null`; `actorAvatar` is named to sit beside the existing `actorUserId` / `actorDisplayName` pair. `SteamMatchedUserResponse.avatar` is the deliberate exception — task 5C types it `AvatarResponse Avatar`, **required and non-nullable**, because the match list has no masked arm (an invisible or blocked candidate becomes an unmatched row before a `SteamMatchedUser` exists). `AvatarResponse` is unchanged in wave A: `{ kind: string; emoji: null | string; sourcePlatformLinkId: null | string }`.
- Consumes (shipped): `AvatarDisc({ seed, emoji?, size?, className?, masked? })` from `web/src/features/friends/AvatarDisc.tsx` — renders an `aria-hidden="true"` `<span>`; `masked` forces the busy pair and the `'●'` glyph regardless of `emoji`.
- Produces: no new exports. Produces the four rendered call sites and the test names cited by Task 7's TRACEABILITY row.

**Decisions this task fixes, stated once (they are cited by later steps):**

**The seed rule.** `AvatarDisc`'s `seed` only chooses the fill variant (`stableFillVariant`, `AvatarDisc.tsx:24-30`); the emoji carries identity. Every shipped call site seeds on `friend.username`, but none of the wave-A DTOs carries a username. The rule for wave A is: **seed on the most stable identity the row already carries, preferring the user id.**

| Surface | Seed expression |
| --- | --- |
| Session roster / waitlist | `entry.userId ?? entry.guestIdentityId ?? entry.participationId` |
| Poll roster | `entry.userId ?? entry.guestIdentityId ?? entry.participationId` |
| Notification row | `notification.actorUserId ?? notification.id` |
| Steam matched card | `match.username` |

**The consequence, stated plainly:** for a registered user, `userId` hashes to a different fill variant than `username` does, so the same person's disc is (for example) accent-filled on the friend list and card-filled on a session roster. The emoji is identical; only the disc colour differs. The Steam card is the exception — it seeds on `username` and therefore *does* match.

I judge this acceptable and recommend shipping it, because the fill variant is explicitly documented as decoration with no semantic content (`AvatarDisc.tsx:1-7`), it is never the thing a user recognises, and a mismatch is invisible unless two surfaces are open side by side. **The alternative, so it is not hidden:** add `username: string | null` to the three roster DTOs in task 5B and seed on `entry.username ?? entry.participationId`. Usernames are already public on any profile the viewer can reach, so it leaks nothing new — but it widens task 5B's DTO surface, its OpenAPI inventory assertions and its masking tests for a purely cosmetic gain. If the owner wants colour identity to be global, that is the change to make, and it belongs in wave B alongside the picture path, not bolted onto this task.

**The masking rule (this is what "masking is structural" means at the render layer).** The disc's masked state is derived from the *same predicate the name already uses* — never from a separate flag and never from the avatar's own emptiness:

```
masked={entry.displayName === null}
```

The row that renders `t('detail.anonymous')` renders `'●'`; the row that renders a real name renders the real emoji. One predicate, two renderings — a future edit cannot desynchronise them. The server independently withholds `avatar` for masked rows (task 5B), so this is belt-and-braces, and the test below proves the disc stays neutral **even if a real emoji is handed to a nameless row**.

**The notification rule.** Only `FRIEND_TYPES` rows get a disc. Session/poll rows put a `subjectTitle` on the detail line — a session, not a person — and there is no person on the row to decorate. Attaching a disc to a title would assert a personhood the row does not have, and `actorUserId` on those rows names an actor the row never displays. So: `FRIEND_TYPES` → disc seeded on the actor; everything else → no disc, markup unchanged.

**Finalization preview: DTO yes, render no.** `PollFinalizationPreviewRosterEntryResponse.avatar` ships in task 5B and stays unrendered in wave A. `PollDetailPage.tsx:478-517` renders those names as a comma-joined i18n string (`t('finalize.going', { names: ... })`) inside a `<label>` that **wraps the radio input** (`:487-497`). The label's content is the radio's accessible name. Interpolating a component into that string is not possible without restructuring the label, and restructuring it risks silently rewriting the radio's accessible name — the exact D-188 hazard this task is guarding against everywhere else. The render is deferred to a dedicated change that restructures the finalize option into an explicitly-labelled control first; the field being on the wire means that change is UI-only.

---

- [ ] **Step 1: Write the failing session-roster tests**

Append to `web/src/features/sessions/SessionDetailPage.test.tsx` (inside the existing top-level `describe`):

```tsx
  it('renders an avatar disc on each roster row, decoration only_D188', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
        roster: [
          {
            participationId: 'p-named',
            userId: 'user-9',
            guestIdentityId: null,
            displayName: 'Ada',
            status: 'going',
            isWaitlisted: false,
            origin: 'invited',
            reminderChoice: null,
            avatar: { kind: 'emoji', emoji: '🦊', sourcePlatformLinkId: null },
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-named')
    const disc = row.querySelector('[aria-hidden="true"]')
    expect(disc).toHaveTextContent('🦊')
    // Decoration: the row still reads as the name alone to a screen reader.
    expect(row).toHaveTextContent('Ada')
  })

  it('a masked roster row renders the neutral disc and leaks no emoji_D188_D076', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
        roster: [
          {
            participationId: 'p-masked',
            userId: 'user-blocked',
            guestIdentityId: null,
            displayName: null,
            // Even if the server ever emitted one, the render must not show it.
            avatar: { kind: 'emoji', emoji: '🐉', sourcePlatformLinkId: null },
            status: 'going',
            isWaitlisted: false,
            origin: 'invited',
            reminderChoice: null,
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-masked')
    const disc = row.querySelector('[aria-hidden="true"]')
    expect(disc).toHaveTextContent('●')
    expect(disc).not.toHaveTextContent('🐉')
    expect(disc).toHaveClass('bg-busy')
  })

  it('the remove-guest control keeps its own accessible name, disc excluded_D188', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
        roster: [
          {
            participationId: 'p-guest',
            userId: null,
            guestIdentityId: 'guest-1',
            displayName: 'Guest Bob',
            status: 'going',
            isWaitlisted: false,
            origin: 'guestLink',
            reminderChoice: null,
            avatar: { kind: 'emoji', emoji: '🍕', sourcePlatformLinkId: null },
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-guest')
    const remove = within(row).getByRole('button')
    expect(remove).toHaveAccessibleName('Remove guest Guest Bob')
    expect(remove).not.toHaveAccessibleName(/🍕/)
  })
```

`detail()` at `SessionDetailPage.test.tsx:37` takes no parameters today (`function detail() {`), so widen its signature — the shipped call sites keep working because the parameter is defaulted, matching `pollDetail()`'s shape in `PollDetailPage.test.tsx:22`:

```tsx
function detail(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'session-1',
    // …existing fields unchanged…
    ...overrides,
  }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd web && npx vitest run src/features/sessions/SessionDetailPage.test.tsx`

Expected: FAIL — `renders an avatar disc on each roster row, decoration only_D188` fails with `expected null to have text content '🦊'` (`row.querySelector('[aria-hidden="true"]')` returns `null`; the roster `<li>` contains no aria-hidden element today). The masked case fails identically. The accessible-name case passes already — it is the regression guard, and it must still pass after Step 3.

- [ ] **Step 3: Implement the session roster and waitlist**

Add the import to `web/src/features/sessions/SessionDetailPage.tsx` (after line 5, keeping the existing alphabetical-by-path grouping):

```tsx
import { AvatarDisc } from '../friends/AvatarDisc'
```

Replace the roster row's name span (`:554`):

```tsx
                      <span className="text-ink">{entry.displayName ?? t('detail.anonymous')}</span>
```

with:

```tsx
                      <span className="flex min-w-0 items-center gap-2">
                        <AvatarDisc
                          emoji={entry.avatar?.emoji}
                          masked={entry.displayName === null}
                          seed={entry.userId ?? entry.guestIdentityId ?? entry.participationId}
                          size="xs"
                        />
                        <span className="truncate text-ink">
                          {entry.displayName ?? t('detail.anonymous')}
                        </span>
                      </span>
```

Apply the identical replacement to the waitlist row's name span (`:582`) — same expression, same props; the surrounding `<li>` already carries `flex items-center justify-between gap-2`, so the new wrapper becomes the left-hand flex child and the control span stays the right-hand one.

- [ ] **Step 4: Run the session tests**

Run: `cd web && npx vitest run src/features/sessions/SessionDetailPage.test.tsx`

Expected: PASS — all three new cases plus every pre-existing case in the file.

- [ ] **Step 5: Write the failing poll-roster test**

Append the roster case to the end of the top-level `describe('PollDetailPage', �)` in `web/src/features/polls/PollDetailPage.test.tsx` (before its closing `})` at `:959`). The default fixture's roster is `participation-1` / `participation-2` (`:64-79`), so this case supplies its own roster through `pollDetail`'s overrides parameter and the `pp-*` testids exist only because of that override:

```tsx
  it('renders an avatar disc on each poll roster row_D188', async () => {
    api.get.mockResolvedValue({
      data: pollDetail({
        roster: [
          {
            participationId: 'pp-1',
            userId: 'user-7',
            guestIdentityId: null,
            displayName: 'Ada',
            isActive: true,
            avatar: { kind: 'emoji', emoji: '🐙', sourcePlatformLinkId: null },
          },
          {
            participationId: 'pp-masked',
            userId: 'user-blocked',
            guestIdentityId: null,
            displayName: null,
            isActive: true,
            avatar: { kind: 'emoji', emoji: '⚡', sourcePlatformLinkId: null },
          },
        ],
      }),
    })
    renderPage()

    const named = await screen.findByTestId('poll-roster-entry-pp-1')
    expect(named.querySelector('[aria-hidden="true"]')).toHaveTextContent('🐙')

    const masked = screen.getByTestId('poll-roster-entry-pp-masked')
    const maskedDisc = masked.querySelector('[aria-hidden="true"]')
    expect(maskedDisc).toHaveTextContent('●')
    expect(maskedDisc).not.toHaveTextContent('⚡')
  })
```

The preview case needs the organizer + awaiting-selection state that actually mounts `FinalizeSection`, and that state's helpers (`awaitingSelectionPoll` at `:556`, `previewResponse` at `:566`) are local to the `describe('selection/finalize (S7, design §5/§6.1)', …)` block. So it goes **inside that describe**, appended before its closing `})` at `:746`, driven exactly the way the shipped finalize tests at `:594` and `:612` drive it:

```tsx
    it('the finalization preview renders no disc in wave A_D188', async () => {
      // Rows carrying an avatar, so the assertion proves a deliberate
      // non-render rather than an empty field.
      const preview = previewResponse()
      const previewWithAvatars = {
        options: preview.options.map((option) => ({
          ...option,
          roster: option.roster.map((row) => ({
            ...row,
            avatar: { kind: 'emoji', emoji: '�', sourcePlatformLinkId: null },
          })),
        })),
      }
      api.get.mockImplementation((path: string) =>
        Promise.resolve({
          data: path.includes('finalization-preview')
            ? previewWithAvatars
            : awaitingSelectionPoll(),
        }),
      )
      renderPage()

      const option = await screen.findByTestId('finalize-option-option-1')
      // The preview's names live inside a <label> wrapping the radio input;
      // a disc there would rewrite the radio's accessible name.
      expect(option.querySelector('[aria-hidden="true"]')).toBeNull()
      expect(within(option).getByRole('radio')).not.toHaveAccessibleName(/�/)
    })
```

- [ ] **Step 6: Run it and watch it fail**

Run: `cd web && npx vitest run src/features/polls/PollDetailPage.test.tsx`

Expected: FAIL — `renders an avatar disc on each poll roster row_D188` fails with `expected null to have text content '🐙'`. The finalization-preview case passes now and must keep passing.

- [ ] **Step 7: Implement the poll roster**

Add to `web/src/features/polls/PollDetailPage.tsx` imports (after line 6):

```tsx
import { AvatarDisc } from '../friends/AvatarDisc'
```

Replace `:865-867`:

```tsx
                  <span className="text-ink">
                    {entry.displayName ?? t('detail.anonymous')}
                  </span>
```

with:

```tsx
                  <span className="flex min-w-0 items-center gap-2">
                    <AvatarDisc
                      emoji={entry.avatar?.emoji}
                      masked={entry.displayName === null}
                      seed={entry.userId ?? entry.guestIdentityId ?? entry.participationId}
                      size="xs"
                    />
                    <span className="truncate text-ink">
                      {entry.displayName ?? t('detail.anonymous')}
                    </span>
                  </span>
```

`FinalizeSection` (`:478-517`) is left untouched.

- [ ] **Step 8: Run the poll tests**

Run: `cd web && npx vitest run src/features/polls/PollDetailPage.test.tsx`

Expected: PASS.

- [ ] **Step 9: Write the failing notification tests — including the accessible-name assertion**

Append to `web/src/features/notifications/NotificationCenterPage.test.tsx`:

```tsx
  it('a friend-graph row carries the actor disc and the link name excludes it_D188', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-friend',
          type: 'friendRequestReceived',
          actorUserId: 'user-3',
          actorDisplayName: 'Ada',
          actorAvatar: { kind: 'emoji', emoji: '🦉', sourcePlatformLinkId: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-friend')
    expect(row.querySelector('.notification-actor-disc')).toHaveTextContent('🦉')

    // The disc must never enter a control's accessible name. This is the
    // hazard the Tier 1 sweep found: getByLabelText saw the disc, but
    // computeAccessibleName did not. The name is the sr-only unread prefix
    // plus the type label (`NotificationCenterPage.tsx:104-107`), which for
    // `friendRequestReceived` is en's `item.types.friendRequestReceived`.
    const link = within(row).getByRole('link')
    expect(link).toHaveAccessibleName('Unread New friend request')
    expect(link).not.toHaveAccessibleName(/🦉/)
  })

  it('a session row shows its subject title and no person disc_D188', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-session',
          type: 'sessionInvitation',
          subjectKind: 'session',
          subjectId: '11111111-1111-1111-1111-111111111111',
          subjectTitle: 'Raid night',
          actorUserId: 'user-3',
          actorAvatar: { kind: 'emoji', emoji: '🔥', sourcePlatformLinkId: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-session')
    expect(row).toHaveTextContent('Raid night')
    // No person is named on this row, so nothing is decorated.
    expect(row.querySelector('.notification-actor-disc')).toBeNull()
  })

  it('a masked friend-graph row renders the neutral disc_D188_D076', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-masked',
          type: 'friendRequestReceived',
          actorUserId: 'user-blocked',
          actorDisplayName: null,
          actorAvatar: { kind: 'emoji', emoji: '🌵', sourcePlatformLinkId: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-masked')
    const disc = row.querySelector('.notification-actor-disc')
    expect(disc).toHaveTextContent('●')
    expect(disc).not.toHaveTextContent('🌵')
  })
```

The `.notification-actor-disc` hook is load-bearing, and a generic `[aria-hidden="true"][class*="rounded-full"]` selector will **not** serve: the unread dot at `NotificationCenterPage.tsx:114-118` is itself an `aria-hidden`, `rounded-full` span, and the `notification()` fixture at `NotificationCenterPage.test.tsx:17-26` defaults `unread: true` — so the session-row case's `toBeNull()` would match the dot and fail against a correct implementation. The hook is a plain marker class threaded through `AvatarDisc`'s existing `className` prop (declared `AvatarDisc.tsx:36`, appended verbatim to the class list at `:59`); it carries no styling, and no lint rule objects — `web/eslint.config.js` loads no Tailwind plugin. Neither the hook nor the row's `data-testid` exists yet; Step 11 adds both.

- [ ] **Step 10: Run it and watch it fail**

Run: `cd web && npx vitest run src/features/notifications/NotificationCenterPage.test.tsx`

Expected: FAIL — all three cases fail at `findByTestId`, with `Unable to find an element by: [data-testid="notification-n-friend"]` (and `-n-session`, `-n-masked`): the row carries no testid today, so the await times out before the disc assertion is even reached. Step 11 adds the testid and the disc together, after which only the disc assertions can fail.

- [ ] **Step 11: Implement the notification row**

Add to `web/src/features/notifications/NotificationCenterPage.tsx` imports (after line 5):

```tsx
import { AvatarDisc } from '../friends/AvatarDisc'
```

Replace the `detail` derivation (`:82-84`) with one that also decides the disc, so the two stay welded to the same predicate:

```tsx
  const namesAPerson = FRIEND_TYPES.has(notification.type)
  const detail = namesAPerson
    ? (notification.actorDisplayName ?? t('item.anonymous'))
    : (notification.subjectTitle ?? null)
```

Give the row a testid and hang the disc off the row's left edge, **outside** the `<Link>`. `return (` sits at `:110` and must stay; replace `:111-113` — the `<li>`, the wrapping `<div className="flex flex-wrap items-start justify-between gap-3">` and the `<div className="flex min-w-0 items-start gap-2">` — with:

```tsx
    <li
      className="rounded-card border-2 border-ink bg-card p-4 shadow-offset-card"
      data-testid={`notification-${notification.id}`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-2">
          {namesAPerson && (
            <AvatarDisc
              className="notification-actor-disc"
              emoji={notification.actorAvatar?.emoji}
              masked={notification.actorDisplayName === null}
              seed={notification.actorUserId ?? notification.id}
              size="xs"
            />
          )}
```

Everything from the unread-dot block at `:114` down is unchanged. The disc is a sibling of the block containing the `<Link>`, so it can never be part of the link's accessible name. `className="notification-actor-disc"` styles nothing — it exists so a test can tell the disc apart from the unread dot, which is also an `aria-hidden`, `rounded-full` span on the very same row.

- [ ] **Step 12: Run the notification tests**

Run: `cd web && npx vitest run src/features/notifications/NotificationCenterPage.test.tsx`

Expected: PASS.

- [ ] **Step 13: Write the failing Steam-import test**

Append to `web/src/features/friends/SteamImportPage.test.tsx`:

```tsx
  it('a matched card carries the avatar disc and the heading name excludes it_D188', async () => {
    api.post.mockResolvedValueOnce({
      data: {
        state: 'ready',
        matches: [
          {
            username: 'ready.one',
            avatar: { kind: 'emoji', emoji: '🎮', sourcePlatformLinkId: null },
          },
        ],
        unmatched: [],
        invitationUrl: 'https://links.test/add/23456789AB',
      },
    })
    renderPage()

    fireEvent.click(screen.getByRole('button', { name: 'Find Steam friends' }))

    const heading = await screen.findByRole('heading', { name: '@ready.one' })
    const card = heading.closest('article') as HTMLElement
    expect(card.querySelector('[aria-hidden="true"]')).toHaveTextContent('🎮')
    expect(heading).not.toHaveTextContent('🎮')
  })
```

`'Find Steam friends'` is the shipped trigger's exact accessible name, and `mockResolvedValueOnce` matches the idiom every other case in the file uses (`SteamImportPage.test.tsx:80`, `:106`, `:123`). The payload is written inline rather than through the file's `result('ready')` helper (`:34-55`) because that helper's `matches: [{ username: 'ready.one' }]` literal has no `avatar` — task 5C's required field is what forces that helper to be widened, and this case must not depend on the order in which that happens.

- [ ] **Step 14: Run it and watch it fail**

Run: `cd web && npx vitest run src/features/friends/SteamImportPage.test.tsx`

Expected: FAIL — `expected null to have text content '🎮'`; `MatchCard` drops the avatar on the way through `setView`, and the card renders no disc.

- [ ] **Step 15: Implement the Steam matched card**

`web/src/features/friends/SteamImportPage.tsx:15` — widen `MatchCard`:

```tsx
type MatchCard = {
  username: string
  state: MatchState
  avatar: { emoji: string | null }
}
```

`avatar` is non-optional here because task 5C types `SteamMatchedUserResponse.Avatar` as a required, non-nullable `AvatarResponse` — the match list has no masked arm, so widening this to `| null` would model a case the wire cannot produce.

`:47-52` — carry the avatar through the mapping:

```tsx
          'matches' in result
            ? result.matches.map((match) => ({
                username: match.username,
                state: 'actionable' as const,
                avatar: match.avatar,
              }))
            : [],
```

`:185-187` — put the disc beside the heading, not inside it (a Steam match is always a named account, so `masked` is never set here):

```tsx
                    <div className="flex items-center gap-2">
                      <AvatarDisc
                        emoji={match.avatar.emoji}
                        seed={match.username}
                        size="sm"
                      />
                      <h3 className="min-w-0 truncate font-mono font-semibold text-ink">
                        @{match.username}
                      </h3>
                    </div>
```

`AvatarDisc` is already imported at `:6`.

- [ ] **Step 16: Run the full web gate**

Run: `cd web && npm run lint && npm run build && npm test`

Expected: PASS — lint clean, `tsc` clean (the new `avatar` / `actorAvatar` reads type-check only because tasks 5A, 5B and 5C each ran `gen:api` and landed their fields in `src/api/schema.d.ts`), full vitest suite green.

- [ ] **Step 17: Commit**

```bash
git add web/src/features/sessions/SessionDetailPage.tsx \
        web/src/features/sessions/SessionDetailPage.test.tsx \
        web/src/features/polls/PollDetailPage.tsx \
        web/src/features/polls/PollDetailPage.test.tsx \
        web/src/features/notifications/NotificationCenterPage.tsx \
        web/src/features/notifications/NotificationCenterPage.test.tsx \
        web/src/features/friends/SteamImportPage.tsx \
        web/src/features/friends/SteamImportPage.test.tsx
git commit -m "feat(web): render avatar discs on the D-188 Tier 2 surfaces

Session roster and waitlist, poll roster, friend-graph notification rows
and Steam matched cards each gain an AvatarDisc. Seeds come from the ids
the DTOs already carry (userId, then guestIdentityId, then the row id;
username on the Steam card), so a registered user's disc fill can differ
from the friend list's username-seeded fill — the fill is decoration and
the emoji is identical, which is the accepted trade.

Masking is structural: the disc's masked flag reads the same
displayName === null predicate the anonymous label reads, so the two
cannot drift. Notification discs are limited to FRIEND_TYPES rows, the
only rows that name a person; session and poll rows show a subject title
and are left undecorated. The disc always sits outside the row's control,
proven by toHaveAccessibleName on the notification link and the
remove-guest button.

The finalization preview's DTO field ships unrendered: its names are a
comma-joined string inside a label wrapping the radio input, so a disc
there would rewrite the radio's accessible name."
```

---

### Task 7: land the spec, architecture and tracker amendments

**Files:**
- Modify: `PRODUCT_SPEC.md:681` (append three rows after the D-189 row, before the blank line at `:682` and `## 15. Open questions` at `:683`)
- Modify: `ARCHITECTURE.md:215` (append one row after the A-083 row, the last row of the �9 decision record)
- Modify: `TRACEABILITY.md:205` (rewrite the D-188 row), `:59` (D-032 row), `:77` (D-050 row)
- Modify: `TODO.md:7-10` (retire the Tier 2 item, replace with the wave-B remainder)

**Interfaces:**
- Consumes: the test names produced by Task 6 (`SessionDetailPage.test.tsx`, `PollDetailPage.test.tsx`, `NotificationCenterPage.test.tsx`, `SteamImportPage.test.tsx` `_D188` cases) and by Tasks 1–5 (`AvatarCatalogTests`, `AvatarPicker.test.tsx`, `i18n.keys.test.ts`, the task-5A/5B/5C masking and wire-shape tests).
- Produces: D-190, D-191, D-192 as `Confirmed` spec decisions and A-084 as an architecture decision — the identifiers wave B's tasks cite. **A-021 is already taken** (`ARCHITECTURE.md:153`, "Identity is a sibling `auth_users` table keyed 1:1 to `users.id`"); the §9 record runs to A-083 at `:215`, so the next free number is A-084, which is what `AVATARS_DESIGN.md:17` already uses.

- [ ] **Step 1: Add D-190, D-191 and D-192 to the decision table**

`PRODUCT_SPEC.md` — insert immediately after the **D-189** row, which is `:681`; `:680` is the D-188 row, so anchoring there would wedge the new rows between D-188 and D-189. The insertion therefore lands at `:682`, ahead of the blank line and `## 15. Open questions` at `:683`. The table's column order is `| number | decision | status |` and the status vocabulary in use is `Confirmed` / `Superseded by D-nnn`. Text is `AVATARS_DESIGN.md` §1 verbatim, with the design doc's file-relative back-references kept:

```markdown
| D-190 | Avatar sources include picture-bearing **auth** accounts (Google), not only gaming platform links. D-050's "linked platform account" reads "linked account". | Confirmed |
| D-191 | A PartyUp user's avatar picture is served only through the first-party proxy, from bytes re-encoded server-side at capture. Upstream URLs are never emitted to clients. **Carve-out:** `SteamUnmatchedFriendResponse.AvatarUrl` (`FriendEndpoints.cs:709`) keeps its raw steamstatic URL — those people have no `User` row and therefore no avatar source. | Confirmed |
| D-192 | The blocked-users list renders a neutral disc seeded from the stored username snapshot and carries no avatar. Amends D-188, which listed it as an avatar surface. | Confirmed |
```

- [ ] **Step 2: Add A-084 to the architecture decision record**

`ARCHITECTURE.md` — the §9 decision record is one table, header `| ID | Decision | Rationale |` at `:131`, and it ends at the A-083 row (`:215`), immediately before the blank line and `### Phase 2 implementation notes and accepted variances` at `:217`. Append the new row **after `:215`**. Do not insert after A-020: `:153` is already A-021 (Identity's `auth_users` table). The tail of this table carries the full statement in the Decision cell and citations in the Rationale cell (A-082, A-083 are the pattern), so A-084 follows that shape and the rationale paragraph rides in the Decision cell:

```markdown
| A-084 | Avatar proxy tokens are plaintext capability slugs, carved out of A-015; SixLabors.ImageSharp is admitted under A-013 for capture-time re-encoding. **Stated plainly because the consequence is permanent:** an avatar proxy token is minted once per source row, never rotated, and stored in plaintext. Possession of the URL is the authorization. An avatar picture is therefore *public-once-seen* — a viewer who was ever authorized keeps a working URL until the source row is deleted. Deletion of the source row (unlink, re-link, ban, account deletion) is the sole revocation authority, the way `guest_links.state` is for A-065. This is a deliberate trade of revocability for a pure, join-free read path; it is not an oversight, and the picture is the only thing exposed — never a name, a user id, or a relationship. The ImageSharp admission is what makes D-191's "never emit an upstream URL" enforceable, since the bytes must be re-encoded at capture. | A-013, A-015, A-065; D-190, D-191; `AVATARS_DESIGN.md` §1, §3. |
```

Note for the implementer: A-084's second clause is a **wave B/C** authorization. Nothing in wave A adds `SixLabors.ImageSharp` to `Directory.Packages.props`, and A-013 keeps forbidding it until the proxy task lands.

- [ ] **Step 3: Verify the two tables still parse**

Run: `cd C:/Users/nilot/source/repos/PartyUpSocial && git diff --stat PRODUCT_SPEC.md ARCHITECTURE.md`

Expected: `PRODUCT_SPEC.md | 3 +` and `ARCHITECTURE.md | 1 +`. Then eyeball `sed -n '678,686p' PRODUCT_SPEC.md` and `sed -n '213,218p' ARCHITECTURE.md` — the three spec rows must sit **after** D-189, not between D-188 and D-189; every inserted line must start with `| D-` / `| A-`, end with `|`, and carry exactly two interior `|` separators, matching its neighbours. Confirm no duplicate identifier survives: `grep -c "^| A-084 " ARCHITECTURE.md` returns `1`, and `grep -n "^| A-021 " ARCHITECTURE.md` still returns only the Identity row.

- [ ] **Step 4: Commit the spec amendments**

```bash
git add PRODUCT_SPEC.md ARCHITECTURE.md
git commit -m "docs(spec): confirm D-190..D-192 and A-084 for avatars

The avatar design ratified 2026-08-11 depends on four amendments that had
no numbers: auth accounts as avatar sources, the proxy-only picture path
with the SteamUnmatchedFriendResponse carve-out, the blocked-users list
dropping to a neutral disc, and the plaintext capability-token carve-out
from A-015. The architecture decision takes A-084, the next free number
after A-083; A-021 has been Identity's auth_users table since Phase 2.
A-084's ImageSharp clause authorizes the wave B/C package addition and
does not add it now."
```

- [ ] **Step 5: Rewrite the D-188 TRACEABILITY row**

`TRACEABILITY.md:205` currently reads `| D-188 | … | Client | pending | **Not yet implemented.** Tier 1 … shipped in \`9093a34\` … Backlog item: TODO.md "Avatars on the API-blocked surfaces (Tier 2)". |`. Replace the phase cell `pending` with the wave label and the evidence cell with real citations. Column order is `| number | decision | layer | phase | evidence |`, and shipped rows in this file cite bare test method names for .NET and `File.test.tsx` — `test name` for vitest:

```markdown
| D-188 | The D-050 avatar renders on session rosters and waitlists, poll voter lists and the finalization preview, notification rows, the blocked-users list, and Steam import matches. Friend request cards stay username-only, and masked participants keep the neutral treatment with the server withholding the avatar exactly as it withholds the display name (D-076). | Client | Avatars wave A | Tier 1 (friend selectors) shipped in `9093a34`. Wave A wire: `SessionRosterMaskingWireTests.AnUnmaskedRosterEntryCarriesTheParticipantAvatar_D188`, `SessionRosterMaskingWireTests.TheSessionListOrganizerMaskedParticipantCarriesNoAvatar_D188_D076`, `PollListAndPreviewTests.PreviewRosterEntriesCarryTheParticipantAvatar_D188`, `PollGuestHttpTests.GuestPollRoster_CarriesNoAvatarOnAnyRow_D150_D188`, `NotificationPushHttpTests.NotificationWireShape_NamedActorCarriesTheirAvatar_D188`, and the Steam match avatar asserted inside `SteamImportEndpointTests.ProviderStates_Are200DataNotProblemResponses_D049`. Withholding for a masked row is proven inside the extended D-076 tests themselves: `SessionRosterMaskingWireTests.AMaskedEntryHasNoUserIdPropertyWhileATombstonedOneKeepsIt_D076`, `PollEndpointTests.AMaskedPollRosterEntryHasNoUserIdProperty_D076`, `NotificationPushHttpTests.NotificationWireShape_BlockedPairActorOmitsBothActorFields_D076_D086`. Wave A web: `AvatarPicker.test.tsx` (the `_APG` roving-tabindex cases); `SessionDetailPage.test.tsx` — `renders an avatar disc on each roster row, decoration only_D188`, `a masked roster row renders the neutral disc and leaks no emoji_D188_D076`, `the remove-guest control keeps its own accessible name, disc excluded_D188`; `PollDetailPage.test.tsx` — `renders an avatar disc on each poll roster row_D188`, `the finalization preview renders no disc in wave A_D188`; `NotificationCenterPage.test.tsx` — `a friend-graph row carries the actor disc and the link name excludes it_D188`, `a session row shows its subject title and no person disc_D188`, `a masked friend-graph row renders the neutral disc_D188_D076`; `SteamImportPage.test.tsx` — `a matched card carries the avatar disc and the heading name excludes it_D188`. **Open:** the blocked-users list is amended out by D-192 (neutral disc, no avatar) and the finalization-preview **render** is deferred — its DTO field ships in wave A, but its names sit inside a `<label>` wrapping the radio input, so decorating them would rewrite the radio's accessible name. |
```

- [ ] **Step 6: Extend the D-032 and D-050 rows with the catalog citation**

`TRACEABILITY.md:59` (D-032) — append to the evidence cell, before the closing `|`:

```markdown
 Avatars wave A: the emoji field is now an application-layer allowlist, `AvatarCatalogTests.Catalog_HoldsTwentyFourGlyphs_D050` and `AvatarCatalogTests.Validate_RejectsAnOffCatalogGlyph_D050`.
```

`TRACEABILITY.md:77` (D-050) — append to the evidence cell, before the closing `|`:

```markdown
 Avatars wave A extends the choice to a fixed 24-glyph catalog validated server-side — `AvatarCatalogTests.Catalog_HoldsTwentyFourGlyphs_D050`, `AvatarCatalogTests.Validate_AcceptsEveryCatalogGlyph_D050`, `AvatarCatalogTests.Validate_RejectsAnOffCatalogGlyph_D050`, `AvatarCatalogTests.ChangeAvatar_RejectsAnOffCatalogGlyph_D050`, `AvatarCatalogTests.ChangeAvatar_AcceptsACatalogGlyph_D050` — and picked through `AvatarPicker.tsx`; D-190 widens "linked platform account" to "linked account".
```

Both are **extensions** — nothing already in those cells is deleted or weakened.

- [ ] **Step 7: Retire the TODO Tier 2 item and state what wave B still owes**

`TODO.md:7-10` — delete the whole `- **Avatars on the API-blocked surfaces (Tier 2) — scoped by D-188**` bullet with its three sub-bullets, and put in its place:

```markdown
- **Avatars wave B/C — picture sources (D-190/D-191/A-084)** — designed in `AVATARS_DESIGN.md`, not built. Wave A (emoji catalog + picker + the D-188 Tier 2 fan-out + structural masking) **shipped**; what remains:
  - **Wave B:** the `AvatarSource` entity and its capture pipeline — `SixLabors.ImageSharp` enters `Directory.Packages.props` under A-084 (A-013 forbids it until this task), bytes are re-encoded at capture, and the first-party proxy endpoint mints the A-084 plaintext capability token. `AvatarResponse` gains its `<img>` arm here; it is unchanged in wave A. Deleting the source row is the only revocation — no rotation, no expiry.
  - **Wave C:** Google as a picture-bearing auth account (D-190).
  - **Deferred out of wave A, both UI-only when picked up:** the poll finalization preview's **render** (`PollDetailPage.tsx:478-517` — its names are a comma-joined i18n string inside a `<label>` wrapping the radio input, so a disc there rewrites the radio's accessible name; the DTO field already ships), and global disc-fill consistency (wave-A roster discs seed on `userId`, friend-list discs on `username`, so the same person's fill colour differs across surfaces — fixable by putting `username` on the roster DTOs, cosmetic only).
  - **Amended out entirely:** the blocked-users list, by D-192 — neutral disc seeded from the stored username snapshot, no avatar. `IBlockRepository` deliberately holds no join to the blocked `User`; a live avatar there would make blocking a monitoring channel.
  - **Still binding from the original item:** `MaskedParticipant` stays excluded from avatars (join requests, organizer display, considered friends, calendar layer owners); friend request cards stay username-only; reuse `AvatarDisc`, never a second component; rerun `gen:api` after DTO changes and **extend, never loosen**, any inventory assertion.
```

- [ ] **Step 8: Commit the tracker updates**

```bash
git add TRACEABILITY.md TODO.md
git commit -m "docs: mark D-188 wave A shipped and re-scope the avatar backlog

The D-188 row moves off 'pending' onto the wave A tests, and D-032/D-050
gain the catalog citation. The TODO Tier 2 item is closed and replaced by
what wave B/C actually still owes: the AvatarSource entity, the proxy and
its ImageSharp dependency, Google, the deferred finalization-preview
render, and the cosmetic disc-fill seed mismatch."
```

- [ ] **Step 9: Run the full gate before any push**

Docker lives in WSL, not on the Windows host, so the integration suite only runs there. This is mandatory before pushing — never push integration tests that have not run.

Run (write to a log, do not pipe `tail` inside the tool call):

```bash
wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test PartyUp.sln -p:ArtifactsPath=/tmp/partyup-artifacts > /tmp/partyup-wave-a.log 2>&1; echo EXIT=\$?"
```

Expected: `EXIT=0`, and `Passed!` in `/tmp/partyup-wave-a.log` for every test project, with `0 Warning(s)` from the build (TreatWarningsAsErrors). Then confirm the web side one more time from `web/`: `npm run lint && npm run build && npm test` — all green.

- [ ] **Step 10: Push**

```bash
git push origin main
```

Expected: CI green (the `dotnet` job and the `browser` job), then CD green, then `curl -sS https://partyup.social/health` returns the healthy payload. Deploy-verify is not complete until that curl succeeds.


