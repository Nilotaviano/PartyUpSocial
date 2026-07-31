using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace PartyUp.Api.Auth;

public static class IntentBinder
{
    public static byte[] Create() => RandomNumberGenerator.GetBytes(32);

    public static string Hash(ReadOnlySpan<byte> binder) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(binder));

    public static string Encode(ReadOnlySpan<byte> binder) =>
        WebEncoders.Base64UrlEncode(binder);

    public static bool TryDecode(string value, out byte[] binder)
    {
        try
        {
            binder = WebEncoders.Base64UrlDecode(value);
            return binder.Length == 32;
        }
        catch (FormatException)
        {
            binder = [];
            return false;
        }
    }
}
