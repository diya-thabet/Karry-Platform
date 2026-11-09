using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// The real host (every module's sync source) with two companies: North (the tablet's site) and South in company A,
/// and company B with its own tablet. Data is seeded as the modules store it; changes go through the API.
/// </summary>
public sealed class SyncHost : IAsyncLifetime
{
    public const string Pin = "804613";

    private PostgresServer? server;
    private Factory? factory;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public Company A { get; private set; } = null!;

    public Company B { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();
        factory = new Factory(Database.App, Clock);
        await MigrationRunner.MigrateAsync(factory.Services, Database.Owner, CancellationToken.None);
        await ExecuteAsync(TestCommandHandler.CreateTable);
        Client = factory.CreateClient();
        A = await CompanyAsync("sync-a");
        B = await CompanyAsync("sync-b");
    }

    /// <summary>A token of the company's Tenant Admin who just signed in (no step-up asked).</summary>
    public string Admin(Company company) => TestTokens.Create(company.AdminSubject,
        (TenantResolutionMiddleware.TenantClaim, company.Id.ToString()),
        (PermissionMiddleware.AuthTimeClaim, Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>
    /// Starts the company's trial as Billing does on provisioning (this host has no Keycloak, so the provisioning message
    /// fails at Identity's handler before Billing's); idempotent like the handler.
    /// </summary>
    public async Task StartTrialAsync(Company company)
    {
        await using var scope = factory!.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new SharedKernel.Tenancy.TenantId(company.Id));
        var trial = scope.ServiceProvider.GetServices<Modules.Tenancy.Contracts.ITenantProvisionedHandler>().OfType<Modules.Billing.Application.TrialOnProvisioning>().Single();
        await trial.HandleAsync(new Modules.Tenancy.Contracts.TenantSummary(new SharedKernel.Tenancy.TenantId(company.Id), company.Slug, company.Slug), CancellationToken.None);
    }

    /// <summary>A tablet of the company, enrolled on its North site.</summary>
    public async Task<TestTablet> TabletAsync(Company company)
    {
        var created = await SendAsync(HttpMethod.Post, $"{TestTablet.Devices}/enrollments", Admin(company), new { siteId = company.North });
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var tablet = new TestTablet(Client, Clock.GetUtcNow);
        (await tablet.EnrollAsync(company.Slug, code)).StatusCode.Should().Be(HttpStatusCode.Created);
        return tablet;
    }

    public async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Moves every changed_at of the company's reference rows back by <paramref name="age"/>.</summary>
    public async Task AgeAsync(Company company, TimeSpan age)
    {
        foreach (var table in new[] { "identity.users", "assets.assets", "assets.meters", "materials.materials" })
        {
            // Table names are constants of this method.
            await ExecuteAsync($"UPDATE {table} SET changed_at = changed_at - $2 WHERE tenant_id = $1", company.Id, age);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>The PBKDF2 hash a field user's PIN is stored as (few iterations: fast tests, same format).</summary>
    public static string PinHash(byte[] salt) =>
        $"pbkdf2-sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Pin, salt, 1000, HashAlgorithmName.SHA256, 32))}";

    /// <summary>A new seeded company (a test that changes data uses its own).</summary>
    public async Task<Company> CompanyAsync(string prefix)
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}"[..20];
        var provisioned = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        provisioned.EnsureSuccessStatusCode();
        var id = (await provisioned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var company = new Company(id, slug, $"admin-{slug}", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var salt = RandomNumberGenerator.GetBytes(16);
        await ExecuteAsync(
            """
            INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, email, subject, roles, created_at)
            VALUES (gen_random_uuid(), $1, 'Office', 'Active', 'Admin', 'Sync', $2, $3, '[{"role":"tenant-admin","siteId":null}]', now());
            """, id, $"{company.AdminSubject}@sync.test", company.AdminSubject);
        foreach (var (site, code) in new[] { (company.North, "NORD"), (company.South, "SUD") })
        {
            await ExecuteAsync(
                """
                INSERT INTO organization.sites (id, tenant_id, code, name, type, address_line, city, country, location, time_zone, status, created_at, changed_at)
                VALUES ($1, $2, $3, $4, 'Quarry', 'Route 1', 'Tunis', 'TN', ST_SetSRID(ST_MakePoint(10.0, 36.8), 4326), 'Africa/Tunis', 'Active', now(), now())
                """, site, id, code, $"Carrière {code}");
        }

        await ExecuteAsync(
            """
            INSERT INTO organization.shift_patterns (id, tenant_id, site_id, effective_from, shifts, created_at, changed_at)
            VALUES ($1, $2, $3, '2099-01-01', '[{"Code":"J","Name":"Jour","Start":"06:00","End":"18:00","Days":["mon","tue"]}]', now(), now())
            """, company.Pattern, id, company.North);
        foreach (var (asset, site, code) in new[] { (company.Excavator, company.North, "PEL-01"), (company.Loader, company.North, "CHG-01"), (company.Truck, company.South, "DMP-01") })
        {
            await ExecuteAsync(
                """
                WITH asset AS (
                  INSERT INTO assets.assets (id, tenant_id, code, name, type, make, model, site_id, status, label_token, created_at, changed_at)
                  VALUES ($1, $2, $3, $4, 'excavator', 'Caterpillar', '336', $5, 'Active', $6, now(), now())
                  RETURNING id, tenant_id, site_id)
                INSERT INTO assets.site_assignments (tenant_id, asset_id, site_id, "from") SELECT tenant_id, id, site_id, now() FROM asset
                """, asset, id, code, $"Machine {code}", site, $"TOKEN{asset:N}"[..26].ToUpperInvariant());
        }

        await ExecuteAsync(
            """
            INSERT INTO assets.meters (id, tenant_id, asset_id, kind, initial_reading, installed_on, status, "offset", last_value, last_read_at, created_at, changed_at)
            VALUES ($1, $2, $3, 'Hours', 10, '2026-01-01', 'Ok', 0, 10, '2026-01-01T00:00:00Z', now(), now())
            """, company.Meter, id, company.Excavator);
        await ExecuteAsync(
            """
            INSERT INTO materials.materials (id, tenant_id, code, name, kind, density_versions, status, created_at, changed_at)
            VALUES ($1, $2, 'G-0/31', 'Grave 0/31.5', 'Product', '[{"EffectiveFrom":"2026-01-01","Bank":"2.2","Loose":"1.6","Compacted":null,"Moisture":"0.03"}]', 'Active', now(), now())
            """, company.Material, id);
        foreach (var (user, employeeId, roles, pin) in new[]
        {
            (company.Operator, "OP-1", $$"""[{"role":"operator","siteId":"{{company.North}}"}]""", true),
            (company.Roaming, "OP-2", """[{"role":"operator","siteId":null}]""", false),
            (company.Elsewhere, "OP-3", $$"""[{"role":"operator","siteId":"{{company.South}}"}]""", true),
        })
        {
            await ExecuteAsync(
                """
                INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, employee_id, pin_hash, roles, created_at)
                VALUES ($1, $2, 'Field', 'Active', 'Opérateur', $3, $3, $4, $5::jsonb, now())
                """, user, id, employeeId, pin ? PinHash(salt) : DBNull.Value, roles);
        }

        return company;
    }

    private sealed class Factory(string connectionString, TimeProvider clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Karry", connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.TrustTestIssuer();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                TestCommandHandler.AddTo(services);
            });
        }
    }
}

