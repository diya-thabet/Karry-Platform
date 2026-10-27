using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Persistence;
using Karry.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;

namespace Karry.TenantIsolationTests;

/// <summary>
/// The real host (all modules) on a database prepared like an environment, with two tenants: A holds the data under
/// attack, B is the attacker.
/// </summary>
public sealed class IsolationHost : IAsyncLifetime
{
    private PostgresServer? server;
    private Factory? factory;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public SeededTenant A { get; private set; } = null!;

    public SeededTenant B { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();
        factory = new Factory(Database.App);
        await MigrationRunner.MigrateAsync(factory.Services, Database.Owner, CancellationToken.None);
        Client = factory.CreateClient();

        A = await ProvisionAsync("iso-carriere-a", "Isolation Carrière Alpha");
        B = await ProvisionAsync("iso-carriere-b", "Isolation Carrière Bravo");

        // Module data of tenant A is seeded here as modules add tenant-owned resources (P1.6+).
        // Identity (P1.8): a user of A to steal, and B's attacker is an active Tenant Admin of B, so user-management
        // endpoints really run (and must stay inside B) instead of stopping at "admin required".
        AUser = await SeedUserAsync(A.Id, "Alphauser", "alpha.victim@iso-a.test", subject: null);
        await SeedUserAsync(B.Id, "Bravoadmin", "bravo.admin@iso-b.test", AttackerSubject);
        // Identity (P1.9): a custom role of A to read, change or delete.
        ARole = await SeedRoleAsync(A.Id, "Alpharole");
        // Organization (P1.11): a site of A to read, change, archive or restore.
        ASite = await SeedSiteAsync(A.Id, "Alphasite");
        await SeedShiftsAsync(A.Id, ASite);
        // Materials (P1.12): a material of A with a future density version (on AShiftDate) to change or remove.
        AMaterial = await SeedMaterialAsync(A.Id, "Alphamaterial");
        // Assets (P1.13): a machine of A on its site, to read, change, move or dispose.
        AAsset = await SeedAssetAsync(A.Id, ASite, "Alphamachine");
        AMeter = await SeedMeterAsync(A.Id, AAsset);
        // Imports (P1.14): a validated import of A without errors (to read, commit or discard), with an issue and a preview row.
        AImport = await SeedImportAsync(A.Id);
    }

    /// <summary>ID of tenant A's import (its file, <c>Alphaimport.xlsx</c>, and its row key, <c>ALPHA-IMPORT</c>, are markers too).</summary>
    public Guid AImport { get; private set; }

    /// <summary>ID of tenant A's hour meter on its asset, with one reading (value 4242.5, a marker too).</summary>
    public Guid AMeter { get; private set; }

    /// <summary>ID of tenant A's asset (its name, <c>Alphamachine</c>, code, <c>ALPHA-PEL</c>, and plate, <c>ALPHA 0001</c>, are markers too).</summary>
    public Guid AAsset { get; private set; }

    /// <summary>The token on tenant A's asset label (<c>KRY1:</c> + token, scanned in B, must be unknown: TC-P1-AST-001).</summary>
    public const string ALabelToken = "ALPHALABELTQKENAAAAAAAAAAA";

    /// <summary>ID of tenant A's material (its name, <c>Alphamaterial</c>, and code, <c>ALPHA-MAT</c>, are markers too).</summary>
    public Guid AMaterial { get; private set; }

    /// <summary>Date of tenant A's future pattern version and non-working day (their names, <c>Alphashift</c> and <c>Alphaday</c>, are markers).</summary>
    public const string AShiftDate = "2099-01-01";

    /// <summary>ID of tenant A's site (its name, <c>Alphasite</c>, is a marker too).</summary>
    public Guid ASite { get; private set; }

    /// <summary>Code of tenant A's custom role (its name, <c>Alpharole</c>, is a marker too).</summary>
    public string ARole { get; private set; } = null!;

    public const string AttackerSubject = "attacker-b";

    public SeededUser AUser { get; private set; } = null!;

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

    private async Task<SeededTenant> ProvisionAsync(string slug, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new SeededTenant(id, slug, name);
    }

