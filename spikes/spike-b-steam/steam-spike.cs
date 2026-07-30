#:sdk Microsoft.NET.Sdk.Web

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string BaseUrl = "http://localhost:5179";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(BaseUrl);
var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY") ?? "";
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("PartyUpSpikeB/0.1");

// Result of the most recent /callback, re-shown on the status page.
var lastLoginHtml = "";

// ---------- helpers ----------

static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

static string Page(string title, string body) => $$"""
    <!doctype html>
    <html><head><meta charset="utf-8"><title>{{H(title)}}</title>
    <style>
      body { font-family: system-ui, sans-serif; max-width: 52rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
      code { background: #eee; padding: 0 .25rem; }
      .ok   { color: #0a7d33; font-weight: 600; }
      .warn { color: #b06000; font-weight: 600; }
      .err  { color: #b00020; font-weight: 600; }
      img.avatar { width: 64px; height: 64px; vertical-align: middle; border-radius: 6px; }
      section { border: 1px solid #ccc; border-radius: 8px; padding: 0 1rem 1rem; margin: 1rem 0; }
    </style></head><body>
    <p><a href="/">home</a> · <a href="/login">login</a> · <a href="/catalog">catalog</a></p>
    {{body}}
    </body></html>
    """;

string Sanitize(string s) => apiKey.Length > 0 ? s.Replace(apiKey, "***KEY***") : s;

string Err(string what, Exception ex) =>
    $"<h3>{H(what)} → <span class=err>unexpected error</span></h3><p><code>{H(ex.GetType().Name)}: {H(Sanitize(ex.Message))}</code></p>";

