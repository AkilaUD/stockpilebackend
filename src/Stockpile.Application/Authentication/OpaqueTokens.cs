using System.Security.Cryptography;
using System.Text;

namespace Stockpile.Application.Authentication;

/// <summary>
/// The ONE place an opaque bearer-style token is minted and hashed. Reset,
/// verification, refresh and registration-draft tokens are all the same shape:
/// 256 CSPRNG bits handed to the client, and only their SHA-256 hex stored at
/// rest (threat-model T1.5 "hashed at rest") — so a database breach yields no
/// usable token. Keeping one implementation is the point: a second hasher is a
/// second chance to weaken one.
/// </summary>
internal static class OpaqueTokens
{
    /// <summary>A 256-bit opaque token (base64 of 32 CSPRNG bytes).</summary>
    internal static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 hex of the token — the ONLY thing stored at rest.</summary>
    internal static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
