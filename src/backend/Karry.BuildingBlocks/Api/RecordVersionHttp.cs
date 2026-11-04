using System.Globalization;
using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// <c>ETag</c> and <c>If-Match</c> for <see cref="RecordVersions"/>: the tag is the version in quotes (<c>"4711"</c>).
/// Without <c>If-Match</c> (or with <c>*</c>) an update applies to whatever version is current; P1.18a keeps it
/// optional so older clients keep working, and makes it required once every client sends it.
/// </summary>
public static class RecordVersionHttp
{
    public const string InvalidCode = "request.if_match_invalid";

    public static string ETag(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>The version an update is meant for: null without <c>If-Match</c> or with <c>*</c>.</summary>
    public static Result<uint?> Expected(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch) || ifMatch.Trim() == "*")
        {
            return (uint?)null;
        }

        var tag = ifMatch.Trim();
        tag = tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
        Result<uint?> parsed = tag.Length > 2 && tag[0] == '"' && tag[^1] == '"'
            && uint.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? (Result<uint?>)(uint?)version
            : Error.Validation(InvalidCode, "If-Match must be one version tag as the API sent it in ETag, for example \"4711\".");
        return parsed;
    }

    /// <summary>Sets <c>ETag</c> on the answer, so a client that read one record can update it safely.</summary>
    public static void SetETag(this HttpResponse response, uint version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = ETag(version);
    }
}
