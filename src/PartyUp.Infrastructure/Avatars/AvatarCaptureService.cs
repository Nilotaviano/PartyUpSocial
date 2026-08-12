using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Users;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

// The fake-handler/fake-resolver test seam below is internal so production code cannot construct it
// with anything other than the real SocketsHttpHandler and Dns.GetHostAddressesAsync.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PartyUp.Api.IntegrationTests")]

namespace PartyUp.Infrastructure.Avatars;

/// <summary>
/// AVATARS_DESIGN.md §3.2's six-step capture pipeline, run once at write time -- never on the proxy's
/// read path. Owns a <em>dedicated</em> <see cref="SocketsHttpHandler"/>: never the shared singleton
/// <c>HttpClient</c> registered in <c>DependencyInjection.cs</c>, which Steam OpenID verification also
/// uses and which allows redirects and carries no size cap.
/// </summary>
public sealed partial class AvatarCaptureService : IAvatarCaptureService, IDisposable
{
    private const int MaxBytes = 512 * 1024;
    private const int OutputSize = 128;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// §3.2 step 1's host allowlist, scoped per <see cref="AvatarSourceKind"/> so a Steam-sourced
    /// picture can never be fetched from a googleusercontent.com URL or vice versa.
    /// </summary>
    private static readonly Dictionary<AvatarSourceKind, string> AllowedHostSuffixes =
        new Dictionary<AvatarSourceKind, string>
        {
            [AvatarSourceKind.SteamPlatform] = "steamstatic.com",
            [AvatarSourceKind.GoogleAccount] = "googleusercontent.com",
        };

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly ILogger<AvatarCaptureService> _logger;

    public AvatarCaptureService(ILogger<AvatarCaptureService> logger)
        : this(CreateHandler(), Dns.GetHostAddressesAsync, logger)
    {
    }

    /// <summary>Test seam: a fake handler and host resolver, so capture rejections never touch the network.</summary>
    internal AvatarCaptureService(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost,
        ILogger<AvatarCaptureService> logger)
    {
        _http = new HttpClient(handler)
        {
            Timeout = TotalTimeout,
            MaxResponseContentBufferSize = MaxBytes,
        };
        _resolveHost = resolveHost;
        _logger = logger;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = ConnectTimeout,
        // Step 3's host resolution above only vets a snapshot; the OS resolver could hand the actual
        // connect a different (rebound) address for the same hostname a moment later. Pinning the
        // connect itself to a freshly re-vetted address closes that TOCTOU/DNS-rebinding gap.
        // SocketsHttpHandler still layers TLS with SNI/Host from the original hostname on top of the
        // stream this returns, so nothing about the request's identity changes.
        ConnectCallback = ConnectToVettedAddressAsync,
    };

    private static async ValueTask<Stream> ConnectToVettedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        IPAddress? vetted = Array.Find(addresses, address => !IsDisallowedAddress(address));
        if (vetted is null)
        {
            throw new HttpRequestException("No allowed address resolved for the upstream host.");
        }

        var socket = new Socket(vetted.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(vetted, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<AvatarCapture?> CaptureAsync(
        string upstreamUrl,
        AvatarSourceKind kind,
        CancellationToken cancellationToken)
    {
        // Step 1: https only, host in the explicit per-kind allowlist.
        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null;
        }

        if (!AllowedHostSuffixes.TryGetValue(kind, out string? suffix) || !IsAllowedHost(uri.Host, suffix))
        {
            return null;
        }

        // Step 3: resolve the host and reject loopback/link-local/private/ULA before connecting.
        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHost(uri.Host, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return null;
        }

        if (addresses.Length == 0 || Array.Exists(addresses, IsDisallowedAddress))
        {
            return null;
        }

        byte[]? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            // AllowAutoRedirect = false means a redirect never gets followed; it just surfaces
            // here as a non-success (3xx) status, indistinguishable in handling from any other
            // upstream rejection.
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            body = await ReadBoundedAsync(response, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or TimeoutException &&
            !cancellationToken.IsCancellationRequested)
        {
            // A stalled body read (Steam/Google connection idle mid-response) surfaces here too --
            // TaskCanceledException derives from OperationCanceledException, so callers filtering on
            // "not OperationCanceledException" (sign-in, the nightly refresh batch) must never see it
            // unless it was the caller's own cancellationToken that fired.
            return null;
        }

        // Step 4: the cap was exceeded and the read aborted.
        if (body is null)
        {
            return null;
        }

        // Step 5: magic-byte sniff -- PNG, JPEG or WebP only. Never SVG (or anything else).
        if (!LooksLikeAllowedImage(body))
        {
            return null;
        }

        // Step 6: re-encode to 128px WebP; the upstream bytes are never stored verbatim (D-032).
        return ReEncode(body);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsAllowedHost(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsDisallowedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6UniqueLocal)
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true, // 0.0.0.0/8 "this network"
            10 => true, // 10.0.0.0/8
            100 when octets[1] is >= 64 and <= 127 => true, // 100.64.0.0/10 CGNAT
            127 => true, // loopback, redundant with IsLoopback above but kept explicit
            169 when octets[1] == 254 => true, // 169.254.0.0/16 link-local
            172 when octets[1] is >= 16 and <= 31 => true, // 172.16.0.0/12
            192 when octets[1] == 0 && octets[2] == 0 => true, // 192.0.0.0/24 IETF protocol assignments
            192 when octets[1] == 168 => true, // 192.168.0.0/16
            198 when octets[1] is 18 or 19 => true, // 198.18.0.0/15 benchmarking
            >= 224 => true, // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved (incl. 255.255.255.255)
            _ => false,
        };
    }

    private static bool LooksLikeAllowedImage(byte[] bytes) =>
        IsPng(bytes) || IsJpeg(bytes) || IsWebp(bytes);

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
        bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    private AvatarCapture? ReEncode(byte[] bytes)
    {
        try
        {
            using Image image = Image.Load(bytes);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(OutputSize, OutputSize),
                Mode = ResizeMode.Crop,
            }));

            using var output = new MemoryStream();
            image.Save(output, new WebpEncoder());
            return new AvatarCapture(output.ToArray(), AvatarSource.AllowedContentType);
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
        {
            LogReEncodeFailed(_logger, exception.GetType().Name);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Avatar re-encode failed after passing the magic-byte sniff ({ExceptionType})")]
    private static partial void LogReEncodeFailed(ILogger logger, string exceptionType);
}