    private async Task<SeededUser> SeedUserAsync(Guid tenant, string lastName, string email, string? subject)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, email, subject, roles, created_at)
            VALUES ($1, $2, 'Office', 'Active', 'Isolation', $3, $4, $5, '[{"role":"tenant-admin","siteId":null}]', now())
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(lastName);
        insert.Parameters.AddWithValue(email);
        insert.Parameters.AddWithValue((object?)subject ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
        return new SeededUser(id, lastName, email);
    }

    private async Task<Guid> SeedSiteAsync(Guid tenant, string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO organization.sites (id, tenant_id, code, name, type, address_line, city, country, location, time_zone, status, created_at, changed_at)
            VALUES ($1, $2, 'ALPHA-SITE', $3, 'Quarry', 'Route 1', 'Tunis', 'TN', ST_SetSRID(ST_MakePoint(10.0, 36.8), 4326), 'Africa/Tunis', 'Active', now(), now())
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(name);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> SeedAssetAsync(Guid tenant, Guid site, string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            $$"""
            WITH asset AS (
              INSERT INTO assets.assets (id, tenant_id, code, name, type, make, model, plate, site_id, status, label_token, created_at, changed_at)
              VALUES ($1, $2, 'ALPHA-PEL', $3, 'excavator', 'Caterpillar', '336', 'ALPHA 0001', $4, 'Active', '{{ALabelToken}}', now(), now())
              RETURNING id, tenant_id, site_id)
            INSERT INTO assets.site_assignments (tenant_id, asset_id, site_id, "from") SELECT tenant_id, id, site_id, now() FROM asset
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(name);
        insert.Parameters.AddWithValue(site);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> SeedImportAsync(Guid tenant)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            WITH job AS (
              INSERT INTO imports.jobs (id, tenant_id, kind, file_name, size, sha256, requested_by, status, rows, errors, warnings, created, updated, unchanged, uploaded_at, changed_at)
              VALUES ($1, $2, 'assets', 'Alphaimport.xlsx', 4096, repeat('a', 64), 'admin-a', 'Validated', 1, 0, 1, 0, 0, 0, now(), now())
              RETURNING id, tenant_id),
            issue AS (
              INSERT INTO imports.issues (tenant_id, job_id, sequence, row, "column", cell, code, severity) SELECT tenant_id, id, 1, 1, 'colour', 'C1', 'imports.column_unknown', 'warning' FROM job)
            INSERT INTO imports.rows (tenant_id, job_id, row, key, action) SELECT tenant_id, id, 2, 'ALPHA-IMPORT', 'create' FROM job
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> SeedMeterAsync(Guid tenant, Guid asset)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            WITH meter AS (
              INSERT INTO assets.meters (id, tenant_id, asset_id, kind, initial_reading, installed_on, status, "offset", last_value, last_read_at, created_at, changed_at)
              VALUES ($1, $2, $3, 'Hours', 4242.5, '2026-01-01', 'Ok', 0, 4242.5, '2026-01-01T00:00:00Z', now(), now())
              RETURNING id, tenant_id)
            INSERT INTO assets.meter_readings (id, tenant_id, meter_id, value, read_at, recorded_at)
            SELECT gen_random_uuid(), tenant_id, id, 4242.5, '2026-01-01T00:00:00Z', now() FROM meter
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(asset);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> SeedMaterialAsync(Guid tenant, string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            $$"""
            INSERT INTO materials.materials (id, tenant_id, code, name, kind, density_versions, status, created_at, changed_at)
            VALUES ($1, $2, 'ALPHA-MAT', $3, 'Product',
                    '[{"EffectiveFrom":"2026-01-01","Bank":null,"Loose":"1.5","Compacted":null,"Moisture":"0.02"},{"EffectiveFrom":"{{AShiftDate}}","Bank":null,"Loose":"1.6","Compacted":null,"Moisture":"0.02"}]',
                    'Active', now(), now())
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(name);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SeedShiftsAsync(Guid tenant, Guid site)
    {
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand(
                    $$"""
                    INSERT INTO organization.shift_patterns (id, tenant_id, site_id, effective_from, shifts, created_at, changed_at)
                    VALUES ($1, $2, $3, '{{AShiftDate}}', '[{"Code":"ALPHA","Name":"Alphashift","Start":"06:00","End":"18:00","Days":["mon"]}]', now(), now())
                    """)
                {
                    Parameters = { new() { Value = Guid.CreateVersion7() }, new() { Value = tenant }, new() { Value = site } },
                },
                new NpgsqlBatchCommand(
                    $"""
                    INSERT INTO organization.non_working_days (id, tenant_id, site_id, date, kind, name, changed_at)
                    VALUES ($1, $2, $3, '{AShiftDate}', 'Closure', 'Alphaday', now())
                    """)
                {
                    Parameters = { new() { Value = Guid.CreateVersion7() }, new() { Value = tenant }, new() { Value = site } },
                },
            },
        };
        await batch.ExecuteNonQueryAsync();
    }

    private async Task<string> SeedRoleAsync(Guid tenant, string name)
    {
        var id = Guid.CreateVersion7();
        var code = $"custom-{id:N}";
        await using var connection = new NpgsqlConnection(Database.Admin);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO identity.roles (id, tenant_id, code, name, office, permissions, field_levels, created_at, updated_at)
            VALUES ($1, $2, $3, $4, true, '["identity.users.read"]', '{}', now(), now())
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(code);
        insert.Parameters.AddWithValue(name);
        await insert.ExecuteNonQueryAsync();
        return code;
    }

    private sealed class Factory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Karry", connectionString);
            builder.ConfigureTestServices(services => services.TrustTestIssuer());
        }
    }
}

