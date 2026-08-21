using System.Net;
using System.Text;

namespace PartyUp.Api.Unfurl;

/// <summary>
/// D-224's crawler response body: an Open Graph and Twitter Card document with nothing else in it —
/// no script, no style, no link to the SPA bundle, and an empty body. Every interpolated value is
/// HTML-escaped, so a session title containing markup characters cannot break out of an attribute.
/// </summary>
public static class UnfurlDocument
{
    /// <summary>Renders the document for <paramref name="card"/> at the given absolute URLs.</summary>
    public static string Build(UnfurlCard card, string pageUrl, string imageUrl)
    {
        ArgumentNullException.ThrowIfNull(card);

        string title = WebUtility.HtmlEncode(card.Title);
        string description = WebUtility.HtmlEncode(card.Description);
        string page = WebUtility.HtmlEncode(pageUrl);
        string image = WebUtility.HtmlEncode(imageUrl);

        var builder = new StringBuilder(1024);
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        builder.Append("<title>").Append(title).Append("</title>");
        Property(builder, "og:title", title);
        Property(builder, "og:description", description);
        Property(builder, "og:image", image);
        Property(builder, "og:url", page);
        Property(builder, "og:type", "website");
        Property(builder, "og:site_name", "PartyUp");
        Name(builder, "twitter:card", "summary_large_image");
        Name(builder, "twitter:title", title);
        Name(builder, "twitter:description", description);
        Name(builder, "twitter:image", image);
        builder.Append("<link rel=\"canonical\" href=\"").Append(page).Append("\">");
        builder.Append("</head><body></body></html>");
        return builder.ToString();
    }

    private static void Property(StringBuilder builder, string property, string encodedContent) =>
        builder.Append("<meta property=\"").Append(property).Append("\" content=\"")
            .Append(encodedContent).Append("\">");

    private static void Name(StringBuilder builder, string name, string encodedContent) =>
        builder.Append("<meta name=\"").Append(name).Append("\" content=\"")
            .Append(encodedContent).Append("\">");
}