/// <summary>A seeded company: its slug, admin, sites, machines (two on North, one on South), meter, material, field users.</summary>
public sealed record Company(
    Guid Id, string Slug, string AdminSubject, Guid North, Guid South, Guid Pattern, Guid Excavator, Guid Loader, Guid Truck, Guid Meter, Guid Material,
    Guid Operator, Guid Roaming, Guid Elsewhere);

/// <summary>
/// The field reference data a tablet downloads (US-P1-OFF-03, ADR-0028 §5): a snapshot of its site's data, then the
/// changes since its cursor, rows that left its scope as removes, and never anything of another company.
/// </summary>
[Trait("Story", "US-P1-OFF-03")]
public sealed class SyncChangesTests(SyncHost host) : IClassFixture<SyncHost>
{
    private const string Changes = "/api/v1/sync/changes";

    [Fact]
    [Trait("TestCase", "TC-P1-OFF-003")]
    public async Task A_snapshot_holds_the_tablet_s_site_its_machines_the_materials_and_its_field_users_with_pin_verifiers()
    {
        var a = host.A;
        using var tablet = await host.TabletAsync(a);

        var set = await PullAsync(tablet);

        set.GetProperty("full").GetBoolean().Should().BeTrue();
        var changes = set.GetProperty("changes");
        Ids(changes, "sites").Should().Equal(a.North);
        changes.GetProperty("sites").GetProperty("replace").GetBoolean().Should().BeTrue();
        var site = changes.GetProperty("sites").GetProperty("upserts")[0];
        site.GetProperty("code").GetString().Should().Be("NORD");
        site.GetProperty("type").GetString().Should().Be("quarry");
        site.GetProperty("timeZone").GetString().Should().Be("Africa/Tunis");
        var pattern = changes.GetProperty("shiftPatterns").GetProperty("upserts")[0];
        pattern.GetProperty("effectiveFrom").GetString().Should().Be("2099-01-01");
        pattern.GetProperty("shifts")[0].GetRawText().Should().Be("""{"code":"J","name":"Jour","start":"06:00","end":"18:00","days":["mon","tue"]}""");
        Ids(changes, "assets").Should().BeEquivalentTo([a.Excavator, a.Loader]);
        var excavator = changes.GetProperty("assets").GetProperty("upserts").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == a.Excavator);
        excavator.GetProperty("labelCode").GetString().Should().StartWith("KRY1:");
        excavator.GetProperty("status").GetString().Should().Be("active");
        changes.GetProperty("meters").GetProperty("upserts")[0].GetRawText().Should().Be(
            $$"""{"id":"{{a.Meter}}","assetId":"{{a.Excavator}}","kind":"hours","unit":"h","status":"ok"}""");
        Ids(changes, "materials").Should().Equal(a.Material);
        changes.GetProperty("materials").GetProperty("upserts")[0].GetProperty("kind").GetString().Should().Be("product");

