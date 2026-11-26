using System.Text.Json;
using Karry.Modules.Trust.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>The image hash of each envelope's handwritten signature, read from the envelope as signed (RLS).</summary>
internal sealed class EnvelopeImages(TrustDbContext db) : IEnvelopeImages
{
    public async Task<IReadOnlyDictionary<Guid, string>> ImageHashesAsync(IReadOnlyCollection<Guid> envelopeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopeIds);
        if (envelopeIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var wanted = envelopeIds.ToList();
        var rows = await db.SignatureEnvelopes.AsNoTracking()
            .Where(e => wanted.Contains(e.EnvelopeId))
            .Select(e => new { e.EnvelopeId, e.Canonical })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var images = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            using var envelope = JsonDocument.Parse(row.Canonical);
            if (envelope.RootElement.TryGetProperty("handwritten", out var handwritten)
                && handwritten.TryGetProperty("imageHash", out var image) && image.GetString() is { Length: > 0 } hash)
            {
                images[row.EnvelopeId] = hash;
            }
        }

        return images;
    }
}
