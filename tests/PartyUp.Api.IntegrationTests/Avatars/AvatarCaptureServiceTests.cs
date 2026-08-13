using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Avatars;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PartyUp.Api.IntegrationTests.Avatars;

/// <summary>
/// AVATARS_DESIGN.md §5's capture rejection obligations, plus the happy-path re-encode. Every case
/// runs against a fake <see cref="HttpMessageHandler"/> and a fake host resolver -- no live network,
/// per §3.2's own point that the capture pipeline is the only place that ever calls out.
/// </summary>
public sealed class AvatarCaptureServiceTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task CaptureAsync_RejectsNonHttps_D191()
    {
        AvatarCapture? result = await ExecuteAsync(
            "http://img.steamstatic.com/avatar.png",
            AvatarSourceKind.SteamPlatform,
            _ => PngResponse());

        Assert.Null(result);
    }

    [Theory]
    [InlineData("https://evil.example.com/avatar.png", AvatarSourceKind.SteamPlatform)]
    [InlineData("https://lh3.googleusercontent.com/a/pic.png", AvatarSourceKind.SteamPlatform)]
    [InlineData("https://avatars.steamstatic.com/a/pic.png", AvatarSourceKind.GoogleAccount)]
    public async Task CaptureAsync_RejectsOffAllowlistHosts_D191(string url, AvatarSourceKind kind)
    {
        AvatarCapture? result = await ExecuteAsync(url, kind, _ => PngResponse());

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAsync_RejectsARedirectResponse_D191()
    {
        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://internal.example/secret");
                return response;
            });

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAsync_RejectsAnOversizeBody()
    {
        byte[] tooLarge = new byte[512 * 1024 + 1];
        // PNG-signed so an oversize rejection is provably about size, not the magic-byte sniff.
        byte[] png = Png();
        Array.Copy(png, tooLarge, Math.Min(png.Length, tooLarge.Length));

        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tooLarge),
            });

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAsync_RejectsSvgBytes()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns='http://www.w3.org/2000/svg'></svg>");

        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(svg),
            });

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAsync_RejectsAPrivateResolvedAddress_D191()
    {
        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ => PngResponse(),
            resolvedAddress: IPAddress.Parse("10.0.0.5"));

        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureAsync_ReEncodesAHappyPathPngToWebp()
    {
        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Png())
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") },
                },
            });

        Assert.NotNull(result);
        AvatarCapture capture = result!;
        Assert.Equal("image/webp", capture.ContentType);
        Assert.NotEmpty(capture.Bytes);

        using Image<Rgba32> reEncoded = Image.Load<Rgba32>(capture.Bytes);
        Assert.Equal(128, reEncoded.Width);
        Assert.Equal(128, reEncoded.Height);
    }

    /// <summary>
    /// A pixel/decompression bomb: a tiny body that passes the 512 KB transfer cap and the magic-byte
    /// sniff but declares dimensions whose decode would allocate hundreds of megabytes. The header
    /// pre-check must reject it before any pixel buffer is allocated.
    /// </summary>
    [Fact]
    public async Task CaptureAsync_RejectsAHighDimensionImageBeforeDecoding_D191()
    {
        byte[] bomb = HighDimensionPng(30_000, 30_000);
        Assert.True(bomb.Length < 512 * 1024);

        RecordingLogger logger = new();
        AvatarCapture? result = await ExecuteAsync(
            "https://avatars.steamstatic.com/a/pic.png",
            AvatarSourceKind.SteamPlatform,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bomb),
            },
            logger: logger);

        Assert.Null(result);

        // The header pre-check turned it away, not a decoder exception downstream of an allocation.
        Assert.Contains(logger.Messages, message => message.Contains("decode rejected", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites a real 16x16 PNG's IHDR width/height (and its CRC) so the stream advertises
    /// <paramref name="width"/> x <paramref name="height"/> to any header reader.
    /// </summary>
    private static byte[] HighDimensionPng(int width, int height)
    {
        byte[] png = Png();
        const int ihdrTypeOffset = 12;
        const int ihdrDataOffset = 16;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(ihdrDataOffset, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(ihdrDataOffset + 4, 4), height);
        uint crc = Crc32(png.AsSpan(ihdrTypeOffset, 4 + 13));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(ihdrDataOffset + 13, 4), crc);
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Png()
    {
        using var image = new Image<Rgba32>(16, 16);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static HttpResponseMessage PngResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Png()),
    };

    private static async Task<AvatarCapture?> ExecuteAsync(
        string url,
        AvatarSourceKind kind,
        Func<HttpRequestMessage, HttpResponseMessage> response,
        IPAddress? resolvedAddress = null,
        ILogger<AvatarCaptureService>? logger = null)
    {
        var service = new AvatarCaptureService(
            new DelegateHandler(response),
            (_, _) => Task.FromResult(new[] { resolvedAddress ?? PublicAddress }),
            logger ?? NullLogger<AvatarCaptureService>.Instance);
        using (service)
        {
            return await service.CaptureAsync(url, kind, CancellationToken.None);
        }
    }

    private sealed class RecordingLogger : ILogger<AvatarCaptureService>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