        // Field users of North or company-wide; a PIN as a verifier keyed by this tablet, never the hash.
        Ids(changes, "users").Should().BeEquivalentTo([a.Operator, a.Roaming]);
        var users = changes.GetProperty("users").GetProperty("upserts").EnumerateArray().ToList();
        var pin = users.Single(u => u.GetProperty("id").GetGuid() == a.Operator).GetProperty("pin");
        users.Single(u => u.GetProperty("id").GetGuid() == a.Roaming).GetProperty("pin").ValueKind.Should().Be(JsonValueKind.Null);
        var salt = Convert.FromBase64String(pin.GetProperty("salt").GetString()!);
        var hash = Convert.FromBase64String(SyncHost.PinHash(salt).Split('$')[3]);
        pin.GetProperty("iterations").GetInt32().Should().Be(1000);
        pin.GetProperty("verifier").GetString().Should().Be(Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(tablet.PinKey), hash)));
        set.GetRawText().Should().NotContain("pbkdf2").And.NotContain(host.B.Excavator.ToString());
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    [Trait("TestCase", "TC-P1-IAM-004")]
    public async Task A_delta_brings_what_changed_since_the_cursor_and_removes_what_left_the_tablet_s_scope()
    {
        var a = await host.CompanyAsync("sync-c");
        await host.AgeAsync(a, TimeSpan.FromHours(1)); // so only what this test changes falls in the delta
        using var tablet = await host.TabletAsync(a);
        var cursor = (await PullAsync(tablet)).GetProperty("cursor").GetString()!;
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var admin = host.Admin(a);

        await Ok(host.SendAsync(HttpMethod.Post, $"/api/v1/assets/{a.Excavator}/move", admin, new { siteId = a.South }));
        await Ok(host.SendAsync(HttpMethod.Post, $"/api/v1/assets/{a.Truck}/move", admin, new { siteId = a.North }));
        await Ok(host.SendAsync(HttpMethod.Post, $"/api/v1/materials/{a.Material}/archive", admin));
        await Ok(host.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{a.Operator}/deactivate", admin));
        await Ok(host.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{a.Roaming}/pin", admin, new { pin = "502719" }));
        await Ok(host.SendAsync(HttpMethod.Delete, $"/api/v1/organization/sites/{a.North}/shift-patterns/2099-01-01", admin));
        // Another company's changes in the same minutes never reach this tablet, not even as removes.
        await Ok(host.SendAsync(HttpMethod.Post, $"/api/v1/assets/{host.B.Excavator}/move", host.Admin(host.B), new { siteId = host.B.South }));

        var set = await PullAsync(tablet, cursor);

        set.GetProperty("full").GetBoolean().Should().BeFalse();
        var changes = set.GetProperty("changes");
        Removes(changes, "assets").Should().Contain(a.Excavator).And.NotContain(host.B.Excavator);
        Ids(changes, "assets").Should().Equal(a.Truck);
        Ids(changes, "meters").Should().BeEmpty();
        Ids(changes, "materials").Should().BeEmpty();
        Removes(changes, "users").Should().Equal(a.Operator);
        Ids(changes, "users").Should().Equal(a.Roaming);
        Removes(changes, "meters").Should().Equal(a.Meter);
        Removes(changes, "materials").Should().Equal(a.Material);
        var roaming = changes.GetProperty("users").GetProperty("upserts").EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == a.Roaming);
        roaming.GetProperty("pin").GetProperty("verifier").GetString().Should().NotBeNullOrEmpty();
        changes.GetProperty("shiftPatterns").GetProperty("upserts").GetArrayLength().Should().Be(0);
        changes.GetProperty("shiftPatterns").GetProperty("replace").GetBoolean().Should().BeTrue();
        set.GetRawText().Should().NotContain(host.B.Excavator.ToString());
    }

    [Fact]
    public async Task A_delta_starts_five_minutes_before_the_cursor_so_a_late_commit_is_not_missed()
    {
        var a = host.A;
        using var tablet = await host.TabletAsync(a);
        var cursor = (await PullAsync(tablet)).GetProperty("cursor").GetString()!;
        var at = DateTimeOffset.Parse(cursor, CultureInfo.InvariantCulture);
        await host.ExecuteAsync("UPDATE assets.assets SET changed_at = $1 WHERE id = $2", at.AddMinutes(-4), a.Loader);
        await host.ExecuteAsync("UPDATE assets.assets SET name = 'Hors fenêtre', changed_at = $1 WHERE id = $2", at.AddMinutes(-6), a.Truck);

        var changes = (await PullAsync(tablet, cursor)).GetProperty("changes");

        Ids(changes, "assets").Should().Contain(a.Loader);
        Ids(changes, "assets").Concat(Removes(changes, "assets")).Should().NotContain(a.Truck);
    }

    [Fact]
    public async Task A_cursor_the_server_did_not_give_is_refused_and_an_old_one_asks_for_a_snapshot()
    {
        using var tablet = await host.TabletAsync(host.A);
        var now = host.Clock.GetUtcNow().UtcDateTime;

        await ExpectProblemAsync(await tablet.SendAsync(HttpMethod.Get, $"{Changes}?cursor=yesterday"), HttpStatusCode.BadRequest, "sync.cursor_invalid");
        await ExpectProblemAsync(await tablet.SendAsync(HttpMethod.Get, $"{Changes}?cursor={Cursor(now.AddHours(1))}"), HttpStatusCode.BadRequest, "sync.cursor_invalid");
        await ExpectProblemAsync(await tablet.SendAsync(HttpMethod.Get, $"{Changes}?cursor={Cursor(now.AddDays(-8))}"), HttpStatusCode.UnprocessableEntity, "sync.cursor_expired");
        (await tablet.SendAsync(HttpMethod.Get, $"{Changes}?cursor={Cursor(now.AddDays(-6))}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_an_active_enrolled_tablet_pulls()
    {
        using var tablet = await host.TabletAsync(host.A);
        (await host.SendAsync(HttpMethod.Get, Changes, host.Admin(host.A))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await Ok(host.SendAsync(HttpMethod.Post, $"{TestTablet.Devices}/{tablet.Id}/revoke", host.Admin(host.A)));

        await ExpectProblemAsync(await tablet.SendAsync(HttpMethod.Get, Changes), HttpStatusCode.Unauthorized, "devices.revoked");
    }

    [Fact]
    public async Task Each_company_s_tablet_gets_its_own_company_s_data()
    {
        using var tablet = await host.TabletAsync(host.B);

        var changes = (await PullAsync(tablet)).GetProperty("changes");

        Ids(changes, "sites").Should().Equal(host.B.North);
        Ids(changes, "materials").Should().Equal(host.B.Material);
        Ids(changes, "assets").Should().NotContain([host.A.Excavator, host.A.Loader, host.A.Truck]);
    }

    private static string Cursor(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> PullAsync(TestTablet tablet, string? cursor = null)
    {
        var response = await tablet.SendAsync(HttpMethod.Get, cursor is null ? Changes : $"{Changes}?cursor={Uri.EscapeDataString(cursor)}");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static IEnumerable<Guid> Ids(JsonElement changes, string collection) =>
        changes.GetProperty(collection).GetProperty("upserts").EnumerateArray().Select(r => r.GetProperty("id").GetGuid());

    private static IEnumerable<Guid> Removes(JsonElement changes, string collection) =>
        changes.GetProperty(collection).GetProperty("removes").EnumerateArray().Select(r => r.GetGuid());

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        var response = await call;
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }
}
