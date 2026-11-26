using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.Modules.Documents.Api;
using Karry.Modules.Documents.Application;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// The server's signature on issued documents (ADR-0040 §3): a document is valid from its ledger entry on; the Workers
/// sign its envelope when the signing service answers, over the envelope's ID and both hashes, once, ledgered.
/// </summary>
[Trait("Story", "US-P2-SIG-05")]
public sealed class SigningTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_envelope_waits_while_the_service_is_down_then_is_signed_once_over_its_id_and_hashes_and_ledgered()
    {
        var tenant = await ProvisionAsync();
        var site = new SiteSummary(Guid.CreateVersion7(), "CAR-KAK", "Carrière de Kakoulima", "Africa/Conakry", false);
        host.Field.Sites[site.Id] = site;
        host.Field.Entries[site.Id] = [new ShiftReportEntry(Guid.CreateVersion7(), 1, Field.Machine, Field.Operator, "hours", DateTimeOffset.UtcNow.AddHours(-8), 10m,
            DateTimeOffset.UtcNow, 18m, "closed", [], Guid.CreateVersion7(), null, false)];
        using var issued = await IssueAsync(tenant, site.Id);
        var id = Guid.Parse(issued.Headers.GetValues(DocumentsModule.EnvelopeHeader).Single());
        var signing = host.Services.GetRequiredService<SignDocuments>();

        host.Signer.Up = false;
        (await signing.RunAsync(Ct)).Unavailable.Should().BeTrue();
        (await RowAsync($"SELECT coalesce(signature, '-') FROM documents.envelopes WHERE id = '{id}'")).Should().Be("-", "a document is valid before its signature");

        host.Signer.Up = true;
        (await signing.RunAsync(Ct)).Unavailable.Should().BeFalse();
        await signing.RunAsync(Ct);

        var row = (await RowAsync($"SELECT signature || '|' || signature_key_id || '|' || content_hash || '|' || pdf_hash FROM documents.envelopes WHERE id = '{id}'")).Split('|');
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{id:D}|{row[2]}|{row[3]}"));
        (row[0], row[1]).Should().Be((Convert.ToBase64String(digest), "karry-server-v1"), "the server signs the envelope's ID with both hashes");
        var ledger = JsonDocument.Parse(await RowAsync(
            $"SELECT payload FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'documents.envelope_signed.v1'")).RootElement;
        (ledger.GetProperty("envelopeId").GetGuid(), ledger.GetProperty("keyId").GetString()).Should().Be((id, "karry-server-v1"));
        (await RowAsync($"SELECT count(*)::text FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'documents.envelope_signed.v1'"))
            .Should().Be("1", "an envelope is signed once, however often the job runs");
    }

    private async Task<HttpResponseMessage> IssueAsync(Guid tenant, Guid site)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/shift-reports?siteId={site}&date=2026-11-26&shift=M");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        var response = await host.Client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return response;
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
        var slug = $"s-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
