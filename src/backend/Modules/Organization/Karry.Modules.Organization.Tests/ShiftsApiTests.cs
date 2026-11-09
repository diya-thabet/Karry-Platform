using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Domain;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Organization.Tests;

/// <summary>
/// Shift patterns, non-working days and instances over HTTP (US-P1-ORG-02): each change in the company's ledger and
/// applied at once to the stored instances of the next 30 days; history never rewritten; a site manager on their sites.
/// </summary>
[Trait("Story", "US-P1-ORG-02")]
public sealed class ShiftsApiTests(OrganizationHost host) : IClassFixture<OrganizationHost>
{
    private const string Zone = "Africa/Tunis";

    private static readonly string[] Week = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly object TwoTwelves = new
    {
        shifts = new[]
        {
            new { code = "DAY", name = "Jour", start = "06:00", end = "18:00", days = Week },
            new { code = "NIGHT", name = "Nuit", start = "18:00", end = "06:00", days = Week },
        },
    };

    private static readonly object ThreeEights = new
    {
        shifts = new[]
        {
            new { code = "M", name = "Matin", start = "06:00", end = "14:00", days = Week },
            new { code = "A", name = "Après-midi", start = "14:00", end = "22:00", days = Week },
            new { code = "N", name = "Nuit", start = "22:00", end = "06:00", days = Week },
        },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today(string zone = Zone) => ShiftCalendar.Today(zone, DateTimeOffset.UtcNow);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_first_pattern_is_ledgered_and_stores_the_shifts_of_the_next_30_days_at_once()
    {
        var (tenant, token, site) = await SiteAsync();
        var from = Today().AddDays(-3);

        var set = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(from)}", token, TwoTwelves);

        set.StatusCode.Should().Be(HttpStatusCode.OK, "the first pattern may start on any date");
        var pattern = await Json(set);
        pattern.GetProperty("effectiveFrom").GetString().Should().Be(Iso(from));
        pattern.GetProperty("shifts")[1].GetProperty("end").GetString().Should().Be("06:00");
        (await LedgerAsync(tenant)).Should().Be("organization.site_created.v1,organization.shift_pattern_set.v1");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(60, "2 shifts × 30 days");
        (await ScalarAsync<DateOnly>($"SELECT min(date) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(Today());
        (await ScalarAsync<DateOnly>($"SELECT max(date) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(Today().AddDays(29));
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-patterns", token))).Should().ContainSingle();
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-instances", token)))
            .Should().HaveCount(60, "without a range: the next 30 days, as stored");
    }

    [Fact]
    public async Task A_new_version_changes_only_the_future_and_the_past_cannot_change()
    {
        var (tenant, token, site) = await SiteAsync();
        await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today().AddDays(-10))}", token, TwoTwelves);
        var change = Today().AddDays(10);

        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(change)}", token, ThreeEights)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ScalarAsync<string>($"SELECT string_agg(DISTINCT shift_code, ',' ORDER BY shift_code) FROM organization.shift_instances WHERE site_id = '{site}' AND date < '{Iso(change)}'"))
            .Should().Be("DAY,NIGHT");
        (await ScalarAsync<string>($"SELECT string_agg(DISTINCT shift_code, ',' ORDER BY shift_code) FROM organization.shift_instances WHERE site_id = '{site}' AND date >= '{Iso(change)}'"))
            .Should().Be("A,M,N");

        foreach (var past in new[] { Today(), Today().AddDays(-1) })
        {
            var refused = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(past)}", token, ThreeEights);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(refused)).GetProperty("code").GetString().Should().Be("organization.date_in_past");
        }

        (await SendAsync(HttpMethod.Delete, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today().AddDays(-10))}", token)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a version in effect stays");
        (await SendAsync(HttpMethod.Delete, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(change)}", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}' AND shift_code IN ('M', 'A', 'N')")).Should().Be(0);
        (await LedgerAsync(tenant)).Should().EndWith("organization.shift_pattern_set.v1,organization.shift_pattern_set.v1,organization.shift_pattern_removed.v1");
    }

    [Fact]
    public async Task A_non_working_day_removes_its_shifts_and_putting_it_back_restores_them()
    {
        var (tenant, token, site) = await SiteAsync();
        await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today())}", token, TwoTwelves);
        var holiday = Today().AddDays(5);

        var set = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/non-working-days/{Iso(holiday)}", token, new { kind = "public-holiday", name = "Fête du travail" });

        set.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(set)).GetProperty("kind").GetString().Should().Be("public-holiday");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}' AND date = '{Iso(holiday)}'")).Should().Be(0);
        var listed = await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/non-working-days?from={Iso(Today())}&to={Iso(Today().AddDays(365))}", token));
        listed.Single().GetProperty("name").GetString().Should().Be("Fête du travail");

        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/non-working-days/{Iso(Today().AddDays(-1))}", token, new { kind = "closure", name = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the past is not rewritten");
        (await SendAsync(HttpMethod.Delete, $"/api/v1/organization/sites/{site}/non-working-days/{Iso(holiday)}", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}' AND date = '{Iso(holiday)}'")).Should().Be(2);
        (await LedgerAsync(tenant)).Should().EndWith("organization.non_working_day_set.v1,organization.non_working_day_removed.v1");
    }

    [Fact]
    public async Task Any_range_is_computed_across_clock_changes_without_being_stored()
    {
        var (_, token, site) = await SiteAsync("Europe/Paris");
        await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/2026-01-01", token, TwoTwelves);

        var march = await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-instances?from=2026-03-28&to=2026-03-28", token));
        var october = await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-instances?from=2026-10-24&to=2026-10-24", token));

        march.Single(s => s.GetProperty("code").GetString() == "NIGHT").GetProperty("lengthMinutes").GetInt32().Should().Be(11 * 60);
        october.Single(s => s.GetProperty("code").GetString() == "NIGHT").GetProperty("lengthMinutes").GetInt32().Should().Be(13 * 60);
        october[0].GetProperty("startsAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 24, 4, 0, 0, TimeSpan.Zero));
        (await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-instances?from=2026-01-01&to=2027-01-02", token)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "at most 366 days");
        (await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{site}/shift-instances?from=2026-02-01&to=2026-01-01", token)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_site_manager_sets_shifts_of_their_site_only_and_an_operator_only_reads()
    {
        var (tenant, admin, north) = await SiteAsync();
        var south = await CreateSiteAsync(admin, "SUD", Zone);
        host.SiteOf[OrganizationHost.SiteManager] = north;
        host.SiteOf[OrganizationHost.Operator] = north;
        var manager = TestTokens.ForTenant(tenant, OrganizationHost.SiteManager);
        var operatorToken = TestTokens.ForTenant(tenant, OrganizationHost.Operator);

        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{north}/shift-patterns/{Iso(Today())}", manager, TwoTwelves)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{south}/shift-patterns/{Iso(Today())}", manager, TwoTwelves)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "another site does not exist for them");
        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{north}/non-working-days/{Iso(Today().AddDays(1))}", operatorToken, new { kind = "closure", name = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{north}/shift-instances?from={Iso(Today())}&to={Iso(Today())}", operatorToken)))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task Reading_every_site_does_not_extend_changing_shifts_beyond_the_managed_site()
    {
        var (tenant, admin, north) = await SiteAsync();
        var south = await CreateSiteAsync(admin, "SUD", Zone);
        host.SiteOf[OrganizationHost.ManagerAndExecutive] = north;
        var token = TestTokens.ForTenant(tenant, OrganizationHost.ManagerAndExecutive);

        (await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{south}/shift-patterns", token)).StatusCode.Should().Be(HttpStatusCode.OK, "the executive role reads it");
        var refused = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{south}/shift-patterns/{Iso(Today())}", token, TwoTwelves);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("organization.site_not_managed");
        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{north}/shift-patterns/{Iso(Today())}", token, TwoTwelves)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Archiving_a_site_removes_its_future_shifts_and_refuses_new_ones_and_restoring_brings_them_back()
    {
        var (_, token, site) = await SiteAsync();
        await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today().AddDays(1))}", token, TwoTwelves);

        await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{site}/archive", token);

        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(0);
        var refused = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today().AddDays(2))}", token, ThreeEights);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("organization.site_archived");

        await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{site}/restore", token);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(58, "29 days from tomorrow");
    }

    [Fact]
    public async Task Overlapping_shifts_are_refused_with_the_codes_involved()
    {
        var (_, token, site) = await SiteAsync();
        var overlapping = new
        {
            shifts = new[]
            {
                new { code = "DAY", name = "Jour", start = "06:00", end = "18:00", days = Week },
                new { code = "LONG", name = "Longue", start = "17:00", end = "20:00", days = new[] { "fri" } },
            },
        };

        var refused = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today())}", token, overlapping);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await Json(refused);
        problem.GetProperty("code").GetString().Should().Be("organization.shifts_overlap");
        problem.GetProperty("shifts").GetString().Should().Be("DAY,LONG");
    }

    [Fact]
    public async Task The_generator_fills_missing_days_is_idempotent_and_never_touches_a_started_shift()
    {
        var (_, token, site) = await SiteAsync();
        await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{Iso(Today().AddDays(-1))}", token, TwoTwelves);
        await ScalarAsync<int>($"DELETE FROM organization.shift_instances WHERE site_id = '{site}' AND date > '{Iso(Today().AddDays(1))}' RETURNING 1");
        var started = DateTimeOffset.UtcNow.AddHours(-1); // under way now, whatever the time of day the test runs
        await ScalarAsync<int>(
            $"UPDATE organization.shift_instances SET starts_at = '{started:O}', ends_at = '{started.AddHours(2):O}', shift_name = 'Changée' WHERE site_id = '{site}' AND date = '{Iso(Today())}' AND shift_code = 'DAY' RETURNING 1");

        var run = await host.Services.GetRequiredService<GenerateShiftInstances>().RunAsync(Ct);

        run.Failed.Should().Be(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE site_id = '{site}'")).Should().Be(60);
        (await ScalarAsync<string>($"SELECT shift_name FROM organization.shift_instances WHERE site_id = '{site}' AND date = '{Iso(Today())}' AND shift_code = 'DAY'"))
            .Should().Be("Changée", "a shift that has started is never changed");
        var again = await host.Services.GetRequiredService<GenerateShiftInstances>().RunAsync(Ct);
        (again.Added + again.Changed + again.Removed).Should().Be(0, "a second run has nothing to do");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Many_people_setting_the_same_pattern_or_day_at_once_never_get_a_server_error()
    {
        // Hardening plan H-C1: a pattern or non-working day is set by its date (a PUT of the whole thing). Saved at the
        // same moment by several people, each answer is the saved state or a 409 (try again), never a 500, and one
        // row is stored per date with the shifts that follow from it.
        var (_, token, site) = await SiteAsync();
        var from = Iso(Today().AddDays(5));
        var day = Iso(Today().AddDays(9));
        object[] patterns = [TwoTwelves, ThreeEights, TwoTwelves, ThreeEights, TwoTwelves, ThreeEights];

        var sets = await Task.WhenAll(patterns.Select(p => SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/shift-patterns/{from}", token, p)));
        var days = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{site}/non-working-days/{day}", token, new { kind = "public-holiday", name = $"Fête {i}" })));

        foreach (var answer in sets.Concat(days))
        {
            answer.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Conflict], await answer.Content.ReadAsStringAsync(Ct));
        }

        sets.Should().Contain(a => a.StatusCode == HttpStatusCode.OK);
        days.Should().Contain(a => a.StatusCode == HttpStatusCode.OK);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_patterns WHERE site_id = '{site}'")).Should().Be(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.non_working_days WHERE site_id = '{site}'")).Should().Be(1);
    }

    private async Task<(Guid Tenant, string Token, Guid Site)> SiteAsync(string zone = Zone)
    {
        var slug = $"s-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        var token = TestTokens.ForTenant(tenant);
        return (tenant, token, await CreateSiteAsync(token, "NORD", zone));
    }

    private async Task<Guid> CreateSiteAsync(string token, string code, string zone)
    {
        var created = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, new
        {
            code,
            name = $"Carrière {code}",
            type = "quarry",
            address = new { line = "Route de Bizerte km 12", city = "Tunis", region = (string?)null, postalCode = "1000", country = "TN" },
            location = new { latitude = 36.8045, longitude = 10.0056 },
            boundary = (object?)null,
            timeZone = zone,
            currency = (string?)null,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(created)).GetProperty("id").GetGuid();
    }

    private Task<string> LedgerAsync(Guid tenant) =>
        ScalarAsync<string>($"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'organization.%'");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<IReadOnlyList<JsonElement>> Items(HttpResponseMessage response) =>
        [.. (await Json(response)).GetProperty("items").EnumerateArray()];

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
