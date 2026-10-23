using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Karry.SharedKernel.Canonical;

/// <summary>
/// JSON Canonicalization Scheme, RFC 8785 (US-P1-AUD-01 AC4): one exact byte sequence per JSON value, so a hash or a
/// signature over it is reproducible by any party (the server, the field app in TypeScript, an external auditor).
/// <list type="bullet">
/// <item>Object members sorted by the UTF-16 code units of their names; no whitespace.</item>
/// <item>Strings escaped as ECMAScript <c>JSON.stringify</c> does (only <c>"</c>, <c>\</c> and control characters).</item>
/// <item>Numbers serialized as ECMAScript <c>Number.prototype.toString</c> serializes an IEEE-754 double.</item>
/// </list>
/// Always rejected (I-JSON, RFC 7493): duplicate member names and lone surrogates. With <c>strictNumbers</c> (the default,
/// used by the ledger) numbers that a double cannot represent exactly are rejected too (e.g. 0.1 written with 20
/// digits, integers above 2^53), because the canonical form would silently change the value: exact decimals such as
/// money must travel as strings. <c>strictNumbers: false</c> is plain RFC 8785 (for interoperability vectors).
/// </summary>
public static class JsonCanonicalizer
{
    /// <summary>Canonical UTF-8 bytes of the JSON text.</summary>
    /// <exception cref="FormatException">Invalid JSON, or a value outside I-JSON (see type remarks).</exception>
    public static byte[] Canonicalize(string json, bool strictNumbers = true)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            return Canonicalize(document.RootElement, strictNumbers);
        }
    }

    public static byte[] Canonicalize(JsonElement element, bool strictNumbers = true)
    {
        var builder = new StringBuilder();
        try
        {
            Write(builder, element, strictNumbers);
        }
        catch (InvalidOperationException ex)
        {
            // System.Text.Json refuses to decode an escaped lone surrogate into a string.
            throw new FormatException($"Lone surrogate or invalid string (I-JSON requires valid Unicode): {ex.Message}", ex);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Canonical text (convenience for tests and logs; hash the bytes of <see cref="Canonicalize(string, bool)"/>).</summary>
    public static string CanonicalText(string json, bool strictNumbers = true) => Encoding.UTF8.GetString(Canonicalize(json, strictNumbers));

    private static void Write(StringBuilder output, JsonElement element, bool strict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(output, element, strict);
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    Write(output, item, strict);
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Append(SerializeNumber(element.GetRawText(), strict));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new FormatException($"Unexpected JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteObject(StringBuilder output, JsonElement element, bool strict)
    {
        var members = element.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
        // Ordinal comparison of .NET strings is the comparison of their UTF-16 code units, as RFC 8785 §3.2.3 requires.
        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        output.Append('{');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                if (string.Equals(members[i].Name, members[i - 1].Name, StringComparison.Ordinal))
                {
                    throw new FormatException($"Duplicate member name '{members[i].Name}' (I-JSON forbids it: which one would be hashed?).");
                }

                output.Append(',');
            }

            WriteString(output, members[i].Name);
            output.Append(':');
            Write(output, members[i].Value, strict);
        }

        output.Append('}');
    }

    private static void WriteString(StringBuilder output, string value)
    {
        output.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                output.Append(c).Append(value[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new FormatException("Lone surrogate in a string (I-JSON requires valid Unicode).");
            }

            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }

    /// <summary>ECMAScript Number::toString (ECMA-262 §6.1.6.1.20) of the double nearest to <paramref name="raw"/>.</summary>
    internal static string SerializeNumber(string raw, bool strict = true)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new FormatException($"Number '{raw}' is outside the IEEE-754 double range.");
        }

        if (value == 0)
        {
            return "0"; // also -0
        }

        // Shortest digits that round-trip ("R" is shortest round-trip since .NET Core 3.0): d1d2…dk × 10^(n-k).
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = shortest.StartsWith('-');
        var (digits, n) = DigitsAndExponent(negative ? shortest[1..] : shortest);
        if (strict)
        {
            RejectInexact(raw, value);
        }

        var k = digits.Length;
        var text = n switch
        {
            _ when k <= n && n <= 21 => digits + new string('0', n - k),
            _ when 0 < n && n <= 21 => digits[..n] + "." + digits[n..],
            _ when -6 < n && n <= 0 => "0." + new string('0', -n) + digits,
            _ => digits[..1] + (k > 1 ? "." + digits[1..] : string.Empty) + "e" + (n - 1 >= 0 ? "+" : "-") + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture),
        };
        return negative ? "-" + text : text;
    }

    /// <summary>"4.5E-05" → ("45", -4): value = 0.45 × 10^-4, i.e. n is the position of the decimal point.</summary>
    private static (string Digits, int N) DigitsAndExponent(string shortest)
    {
        var exponentAt = shortest.IndexOfAny(['E', 'e']);
        var mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerPart = point < 0 ? mantissa : mantissa[..point];
        var allDigits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var n = integerPart.Length + exponent;
        var leadingZeros = allDigits.Length - allDigits.TrimStart('0').Length;
        var digits = allDigits.Trim('0');
        return (digits, n - leadingZeros);
    }

    /// <summary>The text must denote exactly the double it parses to; otherwise the canonical form would silently change the value.</summary>
    private static void RejectInexact(string raw, double value)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var written))
        {
            return; // beyond decimal's range (|x| > 7.9e28 or tiny): exactness is judged by the double alone
        }

        var asDouble = value.ToString("R", CultureInfo.InvariantCulture);
        if (!decimal.TryParse(asDouble, NumberStyles.Float, CultureInfo.InvariantCulture, out var represented) || represented != written)
        {
            throw new FormatException($"Number '{raw}' is not exactly representable as a double (canonical {asDouble}); send exact decimals as strings.");
        }
    }
}