public sealed record SeededUser(Guid Id, string LastName, string Email);

public sealed record SeededTenant(Guid Id, string Slug, string Name)
{
    /// <summary>Strings that must never appear in a response to another tenant.</summary>
    public IReadOnlyList<string> Markers => [Id.ToString(), Slug, Name];
}

/// <summary>
/// Automated tenant-isolation suite (US-P1-TEN-03, TC-P1-TEN-002): every operation of the API contract
/// (<c>src/backend/Karry.Host/openapi/v1.json</c>) is called with tenant B's token against tenant A's data. A new
/// endpoint is attacked automatically; an endpoint that legitimately works without a tenant must be listed in
/// <see cref="Exemptions"/> with a reason, and a route parameter needs an attack value in <see cref="AttackValue"/>.
/// </summary>
[Trait("Story", "US-P1-TEN-03")]
[Trait("TestCase", "TC-P1-TEN-002")]
public sealed class TenantIsolationSuite(IsolationHost host) : IClassFixture<IsolationHost>
{
    /// <summary>Operations without tenant data, reviewed one by one. Adding one is a security decision.</summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["ProvisionTenant"] = "Platform operator only (policy platform-operator); a tenant token is refused with 403.",
        ["ListPlans"] = "Plan catalogue: platform data, platform operators only; a tenant token is refused with 403.",
        ["GetPlanVersions"] = "Plan catalogue: platform data, platform operators only; a tenant token is refused with 403.",
        ["PublishPlan"] = "Plan catalogue: platform data, platform operators only; a tenant token is refused with 403.",
        ["GetTenantSubscription"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["ChangeTenantSubscription"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["ActOnTenantSubscription"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["ListTenantInvoices"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["RecordTenantInvoice"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["PayTenantInvoice"] = "Platform operators only (policy platform-operator); a tenant token is refused with 403 (tested in Billing).",
        ["GetTenantBranding"] = "Public login branding: slug, name, colour and logo address of the company named in the URL, by design (threat model R-07).",
        ["GetTenantLogo"] = "Public login branding: the logo of the company named in the URL, by design (threat model R-07).",
    };

    private static readonly JsonElement Contract = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(PostgresServer.RepositoryRoot(), "src", "backend", "Karry.Host", "openapi", "v1.json"))).RootElement;

    public static TheoryData<string, string, string> Operations()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var path in Contract.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var id = operation.Value.TryGetProperty("operationId", out var value) ? value.GetString()! : string.Empty;
                data.Add(id, operation.Name.ToUpperInvariant(), path.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Another_tenant_never_reads_or_changes_tenant_A_data(string operationId, string method, string pathTemplate)
    {
        operationId.Should().NotBeEmpty($"{method} {pathTemplate} needs .WithName(...) so it can be reviewed and reported");
        if (Exemptions.ContainsKey(operationId))
        {
            return;
        }

        var before = await TenantRowCountsAsync(host.A.Id);
        var (path, targetsA) = Fill(pathTemplate);

        var response = await SendAsync(method, path, TestTokens.ForTenant(host.B.Id, IsolationHost.AttackerSubject));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().BeLessThan(500, $"{operationId} must handle a foreign tenant cleanly");
        if (targetsA)
        {
            response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.NotFound],
                $"{operationId} addressed tenant A's resource with tenant B's token");
        }

        body.Should().NotContainAny([.. host.A.Markers, host.AUser.Id.ToString(), host.AUser.LastName, host.AUser.Email, host.ARole, "Alpharole", host.ASite.ToString(), "Alphasite", "ALPHA-SITE", "Alphashift", "Alphaday",
            host.AMaterial.ToString(), "Alphamaterial", "ALPHA-MAT", host.AAsset.ToString(), "Alphamachine", "ALPHA-PEL", "ALPHA 0001", host.AMeter.ToString(), "4242.5", IsolationHost.ALabelToken,
            host.AImport.ToString(), "Alphaimport.xlsx", "ALPHA-IMPORT"],
            $"{operationId} returned tenant A's data to tenant B");
        (await TenantRowCountsAsync(host.A.Id)).Should().Equal(before, $"{operationId} changed tenant A's rows");
    }

    [Fact]
    public void Every_exemption_names_an_existing_operation_and_gives_a_reason()
    {
        var operations = Operations().Select(row => row.Data.Item1).ToHashSet(StringComparer.Ordinal);
        foreach (var (operation, reason) in Exemptions)
        {
            operations.Should().Contain(operation, "a stale exemption hides nothing but suggests a review that did not happen");
            reason.Length.Should().BeGreaterThan(20);
        }
    }

    [Fact]
    public async Task Positive_control_tenant_A_sees_its_own_data_so_the_markers_are_detectable()
    {
        var response = await SendAsync("GET", "/api/v1/tenancy/current", TestTokens.ForTenant(host.A.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().ContainAll(host.A.Markers);
    }

    [Fact]
    public async Task Positive_control_the_attacker_is_a_real_administrator_of_its_own_tenant()
    {
        var own = await SendAsync("GET", "/api/v1/identity/users", TestTokens.ForTenant(host.B.Id, IsolationHost.AttackerSubject));
        var victim = await SendAsync("GET", "/api/v1/identity/users", TestTokens.ForTenant(host.A.Id, "someone-of-a"));

        own.StatusCode.Should().Be(HttpStatusCode.OK, "user management really runs for the attacker");
        (await own.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("Bravoadmin").And.NotContain("Alphauser");
        victim.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an unknown subject of A is no administrator");
    }

    [Fact]
    public async Task The_published_contract_is_the_one_the_suite_attacks()
    {
        var published = await host.Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var runtime = JsonDocument.Parse(published).RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Order();

        runtime.Should().Equal(Contract.GetProperty("paths").EnumerateObject().Select(p => p.Name).Order(),
            "openapi/v1.json is regenerated by the build; commit it");
    }

    /// <summary>Route values that point at tenant A's resources; a new parameter must be added here (and seeded).</summary>
    private (string Path, bool TargetsA) Fill(string template)
    {
        var path = template;
        var targetsA = false;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(template, "{([^}:]+)[^}]*}"))
        {
            var value = AttackValue(match.Groups[1].Value, template)
                ?? throw new InvalidOperationException($"No attack value for route parameter '{match.Groups[1].Value}' in {template}: add one pointing at tenant A's data.");
            path = path.Replace(match.Value, Uri.EscapeDataString(value), StringComparison.Ordinal);
            targetsA = true;
        }

        return (path, targetsA);
    }

    private string? AttackValue(string parameter, string template) => parameter switch
    {
        "id" when template.StartsWith("/api/v1/identity/users/", StringComparison.Ordinal) => host.AUser.Id.ToString(),
        "siteId" => host.ASite.ToString(),
        "effectiveFrom" or "date" when template.StartsWith("/api/v1/organization/sites/", StringComparison.Ordinal) => IsolationHost.AShiftDate,
        "materialId" => host.AMaterial.ToString(),
        "assetId" => host.AAsset.ToString(),
        "meterId" => host.AMeter.ToString(),
        "importId" => host.AImport.ToString(),
        "label" => "KRY1:" + IsolationHost.ALabelToken,
        // A template lists the caller's own values (e.g. site codes); B asking for one must never see A's.
        "kind" when template.StartsWith("/api/v1/imports/", StringComparison.Ordinal) => "assets",
        "effectiveFrom" when template.StartsWith("/api/v1/materials/", StringComparison.Ordinal) => IsolationHost.AShiftDate,
        "id" or "tenantId" => host.A.Id.ToString(),
        "slug" => host.A.Slug,
        "code" when template.StartsWith("/api/v1/identity/roles/", StringComparison.Ordinal) => host.ARole,
        "key" when template.StartsWith("/api/v1/flags/", StringComparison.Ordinal) => "portal.usage-banner", // a real flag, so the change would apply
        _ => null,
    };

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        if (method is "PUT" && path.EndsWith("/photo", StringComparison.Ordinal))
        {
            // A real image, so only the asset check can refuse it (the photo routes accept images, not JSON).
            request.Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        }
        else if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent($$"""{"tenantId":"{{host.A.Id}}"}""", Encoding.UTF8, "application/json");
        }

        return await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Rows of tenant A in every table that has a tenant_id column (read as administrator, past RLS).</summary>
    private async Task<IReadOnlyList<string>> TenantRowCountsAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT format('%I.%I', table_schema, table_name) FROM information_schema.columns
            WHERE column_name = 'tenant_id' AND data_type = 'uuid'
              AND table_schema NOT IN ('pg_catalog', 'information_schema') AND table_schema NOT LIKE '\_timescaledb%'
            ORDER BY 1
            """, connection))
        await using (var reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new List<string>();
        foreach (var table in tables)
        {
            // Table names come from the catalog, quoted by format(%I).
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @tenant", connection);
            count.Parameters.AddWithValue("tenant", tenant);
            counts.Add($"{table}={await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)}");
        }

        return counts;
    }
}
