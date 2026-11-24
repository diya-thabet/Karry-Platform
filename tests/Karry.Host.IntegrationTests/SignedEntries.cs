using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.Modules.Trust.Contracts;
using Karry.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>Shift entry commands as an enrolled tablet signs them (ADR-0034 §5), for the host tests that go through sync.</summary>
internal static class SignedEntries
{
    /// <summary>A sync command whose payload is the record, its envelope and the tablet's signature over the envelope.</summary>
    public static JsonObject Command(TestTablet tablet, int sequence, string type, int version, Guid signer, JsonObject record)
    {
        using var recordDocument = JsonDocument.Parse(record.ToJsonString());
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["envelopeId"] = Guid.CreateVersion7().ToString(),
            ["recordType"] = "fieldops.shift_entry",
            ["recordId"] = record["rootId"]!.GetValue<string>(),
            ["recordVersion"] = version,
            ["payloadHash"] = SignatureEnvelopes.PayloadHash(recordDocument.RootElement),
            ["signer"] = new JsonObject { ["userId"] = signer.ToString(), ["role"] = "operator", ["deviceId"] = tablet.Id.ToString() },
            ["context"] = new JsonObject { ["deviceTime"] = "2026-11-09T06:02:11.250+00:00", ["appVersion"] = "1.4.0" },
        };
        var signature = tablet.SignEnvelope(envelope);
        return tablet.Command(sequence, new JsonObject { ["record"] = record, ["envelope"] = envelope, ["deviceSignature"] = signature }, type: type);
    }

    /// <summary>An instant as a record writes it.</summary>
    public static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Three eights on the company's north site from yesterday, so an entry falls in a shift.</summary>
    public static Task SeedShiftsAsync(SyncHost host, Company company) =>
        host.ExecuteAsync(
            """
            INSERT INTO organization.shift_patterns (id, tenant_id, site_id, effective_from, shifts, created_at, changed_at)
            VALUES (gen_random_uuid(), $1, $2, $3, '[{"Code":"M","Name":"Matin","Start":"06:00","End":"14:00","Days":["mon","tue","wed","thu","fri","sat","sun"]},
              {"Code":"A","Name":"Après-midi","Start":"14:00","End":"22:00","Days":["mon","tue","wed","thu","fri","sat","sun"]},
              {"Code":"N","Name":"Nuit","Start":"22:00","End":"06:00","Days":["mon","tue","wed","thu","fri","sat","sun"]}]', now(), now())
            """, company.Id, company.North, DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime).AddDays(-1));
}
