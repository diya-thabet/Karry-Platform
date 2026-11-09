using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Karry.Modules.Trust.Contracts;

/// <summary>
/// What the server countersigns for an envelope (ADR-0033 §4): the RFC 8785 text of
/// <c>{"envelopeHash", "receivedAt", "v": 1}</c>, with <c>receivedAt</c> in UTC to the microsecond (the database's
/// precision, so the stored value always gives back the signed text). The server key signs SHA-256 of that text.
/// </summary>
public static class Countersignatures
{
    public static string Text(string envelopeHash, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeHash);
        if (!envelopeHash.StartsWith("sha256:", StringComparison.Ordinal) || envelopeHash.Length != 71 || !envelopeHash[7..].All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("An envelope hash is sha256: and 64 lowercase hex characters.", nameof(envelopeHash));
        }

        var at = receivedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        return $$"""{"envelopeHash":"{{envelopeHash}}","receivedAt":"{{at}}","v":1}""";
    }

    public static byte[] Digest(string envelopeHash, DateTimeOffset receivedAt) => SHA256.HashData(Encoding.UTF8.GetBytes(Text(envelopeHash, receivedAt)));
}
