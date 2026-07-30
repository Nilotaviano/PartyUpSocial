# Spike B — Steam integration (OpenID 2.0 login + Web API)

De-risks Phase 2/3 Steam work: OpenID 2.0 login, friend/profile/library access via the
official Web API, and catalog access. The **private friends list** case (Steam's default)
is treated as a first-class outcome, not an error.

Throwaway code. One file, .NET 10 file-based app, no project.

## Get a Steam Web API key

1. Go to <https://steamcommunity.com/dev/apikey> (needs a Steam account that has spent ≥ $5).
2. Domain name: anything, e.g. `localhost` — it is not enforced for these endpoints.
3. Copy the key. Treat it like a password; it is sent as a query param on every Web API call.

The login flow itself needs **no key** — OpenID verification is keyless. The key is only
for the profile/friends/games calls after login.

## Run

```powershell
# from repo root, PowerShell
$env:STEAM_API_KEY = "YOURKEY"     # optional but needed for the interesting parts
dotnet run spikes/spike-b-steam/steam-spike.cs
```

Then open <http://localhost:5179>.

## What to click

1. **/** — shows whether `STEAM_API_KEY` is set, plus the result of your last login.
2. **/login** — redirects to steamcommunity.com; sign in there (credentials go to Steam,
   never to this app). Steam redirects back to `/callback`.
3. **/callback** — verifies the assertion server-side (`check_authentication` POST back to
   Steam, requires `is_valid:true`), extracts your steamid64, then (if key set) calls the
   three Web API endpoints and renders each outcome.
4. **/catalog** — no-key public checks: storefront `appdetails` for TF2 (appid 440) and a
   probe of `ISteamApps/GetAppList` (historically a ~10-15 MB keyless dump, stream-parsed
   to a count + first 5 names; now returns 404 — see findings. Append `?applist=0` to skip).
   If a key is set, the page also demos the `IStoreService/GetAppList` replacement.

## What each outcome means

| Call | Outcome | Meaning |
|---|---|---|
| check_authentication | VALID | Real Steam assertion; steamid64 is trustworthy. Nonces are single-use: refreshing `/callback` shows FAILED — expected. |
| check_authentication | FAILED | Replayed/tampered/hand-crafted callback. Reject. |
| GetPlayerSummaries | always OK | Persona name + avatar returned **regardless of privacy** — safe basis for the account-linking UI. `communityvisibilitystate` 3 = public, 1 = private/friends-only. |
| GetFriendList | HTTP 401 | **Friends list is private — Steam's default.** Not an error: PartyUp degrades to no friend import and tells the user how to open it (Profile → Privacy Settings → Friends List → Public). Key is proven valid by the summaries call, so 401 ⇒ privacy. |
| GetFriendList | 200 + list | Public list: steamid + friend_since only — display names require a batched GetPlayerSummaries (≤100 ids/call). |
| GetOwnedGames | `{"response":{}}` | **Game details private** (HTTP 200, empty object, no error field). Fall back to manual game selection. |
| GetOwnedGames | 200 + games | Library available; `include_appinfo=1` gives names directly. |
| appdetails (storefront) | OK | Unofficial, undocumented API. ~200 req / 5 min / IP, effectively one appid per request. On-demand + cache only, never bulk. |
| ISteamApps/GetAppList v2 | HTTP 404 | **Endpoint removed by Valve** (`Method 'GetAppList' not found in interface 'ISteamApps'`). The keyless full-catalog dump is gone. |
| IStoreService/GetAppList v1 | OK (key required) | The replacement: paginated (`max_results` ≤ 50000, loop on `have_more_results` / `last_appid`), API key mandatory. Background-job territory. |

## Findings

Verified by the spike itself (no human needed):

- OpenID 2.0 redirect builds correctly; Steam serves the login page for it (302 target
  checked; params match the spec: `checkid_setup`, `identifier_select`, return_to/realm).
- The `check_authentication` verification round-trip works: posting a forged assertion to
  `/callback` gets a real `is_valid:false` from Steam and the app rejects it. No OpenID
  library is needed — it's one form POST and a line parse (the ASP.NET Core `AddSteam`
  aspnet-contrib package exists for the real implementation, but the protocol is trivially
  hand-rollable).
- Storefront `appdetails` works keyless and returns rich data (name/type/pricing) —
  verified live against appid 440 (Team Fortress 2).
- **`ISteamApps/GetAppList` is dead** (was the assumed catalog source): every version
  returns HTTP 404 with `Method 'GetAppList' not found in interface 'ISteamApps'`, and the
  keyless `GetSupportedAPIList` no longer lists it. `IStoreService/GetAppList/v1` is the
  replacement — key-required (403 without one), paginated. Plan impact: catalog sync needs
  an API key and a pagination loop; it was never request-path material anyway.
- The spike still carries a Utf8JsonReader streaming parser (flat memory, count + first 5
  names, 64 MB guard) wired to the old endpoint and reusable for any large response.

Needs the human pass (requires real Steam credentials + API key):

- Complete an actual `/login` → Steam sign-in → `/callback` round trip (assertion VALID path).
- Confirm GetFriendList 401 on a default-privacy account, and 200 after flipping the
  friends list to public.
- Confirm GetOwnedGames empty-vs-populated against the same privacy toggles.
- Exercise the keyed `IStoreService/GetAppList` fallback on `/catalog` (page size,
  `have_more_results` behavior, whether names come back empty for delisted apps).
- Rate-limit feel for the Web API key (documented cap: 100k calls/day).

Production notes for Phase 2 (beyond spike scope):

- Also validate `openid.return_to` echoes our own URL and pin `openid.op_endpoint` to
  `https://steamcommunity.com/openid/login` before trusting an assertion.
- steamid64 is an identifier, not a secret — but the Web API key is a secret (query-param
  transmitted): keep it server-side, never log request URLs unsanitized.
