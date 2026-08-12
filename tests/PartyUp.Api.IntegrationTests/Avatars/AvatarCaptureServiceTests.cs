using System.Net;
using System.Text;
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
        IPAddress? resolvedAddress = null)
    {
        var service = new AvatarCaptureService(
            new DelegateHandler(response),
            (_, _) => Task.FromResult(new[] { resolvedAddress ?? PublicAddress }),
            NullLogger<AvatarCaptureService>.Instance);
        using (service)
        {
            return await service.CaptureAsync(url, kind, CancellationToken.None);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
