using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Karry.Modules.Documents.Api;
using Karry.Modules.Documents.Application;
using Karry.Modules.FieldOps.Contracts;
using Karry.Modules.Organization.Contracts;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// Public verification (ADR-0040 §4, US-P2-SIG-06): the envelope ID and the token from the QR give the document, without
/// signing in; every miss is the same "not found" (TC-P2-SIG-005); a PDF's hash tells a changed byte (TC-P2-DOC-002).
/// </summary>
[Trait("Story", "US-P2-SIG-06")]
public sealed class VerifyingTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anyone_with_the_qr_sees_whose_document_it_is_its_hashes_and_then_its_signature_but_no_name()
    {
        var (tenant, site) = (await ProvisionAsync(), Site());
        host.Field.Entries[site] = [Entry()];
        var (id, pdf, token) = await IssueAsync(tenant, site);

        using var answer = await VerifyAsync(id.ToString(), new { token });

        answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync(Ct));
        answer.Headers.CacheControl!.NoStore.Should().BeTrue();
        var document = (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct))!;
        document.GetProperty("envelopeId").GetGuid().Should().Be(id);
        (document.GetProperty("type").GetString(), document.GetProperty("siteCode").GetString(), document.GetProperty("shiftCode").GetString())
            .Should().Be(("shift_report", "CAR-KAK", "M"));
        document.GetProperty("company").GetString().Should().StartWith("Carrière d-");
        document.GetProperty("pdfHash").GetString().Should().Be("sha256:" + Convert.ToHexStringLower(SHA256.HashData(pdf)));
        document.GetProperty("signature").ValueKind.Should().Be(JsonValueKind.Null, "the Workers have not signed it yet");
        document.GetProperty("pdfMatches").ValueKind.Should().Be(JsonValueKind.Null, "no PDF was sent");
        var text = document.GetRawText();
        text.Should().NotContain("Awa Camara").And.NotContain("Mamadou Diallo").And.NotContain(tenant.ToString(), "no name and no company ID");

        host.Signer.Up = true;
        await host.Services.GetRequiredService<SignDocuments>().RunAsync(Ct);
        using var signed = await VerifyAsync(id.ToString(), new { token });
        var signature = (await signed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("signature");
        signature.GetProperty("keyId").GetString().Should().Be("karry-server-v1");
        signature.GetProperty("value").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-DOC-002")]
    public async Task The_issued_pdf_matches_and_a_pdf_with_one_changed_byte_does_not()
    {
        var (tenant, site) = (await ProvisionAsync(), Site());
        host.Field.Entries[site] = [Entry()];
        var (id, pdf, token) = await IssueAsync(tenant, site);
        var changed = (byte[])pdf.Clone();
        changed[changed.Length / 2] ^= 0x01;

        (await PdfMatchesAsync(id, token, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(pdf)))).Should().BeTrue();
        (await PdfMatchesAsync(id, token, Convert.ToHexString(SHA256.HashData(pdf)))).Should().BeTrue("a bare digest, any case, is the same hash");
        (await PdfMatchesAsync(id, token, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(changed)))).Should().BeFalse("one changed byte is another document");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-005")]
    public async Task Every_miss_gets_the_same_not_found_and_another_companys_caller_learns_nothing_more()
    {
        var (tenant, site) = (await ProvisionAsync(), Site());
        host.Field.Entries[site] = [Entry()];
        var (id, _, token) = await IssueAsync(tenant, site);
        var stranger = await ProvisionAsync();

        var misses = new (string Id, object? Body, string? Bearer)[]
        {
            (Guid.CreateVersion7().ToString(), new { token }, null),                          // unknown envelope, a real-looking token
            (id.ToString(), new { token = token[..^1] + (token[^1] == 'A' ? 'B' : 'A') }, null), // one character of the token changed
            (id.ToString(), new { token = string.Empty }, null),
            (id.ToString(), new { }, null),
            (id.ToString(), new { token = new string('x', 5000) }, null),
            ("not-an-id", new { token }, null),
            (id.ToString(), new { token = token[..^1] + (token[^1] == 'A' ? 'B' : 'A') }, TestTokens.ForTenant(stranger, "admin-1")),
        };

        var answers = new List<string>();
        foreach (var (missId, body, bearer) in misses)
        {
            using var answer = await VerifyAsync(missId, body, bearer);
            answer.StatusCode.Should().Be(HttpStatusCode.NotFound, missId);
            var problem = await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
            answers.Add($"{problem.GetProperty("code").GetString()}|{problem.GetProperty("title").GetString()}|{problem.GetProperty("type").GetString()}");
        }

        answers.Distinct().Should().ContainSingle().Which.Should().StartWith("documents.not_found|", "nothing tells which part was wrong");
    }

    [Fact]
    public async Task Each_issue_has_its_own_token()
    {
        var (tenant, site) = (await ProvisionAsync(), Site());
        host.Field.Entries[site] = [Entry()];
        var (first, _, firstToken) = await IssueAsync(tenant, site);
        var (second, _, secondToken) = await IssueAsync(tenant, site);

        firstToken.Should().NotBe(secondToken);
        using var crossed = await VerifyAsync(second.ToString(), new { token = firstToken });
        crossed.StatusCode.Should().Be(HttpStatusCode.NotFound, "a token opens its own document only");
        using var own = await VerifyAsync(first.ToString(), new { token = firstToken });
        own.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<bool> PdfMatchesAsync(Guid id, string token, string pdfHash)
    {
        using var answer = await VerifyAsync(id.ToString(), new { token, pdfHash });
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await answer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("pdfMatches").GetBoolean();
    }

    private async Task<HttpResponseMessage> VerifyAsync(string id, object? body, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/documents/verify/{id}") { Content = JsonContent.Create(body) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<(Guid Id, byte[] Pdf, string Token)> IssueAsync(Guid tenant, Guid site)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/documents/shift-reports?siteId={site}&date=2026-11-26&shift=M");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var id = Guid.Parse(response.Headers.GetValues(DocumentsModule.EnvelopeHeader).Single());
        return (id, await response.Content.ReadAsByteArrayAsync(Ct), host.Renderer.Addresses[id].Fragment.TrimStart('#'));
    }

    private Guid Site()
    {
        var site = new SiteSummary(Guid.CreateVersion7(), "CAR-KAK", "Carrière de Kakoulima", "Africa/Conakry", false);
        host.Field.Sites[site.Id] = site;
        return site.Id;
    }

    private static ShiftReportEntry Entry() =>
        new(Guid.CreateVersion7(), 1, Field.Machine, Field.Operator, "hours", DateTimeOffset.UtcNow.AddHours(-8), 1250m, DateTimeOffset.UtcNow, 1258m, "closed", [],
            Guid.CreateVersion7(), null, false)
        {
            DeviceId = Guid.CreateVersion7(),
        };

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
