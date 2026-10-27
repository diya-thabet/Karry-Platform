using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Imports.Application;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Imports.Tests;

/// <summary>
/// Import jobs over HTTP with the worker's runner (US-P1-ORG-03 AC2–AC5, ADR-0027 §4–§10): upload, report with cells,
/// preview, all-or-nothing commit that can be repeated, discard, the requester's current rights, scanning and retention.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class ImportJobsApiTests(ImportsHost host) : IClassFixture<ImportsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upload_is_validated_by_the_worker_previewed_and_committed_and_the_same_file_again_creates_nothing()
    {
        var tenant = await ProvisionAsync();
        host.Probes[tenant] = new(StringComparer.Ordinal) { ["P-02"] = "Ancien nom", ["P-03"] = "Sonde 3" };
        var file = Workbook(("Code", "Name", "Colour"), ("P-01", "Sonde 1", "red"), ("P-02", "Sonde 2", null), ("P-03", "Sonde 3", null));

        var uploaded = await UploadAsync(tenant, "probes", file, "C:\\fakepath\\sondes.xlsx");
        uploaded.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await Json(uploaded);
        job.GetProperty("status").GetString().Should().Be("validating");
        job.GetProperty("fileName").GetString().Should().Be("sondes.xlsx");
        job.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(file)));
        var id = job.GetProperty("id").GetGuid();
        host.Files.Objects.Should().ContainKey($"tenants/{tenant:D}/imports/{id:D}/original.xlsx");

        await host.RunJobsAsync();
        var validated = await Json(await GetAsync($"/api/v1/imports/{id}", tenant));
        (validated.GetProperty("status").GetString(), validated.GetProperty("rows").GetInt32(), validated.GetProperty("errors").GetInt32(),
            validated.GetProperty("warnings").GetInt32(), validated.GetProperty("scanner").GetString()).Should().Be(("validated", 3, 0, 1, "test-scanner"));
        var issues = await Json(await GetAsync($"/api/v1/imports/{id}/issues", tenant));
        issues.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("row").GetInt32(), i.GetProperty("cell").GetString(), i.GetProperty("code").GetString(), i.GetProperty("severity").GetString()))
            .Should().Equal((1, "C1", "imports.column_unknown", "warning"));
        (await Rows(id, tenant)).Should().Equal((2, "P-01", "create"), (3, "P-02", "update"), (4, "P-03", "unchanged"));
        host.Probes[tenant].Should().HaveCount(2, "validation never writes business data");

        var commit = await Json(await PostAsync($"/api/v1/imports/{id}/commit", tenant));
        commit.GetProperty("status").GetString().Should().Be("committing");
        await host.RunJobsAsync();
        var committed = await Json(await GetAsync($"/api/v1/imports/{id}", tenant));
        (committed.GetProperty("status").GetString(), committed.GetProperty("created").GetInt32(), committed.GetProperty("updated").GetInt32(),
            committed.GetProperty("unchanged").GetInt32()).Should().Be(("committed", 1, 1, 1));
        host.Probes[tenant].Should().BeEquivalentTo(new Dictionary<string, string> { ["P-01"] = "Sonde 1", ["P-02"] = "Sonde 2", ["P-03"] = "Sonde 3" });

        // TC-P1-ORG-002: the same file again changes nothing.
        var again = (await Json(await UploadAsync(tenant, "probes", file))).GetProperty("id").GetGuid();
        await host.RunJobsAsync();
        (await Rows(again, tenant)).Select(r => r.Action).Should().AllBe("unchanged");
        await PostAsync($"/api/v1/imports/{again}/commit", tenant);
        await host.RunJobsAsync();
        var repeated = await Json(await GetAsync($"/api/v1/imports/{again}", tenant));
        (repeated.GetProperty("created").GetInt32(), repeated.GetProperty("updated").GetInt32(), repeated.GetProperty("unchanged").GetInt32()).Should().Be((0, 0, 3));

        var list = await Json(await GetAsync("/api/v1/imports", tenant));
        list.GetProperty("items").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).Should().Equal(again, id);
    }

    [Fact]
    public async Task A_file_with_one_error_is_never_committed_and_can_be_discarded()
    {
        var tenant = await ProvisionAsync();
        var file = Workbook(("Code", "Name", null), ("P-01", "Sonde 1", null), ("P-02", null, null), ("P-01", "Doublon", null));
        var id = (await Json(await UploadAsync(tenant, "probes", file))).GetProperty("id").GetGuid();
        await host.RunJobsAsync();

        var issues = await Json(await GetAsync($"/api/v1/imports/{id}/issues?severity=error", tenant));
        issues.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("cell").GetString(), i.GetProperty("code").GetString()))
            .Should().Equal(("A2", "probes.code_duplicate"), ("B3", "imports.value_required"), ("A4", "probes.code_duplicate"));
        issues.GetProperty("items")[0].GetProperty("args").GetProperty("code").GetString().Should().Be("P-01");
        (await Rows(id, tenant)).Should().BeEmpty("no row is clean");

        (await Problem(await PostAsync($"/api/v1/imports/{id}/commit", tenant))).Should().Be((HttpStatusCode.UnprocessableEntity, "imports.job_has_errors"));
        (host.Probes.TryGetValue(tenant, out var probes) ? probes : []).Should().BeEmpty();

        var discarded = await Json(await PostAsync($"/api/v1/imports/{id}/discard", tenant));
        discarded.GetProperty("status").GetString().Should().Be("discarded");
        (await Problem(await PostAsync($"/api/v1/imports/{id}/discard", tenant))).Should().Be((HttpStatusCode.Conflict, "imports.job_not_discardable"));
        (await Problem(await PostAsync($"/api/v1/imports/{id}/commit", tenant))).Should().Be((HttpStatusCode.Conflict, "imports.job_not_validated"));
    }

    [Fact]
    public async Task A_commit_validates_again_and_an_error_found_then_rolls_the_whole_file_back()
    {
        var tenant = await ProvisionAsync();
        var file = Workbook(("Code", "Name", null), ("P-10", "Sonde 10", null), ("P-11", "Sonde 11", null));
        var id = (await Json(await UploadAsync(tenant, "probes", file))).GetProperty("id").GetGuid();
        await host.RunJobsAsync();
        await PostAsync($"/api/v1/imports/{id}/commit", tenant);

        host.Refused["P-11"] = true; // the data changed since the preview
        try
        {
            await host.RunJobsAsync();
        }
        finally
        {
            host.Refused.TryRemove("P-11", out _);
        }

        var job = await Json(await GetAsync($"/api/v1/imports/{id}", tenant));
        (job.GetProperty("status").GetString(), job.GetProperty("errors").GetInt32(), job.GetProperty("created").GetInt32()).Should().Be(("validated", 1, 0));
        (host.Probes.TryGetValue(tenant, out var probes) ? probes : []).Should().BeEmpty("nothing of the file was written");
    }

    [Fact]
    public async Task Uploads_are_refused_for_unknown_or_forbidden_kinds_and_files_that_are_not_plain_xlsx()
    {
        var tenant = await ProvisionAsync();
        var file = Workbook(("Code", "Name", null), ("P-01", "Sonde", null));

        (await Problem(await UploadAsync(tenant, "rockets", file))).Should().Be((HttpStatusCode.NotFound, "imports.kind_unknown"));
        (await Problem(await UploadAsync(tenant, "locked", file))).Should().Be((HttpStatusCode.Forbidden, "imports.kind_forbidden"));
        (await Problem(await UploadAsync(tenant, "probes", "code;name\nP-01;Sonde"u8.ToArray()))).Should().Be((HttpStatusCode.BadRequest, "imports.file_type_unsupported"));
        (await Problem(await UploadAsync(tenant, "probes", []))).Should().Be((HttpStatusCode.BadRequest, "imports.file_missing"));
        (await Problem(await UploadAsync(tenant, "probes", new byte[ImportErrors.MaxBytes + 1]))).Should().Be((HttpStatusCode.BadRequest, "imports.file_too_large"));
        (await Problem(await UploadAsync(tenant, "probes", file, "\u0007.xlsx"))).Should().Be((HttpStatusCode.BadRequest, "imports.file_name_invalid"));
        (await UploadAsync(tenant, "probes", file, subject: ImportsHost.Operator)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Files.Objects.Keys.Should().NotContain(k => k.StartsWith($"tenants/{tenant:D}/", StringComparison.Ordinal), "a refused file is never kept");
    }

    [Fact]
    public async Task The_job_runs_with_the_requesters_rights_of_now_and_a_file_the_scanner_refuses_is_never_read()
    {
        var tenant = await ProvisionAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        var demoted = (await Json(await UploadAsync(tenant, "probes", Workbook(("Code", "Name", null), ("P-01", "Sonde", null)), subject: subject))).GetProperty("id").GetGuid();
        host.Roles[subject] = DefaultRoles.Operator; // rights removed after the upload

        var infectedFile = Workbook(("Code", "Name", null), ("P-66", "Virus", null));
        host.Infected[Convert.ToHexStringLower(SHA256.HashData(infectedFile))] = true;
        var infected = (await Json(await UploadAsync(tenant, "probes", infectedFile))).GetProperty("id").GetGuid();
        await host.RunJobsAsync();

        (await FileIssues(demoted, tenant)).Should().Equal("imports.kind_forbidden");
        (await FileIssues(infected, tenant)).Should().Equal("imports.file_infected");
        (await Rows(infected, tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Original_files_are_deleted_after_90_days_and_the_job_says_so()
    {
        var tenant = await ProvisionAsync();
        var id = (await Json(await UploadAsync(tenant, "probes", Workbook(("Code", "Name", null), ("P-01", "Sonde", null))))).GetProperty("id").GetGuid();
        var key = $"tenants/{tenant:D}/imports/{id:D}/original.xlsx";

        await Retention(TimeSpan.FromDays(89)).RunAsync(Ct);
        host.Files.Objects.Should().ContainKey(key);
        var run = await Retention(TimeSpan.FromDays(91)).RunAsync(Ct);

        run.Failed.Should().Be(0);
        host.Files.Objects.Should().NotContainKey(key);
        (await Json(await GetAsync($"/api/v1/imports/{id}", tenant))).GetProperty("fileKept").GetBoolean().Should().BeFalse();

        // The job was still waiting: its validation now reports the missing file.
        await host.RunJobsAsync();
        (await FileIssues(id, tenant)).Should().Equal("imports.file_deleted");
    }

    [Fact]
    public async Task Reports_are_paged_and_another_company_never_sees_an_import()
    {
        var tenant = await ProvisionAsync();
        var other = await ProvisionAsync();
        var file = Workbook(("Code", "Name", null), ("P-01", null, null), ("P-02", null, null), ("P-03", "Sonde", null));
        var id = (await Json(await UploadAsync(tenant, "probes", file))).GetProperty("id").GetGuid();
        await host.RunJobsAsync();

        var first = await Json(await GetAsync($"/api/v1/imports/{id}/issues?limit=1", tenant));
        first.GetProperty("items").GetArrayLength().Should().Be(1);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await Json(await GetAsync($"/api/v1/imports/{id}/issues?limit=1&cursor={cursor}", tenant));
        second.GetProperty("items")[0].GetProperty("cell").GetString().Should().Be("B3");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        (await Problem(await GetAsync($"/api/v1/imports/{id}/issues?limit=0", tenant))).Should().Be((HttpStatusCode.BadRequest, "imports.limit_invalid"));
        (await Problem(await GetAsync($"/api/v1/imports/{id}/rows?cursor=x", tenant))).Should().Be((HttpStatusCode.BadRequest, "imports.cursor_invalid"));
        (await Problem(await GetAsync($"/api/v1/imports/{id}/issues?severity=fatal", tenant))).Should().Be((HttpStatusCode.BadRequest, "imports.severity_invalid"));

        (await Problem(await GetAsync($"/api/v1/imports/{id}", other))).Should().Be((HttpStatusCode.NotFound, "imports.job_not_found"));
        (await Problem(await GetAsync($"/api/v1/imports/{id}/issues", other))).Should().Be((HttpStatusCode.NotFound, "imports.job_not_found"));
        (await Problem(await PostAsync($"/api/v1/imports/{id}/discard", other))).Should().Be((HttpStatusCode.NotFound, "imports.job_not_found"));
        (await Json(await GetAsync("/api/v1/imports", other))).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    private DeleteExpiredImportFiles Retention(TimeSpan later) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(DateTimeOffset.UtcNow + later), NullLogger<DeleteExpiredImportFiles>.Instance);

    private async Task<IReadOnlyList<string?>> FileIssues(Guid id, Guid tenant)
    {
        var job = await Json(await GetAsync($"/api/v1/imports/{id}", tenant));
        job.GetProperty("status").GetString().Should().Be("validated");
        var issues = await Json(await GetAsync($"/api/v1/imports/{id}/issues", tenant));
        return [.. issues.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("row").GetInt32() == 0).Select(i => i.GetProperty("code").GetString())];
    }

    private async Task<IReadOnlyList<(int Row, string? Key, string? Action)>> Rows(Guid id, Guid tenant)
    {
        var rows = await Json(await GetAsync($"/api/v1/imports/{id}/rows", tenant));
        return [.. rows.GetProperty("items").EnumerateArray().Select(r => (r.GetProperty("row").GetInt32(), r.GetProperty("key").GetString(), r.GetProperty("action").GetString()))];
    }

    private static byte[] Workbook((string, string, string?) headers, params (string?, string?, string?)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        string?[][] lines = [[headers.Item1, headers.Item2, headers.Item3], .. rows.Select(r => new[] { r.Item1, r.Item2, r.Item3 })];
        for (var r = 0; r < lines.Length; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                if (lines[r][c] is { } value)
                {
                    sheet.Cell(r + 1, c + 1).Value = value;
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> UploadAsync(Guid tenant, string kind, byte[] file, string fileName = "sondes.xlsx", string subject = "admin-1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/imports?kind={kind}&fileName={Uri.EscapeDataString(fileName)}")
        {
            Content = new ByteArrayContent(file),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, subject));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, Guid tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using (response)
        {
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
    }

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response)
    {
        using (response)
        {
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        }
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"i-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
