using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.Modules.Trust.Contracts;
using Karry.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// P2.4 "done when" (ADR-0034): generated shifts go through the whole sync pipeline, signed by a real enrolled tablet
/// (commands and envelopes), and read back from the portal's API with the right shift and Δ, flagged exactly where
/// the generator planted an anomaly. The fixture is <c>tests/vectors/generated-shifts.json</c>, from
/// <c>python3 tools/demo/generate_shifts.py --seed 12 --sites 1 --assets 1 --days 6 --anomaly-rate 0.3
/// --anomalies continuity_gap,negative_delta,oversized_delta,missing_end --replacement-rate 0</c>; its days are moved
/// to just after the test clock so the machine is on the site by then.
/// </summary>
[Trait("Story", "US-P2-FO-01")]
public sealed class ShiftEntriesSyncTests(SyncHost host) : IClassFixture<SyncHost>
{
    private static readonly JsonElement Generated =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "generated-shifts.json"))).RootElement;

    /// <summary>What the server flags for each anomaly the generator plants (the others are P2.7 rules).</summary>
    private static readonly Dictionary<string, string?> FlagOf = new(StringComparer.Ordinal)
    {
        ["continuity_gap"] = "RULE-FO-01",
        ["negative_delta"] = "RULE-FO-02",
        ["oversized_delta"] = "RULE-FO-02",
        ["missing_end"] = null,
    };

    /// <summary>The site's three eights, on its own clock (Africa/Tunis, UTC+1): the generator's UTC times fall inside them.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> ShiftCodeOf = new(StringComparer.Ordinal) { ["morning"] = "M", ["afternoon"] = "A", ["night"] = "N" };

    [Fact]
    [Trait("TestCase", "TC-P2-FO-001")]
    [Trait("TestCase", "TC-P2-FO-005")]
    public async Task Generated_shifts_load_through_the_sync_pipeline_and_read_back_with_the_right_shift_delta_and_flags()
    {
        var company = await host.CompanyAsync("shifts");
        var asset = Generated.GetProperty("assets")[0];
        var (assetId, meterId) = (asset.GetProperty("id").GetGuid(), Guid.CreateVersion7());
        var today = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime);
        var shift = today.AddDays(2).DayNumber - DateOnly.Parse(Generated.GetProperty("meta").GetProperty("start").GetString()!, CultureInfo.InvariantCulture).DayNumber;
        await SeedSiteAsync(company, assetId, meterId, asset.GetProperty("meter").GetProperty("initial").GetString()!, today);
        using var tablet = await host.TabletAsync(company);

        var commands = new List<JsonObject>();
        foreach (var entry in Generated.GetProperty("entries").EnumerateArray())
        {
            var operatorId = entry.GetProperty("operatorId").GetGuid();
            var start = new JsonObject
            {
                ["rootId"] = entry.GetProperty("id").GetString(),
                ["version"] = 1,
                ["assetId"] = assetId.ToString(),
                ["meterId"] = meterId.ToString(),
                ["startedAt"] = Moved(entry.GetProperty("start").GetProperty("at"), shift),
                ["startValue"] = entry.GetProperty("start").GetProperty("reading").GetString(),
                ["fuelStartPercent"] = 75,
                ["startPhoto"] = entry.GetProperty("start").GetProperty("photo").GetString(),
            };
            commands.Add(Command(tablet, commands.Count + 1, "fieldops.shift_entry.start.v1", start, 1, operatorId));
            if (entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Object)
            {
                var record = new JsonObject
                {
                    ["rootId"] = entry.GetProperty("id").GetString(),
                    ["version"] = 2,
                    ["endedAt"] = Moved(end.GetProperty("at"), shift),
                    ["endValue"] = end.GetProperty("reading").GetString(),
                    ["fuelEndPercent"] = 40,
                    ["endPhoto"] = end.GetProperty("photo").GetString(),
                };
                commands.Add(Command(tablet, commands.Count + 1, "fieldops.shift_entry.end.v1", record, 2, operatorId));
            }
        }

        var upload = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/batches", new { commands });
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));
        var receipt = await upload.Content.ReadFromJsonAsync<JsonElement>(Ct);
        receipt.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()).Should().OnlyContain(s => s == "accepted" || s == "conflict",
            "every signed fact is stored, flagged or not: " + receipt);

        var read = await host.SendAsync(HttpMethod.Get, $"/api/v1/fieldops/entries?siteId={company.North}&limit=200", host.Admin(company));
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
        var stored = (await read.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetGuid());

        stored.Should().HaveCount(Generated.GetProperty("entries").GetArrayLength());
        foreach (var entry in Generated.GetProperty("entries").EnumerateArray())
        {
            var got = stored[entry.GetProperty("id").GetGuid()];
            var because = $"entry {entry.GetProperty("id")} ({entry.GetProperty("date")} {entry.GetProperty("shift")})";
            got.GetProperty("shift").GetProperty("code").GetString().Should().Be(ShiftCodeOf[entry.GetProperty("shift").GetString()!], because);
            got.GetProperty("shift").GetProperty("date").GetString().Should()
                .Be(DateOnly.Parse(entry.GetProperty("date").GetString()!, CultureInfo.InvariantCulture).AddDays(shift).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), because);
            got.GetProperty("unit").GetString().Should().Be("h");
            got.GetProperty("operatorId").GetGuid().Should().Be(entry.GetProperty("operatorId").GetGuid(), "the operator is the envelope's signer");
            if (entry.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Object)
            {
                got.GetProperty("status").GetString().Should().Be("closed", because);
                got.GetProperty("usage").GetDecimal().Should()
                    .Be(decimal.Parse(end.GetProperty("reading").GetString()!, CultureInfo.InvariantCulture) - decimal.Parse(entry.GetProperty("start").GetProperty("reading").GetString()!, CultureInfo.InvariantCulture), because);
            }
            else
            {
                got.GetProperty("status").GetString().Should().Be("open", because);
            }

            var expected = entry.GetProperty("expected_flags").EnumerateArray().Select(f => FlagOf[f.GetString()!]).OfType<string>().ToHashSet();
            got.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToHashSet().Should().BeEquivalentTo(expected, because);
        }
    }

    /// <summary>A generator instant moved by whole days, as the record writes it.</summary>
    private static string Moved(JsonElement instant, int days) =>
        DateTimeOffset.Parse(instant.GetString()!, CultureInfo.InvariantCulture).AddDays(days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A sync command whose payload is the record, its envelope and the tablet's signature over the envelope (ADR-0034 §5).</summary>
    private static JsonObject Command(TestTablet tablet, int sequence, string type, JsonObject record, int version, Guid signer)
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

    /// <summary>The site's three eights from today, and the generated machine with its hour meter on the site since now.</summary>
    private async Task SeedSiteAsync(Company company, Guid assetId, Guid meterId, string initial, DateOnly today)
    {
        await host.ExecuteAsync(
            """
            INSERT INTO organization.shift_patterns (id, tenant_id, site_id, effective_from, shifts, created_at, changed_at)
            VALUES (gen_random_uuid(), $1, $2, $3, '[{"Code":"M","Name":"Matin","Start":"06:00","End":"14:00","Days":["mon","tue","wed","thu","fri","sat","sun"]},
              {"Code":"A","Name":"Après-midi","Start":"14:00","End":"22:00","Days":["mon","tue","wed","thu","fri","sat","sun"]},
              {"Code":"N","Name":"Nuit","Start":"22:00","End":"06:00","Days":["mon","tue","wed","thu","fri","sat","sun"]}]', now(), now())
            """, company.Id, company.North, today);
        await host.ExecuteAsync(
            """
            WITH asset AS (
              INSERT INTO assets.assets (id, tenant_id, code, name, type, make, model, site_id, status, label_token, created_at, changed_at)
              VALUES ($1, $2, 'S1-A01', 'Machine S1-A01', 'excavator', 'Caterpillar', '336', $3, 'Active', $4, now(), now())
              RETURNING id, tenant_id, site_id)
            INSERT INTO assets.site_assignments (tenant_id, asset_id, site_id, "from") SELECT tenant_id, id, site_id, now() FROM asset
            """, assetId, company.Id, company.North, $"GEN{assetId:N}"[..26].ToUpperInvariant());
        await host.ExecuteAsync(
            """
            INSERT INTO assets.meters (id, tenant_id, asset_id, kind, initial_reading, installed_on, status, "offset", last_value, last_read_at, created_at, changed_at)
            VALUES ($1, $2, $3, 'Hours', $4::numeric, '2026-01-01', 'Ok', 0, $4::numeric, '2026-01-01T00:00:00Z', now(), now())
            """, meterId, company.Id, assetId, initial);
    }
}