async Task<string> SummariesSection(string steamId)
{
    try
    {
        var body = await http.GetStringAsync(
            $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={steamId}");
        using var doc = JsonDocument.Parse(body);
        var players = doc.RootElement.GetProperty("response").GetProperty("players");
        if (players.GetArrayLength() == 0)
            return "<h3>GetPlayerSummaries → <span class=err>no player returned</span></h3><p>Unexpected: the steamid came from a verified login, so it should always resolve.</p>";

        var p = players[0];
        var persona = p.GetProperty("personaname").GetString();
        var avatar = p.TryGetProperty("avatarfull", out var av) ? av.GetString() : null;
        var vis = p.TryGetProperty("communityvisibilitystate", out var v) ? v.GetInt32() : 0;
        var visLabel = vis == 3
            ? "<span class=ok>3 = public (profile visible to this API key)</span>"
            : $"<span class=warn>{vis} = private/friends-only (profile NOT visible to us)</span>";
        var avatarImg = avatar is null ? "" : $"<img class=avatar src=\"{H(avatar)}\" alt=\"avatar\"> ";
        return $"""
            <h3>GetPlayerSummaries → <span class=ok>OK</span></h3>
            <p>{avatarImg}persona: <b>{H(persona)}</b><br>
            communityvisibilitystate: {visLabel}</p>
            <p>Persona name + avatar are always returned regardless of privacy — safe to use for account linking UI.</p>
            """;
    }
    catch (Exception ex) { return Err("GetPlayerSummaries", ex); }
}

async Task<string> FriendsSection(string steamId)
{
    try
    {
        using var resp = await http.GetAsync(
            $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={apiKey}&steamid={steamId}&relationship=friend");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return """
                <h3>GetFriendList → <span class=warn>friends list is PRIVATE (HTTP 401)</span></h3>
                <p>This is Steam's <b>default</b> privacy setting and a first-class outcome, not an error.
                PartyUp must degrade gracefully: no friend import, show the user how to open it
                (Steam profile → Privacy Settings → Friends List → Public) and offer a re-check.
                Since GetPlayerSummaries succeeded with the same key, a 401 here means privacy, not a bad key.</p>
                """;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var friends = doc.RootElement.GetProperty("friendslist").GetProperty("friends");
        var count = friends.GetArrayLength();
        var sample = string.Join(", ", friends.EnumerateArray().Take(5).Select(f => f.GetProperty("steamid").GetString()));
        return $"""
            <h3>GetFriendList → <span class=ok>PUBLIC ({count} friends)</span></h3>
            <p>First steamids: <code>{H(sample)}</code><br>
            Note: only steamid + friend_since come back — resolving names needs a batched GetPlayerSummaries call (max 100 ids per call).</p>
            """;
    }
    catch (Exception ex) { return Err("GetFriendList", ex); }
}

async Task<string> OwnedGamesSection(string steamId)
{
    try
    {
        var body = await http.GetStringAsync(
            $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=1&format=json");
        using var doc = JsonDocument.Parse(body);
        var response = doc.RootElement.GetProperty("response");
        if (!response.TryGetProperty("game_count", out var gc))
            return """
                <h3>GetOwnedGames → <span class=warn>game details are PRIVATE</span></h3>
                <p>Steam returns an empty <code>{"response":{}}</code> object (HTTP 200, no error!) when
                "Game details" privacy is not public. A first-class outcome: PartyUp cannot pre-fill
                the user's library and should fall back to manual game selection.</p>
                """;
        var names = response.TryGetProperty("games", out var games)
            ? string.Join(", ", games.EnumerateArray().Take(5).Select(g =>
                g.TryGetProperty("name", out var n) ? n.GetString() : g.GetProperty("appid").GetRawText()))
            : "";
        return $"""
            <h3>GetOwnedGames → <span class=ok>PUBLIC ({gc.GetInt32()} games)</span></h3>
            <p>First 5: {H(names)}</p>
            """;
    }
    catch (Exception ex) { return Err("GetOwnedGames", ex); }
}

// The keyed replacement for the removed ISteamApps/GetAppList: one page of
// IStoreService/GetAppList, proving the pagination contract.
async Task<string> StoreServiceAppListSection(CancellationToken ct)
{
    try
    {
        var body = await http.GetStringAsync(
            $"https://api.steampowered.com/IStoreService/GetAppList/v1/?key={apiKey}&include_games=true&max_results=5000", ct);
        using var doc = JsonDocument.Parse(body);
        var response = doc.RootElement.GetProperty("response");
        var apps = response.GetProperty("apps");
        var count = apps.GetArrayLength();
        var names = string.Join(", ", apps.EnumerateArray().Take(5)
            .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : a.GetProperty("appid").GetRawText()));
        var hasMore = response.TryGetProperty("have_more_results", out var hm) && hm.GetBoolean();
        var lastAppId = response.TryGetProperty("last_appid", out var la) ? la.GetRawText() : "n/a";
        return $"""
            <h3>IStoreService/GetAppList/v1 (keyed replacement) → <span class=ok>OK</span></h3>
            <p>Page of {count:N0} apps. First 5: {H(names)}<br>
            have_more_results: <code>{hasMore}</code>, last_appid: <code>{H(lastAppId)}</code> —
            full catalog sync = background job looping with <code>last_appid</code> until <code>have_more_results</code> is false.</p>
            """;
    }
    catch (Exception ex) { return Err("IStoreService/GetAppList", ex); }
}

// Streams the ~10-15 MB GetAppList response through Utf8JsonReader without ever
// materializing it: counts appids, keeps the first 5 names, guards total size.
async Task<(long Bytes, int Count, List<string> Names)> StreamAppListAsync(CancellationToken ct)
{
    using var resp = await http.GetAsync(
        "https://api.steampowered.com/ISteamApps/GetAppList/v2/?format=json",
        HttpCompletionOption.ResponseHeadersRead, ct);
    resp.EnsureSuccessStatusCode();
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);

    var buffer = new byte[128 * 1024];
    var state = new JsonReaderState();
    long totalBytes = 0;
    var count = 0;
    var names = new List<string>(5);
    var leftover = 0;
    var expectName = false;
    const long maxBytes = 64L * 1024 * 1024; // download guard

    while (true)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(leftover, buffer.Length - leftover), ct);
        totalBytes += read;
        if (totalBytes > maxBytes) throw new InvalidOperationException("Download guard tripped: response exceeded 64 MB.");
        var isFinal = read == 0;
        var consumed = ProcessChunk(buffer.AsSpan(0, leftover + read), isFinal, ref state, ref count, names, ref expectName);
        leftover = leftover + read - consumed;
        if (leftover > 0) buffer.AsSpan(consumed, leftover).CopyTo(buffer);
        if (isFinal) break;
    }
    return (totalBytes, count, names);
}

static int ProcessChunk(ReadOnlySpan<byte> span, bool isFinal, ref JsonReaderState state,
    ref int count, List<string> names, ref bool expectName)
{
    var reader = new Utf8JsonReader(span, isFinal, state);
    while (reader.Read())
    {
        if (reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("appid")) count++;
            expectName = reader.ValueTextEquals("name");
        }
        else if (reader.TokenType == JsonTokenType.String && expectName)
        {
            if (names.Count < 5) names.Add(reader.GetString() ?? "");
            expectName = false;
        }
        else
        {
            expectName = false;
        }
    }
    state = reader.CurrentState;
    return (int)reader.BytesConsumed;
}

// ---------- endpoints ----------

app.MapGet("/", () =>
{
    var keyStatus = apiKey.Length > 0
        ? "<p><span class=ok>STEAM_API_KEY is set</span> — after login the spike calls GetPlayerSummaries, GetFriendList and GetOwnedGames.</p>"
        : """
          <p><span class=warn>STEAM_API_KEY is NOT set</span> — login still verifies the OpenID assertion
          and extracts your steamid64, but Web API calls are skipped.
          Get a key at <a href="https://steamcommunity.com/dev/apikey">steamcommunity.com/dev/apikey</a>
          and restart with the env var set.</p>
          """;
    var last = lastLoginHtml.Length > 0
        ? $"<h2>Last login result</h2>{lastLoginHtml}"
        : "<p>No login attempted yet this run.</p>";
    return Results.Content(Page("Steam spike", $"""
        <h1>Spike B — Steam integration</h1>
        {keyStatus}
        <ul>
          <li><a href="/login">/login</a> — Steam OpenID 2.0 login (redirects to steamcommunity.com)</li>
          <li><a href="/catalog">/catalog</a> — catalog checks, no key needed (storefront appdetails + GetAppList probe; historically a ~10-15 MB download, now a 404 finding — see the page)</li>
        </ul>
        {last}
        """), "text/html; charset=utf-8");
});

app.MapGet("/login", () =>
{
    var p = new Dictionary<string, string>
    {
        ["openid.ns"] = "http://specs.openid.net/auth/2.0",
        ["openid.mode"] = "checkid_setup",
        ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
        ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select",
        ["openid.return_to"] = $"{BaseUrl}/callback",
        ["openid.realm"] = BaseUrl,
    };
    var qs = string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    return Results.Redirect($"https://steamcommunity.com/openid/login?{qs}");
});

app.MapGet("/callback", async (HttpRequest req) =>
{
    var openidParams = req.Query
        .Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal))
        .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    if (openidParams.Count == 0)
        return Results.Content(Page("callback", "<p class=err>No openid.* parameters received. Start at <a href=\"/login\">/login</a>.</p>"), "text/html; charset=utf-8");

    // Server-side verification: echo every received openid.* param back to Steam
    // with mode=check_authentication; Steam answers is_valid:true exactly once per nonce.
    var verify = new Dictionary<string, string>(openidParams) { ["openid.mode"] = "check_authentication" };
    using var verifyResp = await http.PostAsync("https://steamcommunity.com/openid/login", new FormUrlEncodedContent(verify));
    var verifyBody = await verifyResp.Content.ReadAsStringAsync();
    var isValid = verifyBody.Split('\n').Any(l => l.Trim() == "is_valid:true");

    var sb = new StringBuilder("<h1>Login callback</h1>");
    if (!isValid)
    {
        sb.Append($"""
            <section><h3>Assertion verification → <span class=err>FAILED</span></h3>
            <p>Steam's check_authentication response did not contain <code>is_valid:true</code>
            (nonce replay, tampered params, or a hand-crafted callback URL). Raw response:</p>
            <pre>{H(verifyBody.Trim())}</pre></section>
            """);
        lastLoginHtml = sb.ToString();
        return Results.Content(Page("callback — failed", sb.ToString()), "text/html; charset=utf-8");
    }

    var claimedId = openidParams.GetValueOrDefault("openid.claimed_id", "");
    var m = Regex.Match(claimedId, @"^https?://steamcommunity\.com/openid/id/(\d{17})$");
    if (!m.Success)
    {
        sb.Append($"<section><h3><span class=err>claimed_id has unexpected shape</span></h3><p><code>{H(claimedId)}</code></p></section>");
        lastLoginHtml = sb.ToString();
        return Results.Content(Page("callback — failed", sb.ToString()), "text/html; charset=utf-8");
    }

    var steamId = m.Groups[1].Value;
    sb.Append($"""
        <section><h3>Assertion verification → <span class=ok>VALID</span></h3>
        <p>steamid64: <b><code>{H(steamId)}</code></b> (extracted from openid.claimed_id)</p></section>
        """);

    if (apiKey.Length == 0)
    {
        sb.Append("<section><p><span class=warn>STEAM_API_KEY not set</span> — skipping GetPlayerSummaries / GetFriendList / GetOwnedGames.</p></section>");
    }
    else
    {
        sb.Append($"<section>{await SummariesSection(steamId)}</section>");
        sb.Append($"<section>{await FriendsSection(steamId)}</section>");
        sb.Append($"<section>{await OwnedGamesSection(steamId)}</section>");
    }

    lastLoginHtml = sb.ToString();
    return Results.Content(Page("callback — result", sb.ToString()), "text/html; charset=utf-8");
});

app.MapGet("/catalog", async (HttpRequest req, CancellationToken ct) =>
{
    var sb = new StringBuilder("<h1>Catalog checks (no API key needed)</h1>");

    // 1. Unofficial storefront API — rich per-app data, but undocumented and
    //    rate-limited to roughly 200 requests / 5 minutes per IP.
    sb.Append("<section>");
    try
    {
        var body = await http.GetStringAsync("https://store.steampowered.com/api/appdetails?appids=440", ct);
        using var doc = JsonDocument.Parse(body);
        var entry = doc.RootElement.GetProperty("440");
        if (entry.GetProperty("success").GetBoolean())
        {
            var data = entry.GetProperty("data");
            var name = data.GetProperty("name").GetString();
            var type = data.GetProperty("type").GetString();
            var isFree = data.TryGetProperty("is_free", out var f) && f.GetBoolean();
            sb.Append($"""
                <h3>store.steampowered.com/api/appdetails?appids=440 → <span class=ok>OK</span></h3>
                <p>name: <b>{H(name)}</b>, type: {H(type)}, is_free: {isFree}</p>
                <p class=warn>Caveat: this is the UNOFFICIAL storefront API — undocumented, ~200 requests / 5 min
                per IP, one appid per request in practice. Fine for on-demand lookups with caching; not for bulk sync.</p>
                """);
        }
        else
        {
            sb.Append("<h3>appdetails → <span class=err>success:false</span></h3><p>Steam refused this appid (delisted or region-locked).</p>");
        }
    }
    catch (Exception ex) { sb.Append(Err("appdetails", ex)); }
    sb.Append("</section>");

    // 2. Official GetAppList — historically a keyless ~10-15 MB full-catalog dump.
    //    Streamed, never echoed; reduced to a count and the first 5 names.
    //    As of mid-2026 Steam returns 404 for it — handled as a finding, with the
    //    key-required IStoreService/GetAppList as the replacement.
    sb.Append("<section>");
    if (req.Query["applist"] == "0")
    {
        sb.Append("<h3>ISteamApps/GetAppList/v2 → skipped (<code>?applist=0</code>)</h3>");
    }
    else
    {
        try
        {
            var started = TimeProvider.System.GetTimestamp();
            var (bytes, count, names) = await StreamAppListAsync(ct);
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            sb.Append($"""
                <h3>ISteamApps/GetAppList/v2 → <span class=ok>OK</span></h3>
                <p>{count:N0} apps, {bytes / (1024.0 * 1024.0):F1} MB downloaded and stream-parsed in {elapsed.TotalSeconds:F1}s
                (Utf8JsonReader over the response stream — the document is never held in memory).</p>
                <p>First 5 names: {H(string.Join(", ", names))}</p>
                <p>Implication: catalog sync must be a background job with the response streamed to the DB, never a request-path call.</p>
                """);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            sb.Append("""
                <h3>ISteamApps/GetAppList/v2 → <span class=warn>HTTP 404 — endpoint REMOVED by Valve</span></h3>
                <p>Steam's error body says <code>Method 'GetAppList' not found in interface 'ISteamApps'</code>, and
                <code>ISteamWebAPIUtil/GetSupportedAPIList</code> confirms the method is gone from the keyless surface.
                The famous free full-catalog dump no longer exists. Replacement:
                <code>IStoreService/GetAppList/v1</code> — <b>requires an API key</b> and is paginated
                (<code>max_results</code> ≤ 50000, loop on <code>have_more_results</code>/<code>last_appid</code>).</p>
                """);
            sb.Append(apiKey.Length > 0
                ? await StoreServiceAppListSection(ct)
                : "<p><span class=warn>STEAM_API_KEY not set</span> — cannot demo the IStoreService replacement here. Re-run with the key set to exercise it.</p>");
        }
        catch (Exception ex) { sb.Append(Err("GetAppList", ex)); }
    }
    sb.Append("</section>");

    return Results.Content(Page("catalog", sb.ToString()), "text/html; charset=utf-8");
});

Console.WriteLine($"Spike B listening on {BaseUrl}  (STEAM_API_KEY {(apiKey.Length > 0 ? "set" : "NOT set")})");
app.Run();
