using System.Globalization;
using System.Security.Cryptography;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Identity.Domain;

/// <summary>
/// The PIN of a field user (US-P1-IAM-03 AC2): 6 digits, not trivially guessable, stored only as a salted
/// PBKDF2-HMAC-SHA256 hash (600 000 iterations, OWASP 2023). The tablet unlock that uses it offline arrives in P1.15;
/// its brute-force protection is the device lockout there, not the hash alone (a 6-digit space is small).
/// </summary>
public static class Pin
{
    public const int Length = 6;
    public const int Iterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static Result<string> Hash(string? pin)
    {
        if (pin is not { Length: Length } || !pin.All(char.IsAsciiDigit))
        {
            return Error.Validation("identity.pin_invalid", $"The PIN must be exactly {Length} digits.");
        }

        if (IsTrivial(pin))
        {
            return Error.Validation("identity.pin_too_simple", "The PIN cannot be a repeated digit or a run such as 123456.");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Constant-time check of <paramref name="pin"/> against a stored hash; false for any malformed hash.</summary>
    public static bool Verify(string? pin, string? stored)
    {
        var parts = (stored ?? string.Empty).Split('$');
        if (pin is null || parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsTrivial(string pin)
    {
        var steps = pin.Zip(pin.Skip(1), (a, b) => b - a).Distinct().ToList();
        return steps is [0] or [1] or [-1];
    }
}
