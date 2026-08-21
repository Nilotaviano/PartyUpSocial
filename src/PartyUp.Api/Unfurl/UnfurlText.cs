using System.Text;

namespace PartyUp.Api.Unfurl;

/// <summary>
/// D-224's handling of user-authored text on the unfurl surface: line breaks and runs of whitespace
/// collapse to single spaces before anything is rendered, and long values are clamped with an
/// ellipsis. HTML escaping of the clamped value is the caller's last step
/// (<see cref="System.Net.WebUtility.HtmlEncode(string)"/>) — this class never emits markup.
/// </summary>
public static class UnfurlText
{
    /// <summary>Strips CR/LF and control characters and collapses whitespace runs to one space.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sanitizes, then clamps to <paramref name="maxLength"/> characters inclusive of the ellipsis,
    /// preferring the last word boundary so a clamp never ends mid-word unless the first word is
    /// itself longer than the budget.
    /// </summary>
    public static string Clamp(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        string sanitized = Sanitize(value);
        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        int budget = maxLength - 1;
        string head = sanitized[..budget];
        int lastSpace = head.LastIndexOf(' ');
        if (lastSpace > budget / 2)
        {
            head = head[..lastSpace];
        }

        return head.TrimEnd() + "…";
    }

    /// <summary>Joins the non-empty parts of a description with the card's separator.</summary>
    public static string JoinParts(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)));
}
