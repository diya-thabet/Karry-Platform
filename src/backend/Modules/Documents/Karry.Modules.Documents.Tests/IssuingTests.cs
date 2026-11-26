using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Storage;
using Karry.Modules.Documents.Api;
using Karry.Modules.Documents.Infrastructure;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// Issuing a shift report (ADR-0040 §3, US-P2-SIG-05, TC-P2-DOC-001): the PDF with its signature images, an envelope
/// keeping the content hash, the PDF's hash and only the token's hash, and the issue in the ledger with both hashes.
/// </summary>
[Trait("Story", "US-P2-SIG-05")]
public sealed class IssuingTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P2-DOC-001")]
    public async Task A_shift_report_is_issued_as_a_pdf_with_an_envelope_whose_hashes_are_ledgered_and_whose_token_is_kept_hashed()
    {
        var tenant = await ProvisionAsync();
        var (site, tablet) = (Site(), Guid.CreateVersion7());
        var (entry, envelope) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.Field.Entries[site] = [Entry(entry, envelope, tablet, 1250.0m)];
        var stroke = ShiftReportPdf.Qr(new Uri("https://karry.test/stroke"));
        var image = Convert.ToHexStringLower(SHA256.HashData(stroke));
        host.Field.Images[envelope] = "sha256:" + image;
        host.Field.Evidence[EvidenceKeys.For(tenant, tablet, image)] = stroke;

        using var response = await IssueAsync(tenant, site, "M");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var id = Guid.Parse(response.Headers.GetValues(DocumentsModule.EnvelopeHeader).Single());
        var pdf = await response.Content.ReadAsByteArrayAsync(Ct);
        Encoding.Latin1.GetString(pdf).Should().StartWith("%PDF-");
        System.Text.RegularExpressions.Regex.Count(Encoding.Latin1.GetString(pdf), @"/Subtype\s*/Image").Should().BeGreaterThanOrEqualTo(2, "the signature image from the evidence and the QR");

        var row = (await RowAsync($"SELECT content_hash || '|' || pdf_hash || '|' || token_hash || '|' || site_id || '|' || shift_code FROM documents.envelopes WHERE id = '{id}'")).Split('|');
        row[1].Should().Be("sha256:" + Convert.ToHexStringLower(SHA256.HashData(pdf)), "the envelope keeps the hash of exactly these bytes");
        row[0].Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        row[2].Should().MatchRegex("^[0-9a-f]{64}$", "only the token's hash is stored");
        (row[3], row[4]).Should().Be((site.ToString(), "M"));
        var ledger = JsonDocument.Parse(await RowAsync(
            $"SELECT payload FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'documents.shift_report_issued.v1'")).RootElement;
        (ledger.GetProperty("contentHash").GetString(), ledger.GetProperty("pdfHash").GetString(), ledger.GetProperty("envelopeId").GetGuid())
            .Should().Be((row[0], row[1], id), "the hashes are in the ledger from the moment the document is issued");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-DOC-001")]
    public async Task A_shift_of_two_hundred_entries_is_issued_in_under_ten_seconds_and_each_issue_is_a_new_envelope()
    {
        var tenant = await ProvisionAsync();
        var site = Site();
        host.Field.Entries[site] = [.. Enumerable.Range(0, 200).Select(i => Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1000m + (i * 8)))];

        var clock = Stopwatch.StartNew();
        using var first = await IssueAsync(tenant, site, "M");
        clock.Stop();
        using var second = await IssueAsync(tenant, site, "M");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        first.Headers.GetValues(DocumentsModule.EnvelopeHeader).Single().Should().NotBe(second.Headers.GetValues(DocumentsModule.EnvelopeHeader).Single());
    }

    [Fact]
    public async Task An_unknown_site_is_not_found_and_a_shift_code_is_required()
    {
        var tenant = await ProvisionAsync();

        using var unknown = await IssueAsync(tenant, Guid.CreateVersion7(), "M");
        using var noShift = await IssueAsync(tenant, Site(), null);

        (unknown.StatusCode, noShift.StatusCode).Should().Be((HttpStatusCode.NotFound, HttpStatusCode.BadRequest));
    }

    private Guid Site()
    {
        var site = new SiteSummary(Guid.CreateVersion7(), "CAR-KAK", "Carrière de Kakoulima", "Africa/Conakry", false);
        host.Field.Sites[site.Id] = site;
        return site.Id;
    }

    private static ShiftReportEntry Entry(Guid root, Guid envelope, Guid tablet, decimal start) =>
        new(root, 1, Field.Machine, Field.Operator, "hours", DateTimeOffset.UtcNow.AddHours(-8), start, DateTimeOffset.UtcNow, start + 8, "closed", [], envelope, null, false)
        {
            DeviceId = tablet,
        };

    private async Task<HttpResponseMessage> IssueAsync(Guid tenant, Guid site, string? shift)
    {
        var query = $"/api/v1/documents/shift-reports?siteId={site}&date=2026-11-26" + (shift is null ? string.Empty : $"&shift={shift}");
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<string> RowAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(host.Database.Admin);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"d-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
