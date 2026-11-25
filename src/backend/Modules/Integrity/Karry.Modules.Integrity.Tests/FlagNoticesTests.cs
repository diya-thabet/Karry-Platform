using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Integrity.Infrastructure;
using Karry.Modules.Notifications.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Integrity.Tests;

/// <summary>
/// Flags people must hear of (ADR-0039 §2, US-P2-NOT-01): when the Integrity outbox delivers a flag's opening, a missing
/// end goes to the site's managers and a fraud suspicion to executives and administrators, once each, in the dispatch's
/// transaction; a continuity gap waits for the console. A second dispatch publishes nothing again.
/// </summary>
[Trait("Story", "US-P2-NOT-01")]
public sealed class FlagNoticesTests(IntegrityHost host) : IClassFixture<IntegrityHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_end_notifies_its_site_once_and_a_continuity_gap_notifies_nobody()
    {
        var tenant = await ProvisionAsync();
        var (site, entry) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await SyncAsync(tenant, new FlagSubject("shift_entry", entry), IntegrityRules.MissingEnd, site, "open-segment");
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), IntegrityRules.Continuity, site, "a>b");

        await DispatchAsync();
        await DispatchAsync();

        var notice = Of(tenant).Should().ContainSingle("the continuity gap waits for the console; the missing end is told once").Subject;
        (notice.Type, notice.SiteId, notice.Link).Should().Be((NoticeTypes.MissingEnd, site, "/assets/integrity"));
        notice.Parameters["subjectType"].Should().Be("shift_entry");
        notice.Parameters["subjectId"].Should().Be(entry.ToString("D"));
        notice.Parameters["flagId"].Should().Be(await FlagIdAsync(tenant, IntegrityRules.MissingEnd));
        notice.Person.Should().BeNull("the audience is a role on the site, not one person");
    }

    [Fact]
    public async Task An_unexplained_gap_raises_a_fraud_suspicion_that_the_executives_hear_of()
    {
        var tenant = await ProvisionAsync();
        var (site, meter) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await SyncAsync(tenant, new FlagSubject("meter", meter), IntegrityRules.Continuity, site, "a>b");
        var gap = await FlagIdAsync(tenant, IntegrityRules.Continuity);

        using var proposed = await SettleAsync(tenant, Guid.Parse(gap), await VersionAsync(tenant, gap), new { reason = "unexplained", comment = "Nobody knows who ran the engine at night." });
        proposed.IsSuccessStatusCode.Should().BeTrue(await proposed.Content.ReadAsStringAsync(Ct));
        await DispatchAsync();

        var notice = Of(tenant).Should().ContainSingle().Subject;
        (notice.Type, notice.SiteId).Should().Be((NoticeTypes.FraudSuspected, site));
        notice.Parameters["subjectId"].Should().Be(meter.ToString("D"));
        notice.Parameters["flagId"].Should().Be(await FlagIdAsync(tenant, IntegrityRules.FraudSuspected));
    }

    private IEnumerable<Notice> Of(Guid tenant) =>
        host.Notices.Published.Where(n => Tenants.TryGetValue(n.SourceEventId, out var t) && t == tenant);

    // The notices carry the outbox message ID, not the tenant: map each message back to its tenant.
    private Dictionary<Guid, Guid> Tenants
    {
        get
        {
            using var connection = new NpgsqlConnection(host.Database.Admin);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT id, tenant_id FROM integrity.outbox_messages", connection);
            using var reader = command.ExecuteReader();
            var map = new Dictionary<Guid, Guid>();
            while (reader.Read())
            {
                map[reader.GetGuid(0)] = reader.GetGuid(1);
            }

            return map;
        }
    }

    private async Task DispatchAsync() =>
        await host.Services.GetRequiredService<OutboxDispatcher<IntegrityDbContext>>().DispatchPendingAsync(1000, Ct);

    private async Task SyncAsync(Guid tenant, FlagSubject subject, string rule, Guid site, string key)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var finding = new Finding(key, [new FlagSubject("shift_entry", Guid.CreateVersion7())], ["sha256:" + new string('a', 64)], new Dictionary<string, string>());
        await scope.ServiceProvider.GetRequiredService<IIntegrityFlags>().SyncAsync(connection, transaction, subject, rule, site, [finding], Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<string> FlagIdAsync(Guid tenant, string rule) =>
        (await ScalarAsync($"SELECT id::text FROM integrity.flags WHERE tenant_id = '{tenant}' AND rule = '{rule}'"))!;

    private async Task<string> VersionAsync(Guid tenant, string flag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/integrity/flags?status=all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        using var response = await host.Client.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .Single(f => f.GetProperty("id").GetGuid() == Guid.Parse(flag)).GetProperty("version").GetString()!;
    }

    private async Task<HttpResponseMessage> SettleAsync(Guid tenant, Guid flag, string version, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrity/flags/{flag}/settle") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create("admin-1",
            (TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (BuildingBlocks.Authorization.PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"n-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }
}
