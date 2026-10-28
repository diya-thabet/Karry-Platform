using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Identity.Domain;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Site tablets on the real pipeline (US-P1-OFF-02, ADR-0028): enrollment codes for the sites one manages, the
/// anonymous enrollment with a proof of the tablet's key, device-signed requests (signature, clock, replay, method and
/// path, tenant), revocation, and the online PIN unlock of a field user with the server's lockout.
/// </summary>
[Trait("Story", "US-P1-OFF-02")]
public sealed class DeviceApiTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private const string Devices = "/api/v1/identity/devices";

    private readonly TenantMembers api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-OFF-001")]
    public async Task An_administrator_enrols_a_tablet_for_a_site_and_the_tablet_then_calls_as_itself()
    {
        var company = await CompanyAsync();

        var created = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", company.Admin, new { siteId = company.Site }), HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        created.GetProperty("url").GetString().Should().Be($"https://{company.Slug}.field.karry.test/enroll#{code}");
        created.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow().AddMinutes(15));

        using var tablet = new Tablet(host);
        var enrolled = await TenantMembers.JsonAsync(await tablet.EnrollAsync(company.Slug, code), HttpStatusCode.Created);
        enrolled.GetProperty("tenantId").GetGuid().Should().Be(company.Id);
        enrolled.GetProperty("siteId").GetGuid().Should().Be(company.Site);
        Convert.FromBase64String(enrolled.GetProperty("pinKey").GetString()!).Should().HaveCount(32);
        enrolled.GetProperty("settings").GetProperty("autoLockMinutes").GetInt32().Should().Be(5);
        enrolled.GetProperty("settings").GetProperty("offlineSessionHours").GetInt32().Should().Be(72);

        var self = await TenantMembers.JsonAsync(await tablet.SendAsync(HttpMethod.Get, $"{Devices}/me"), HttpStatusCode.OK);
        self.GetProperty("deviceId").GetGuid().Should().Be(tablet.Id);
        self.GetProperty("status").GetString().Should().Be("active");

        var list = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Devices, company.Admin), HttpStatusCode.OK);
        list.EnumerateArray().Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be("Tablette pesage");
        (await api.LedgerAsync(company.Id)).Select(e => e.Type).Should().Contain(["identity.device_enrollment_created.v1", "identity.device_enrolled.v1"]);
        (await api.LedgerAsync(company.Id)).Select(e => e.Payload.ToString()).Should().NotContain(p => p.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OFF-002")]
    public async Task An_expired_used_or_unknown_code_or_another_company_s_code_enrols_nothing()
    {
        var company = await CompanyAsync();
        var other = await CompanyAsync();
        using var tablet = new Tablet(host);

        var code = await CodeAsync(company);
        await TenantMembers.ExpectProblemAsync(await tablet.EnrollAsync(other.Slug, code), HttpStatusCode.NotFound, "devices.enrollment_unknown");
        await TenantMembers.ExpectProblemAsync(await tablet.EnrollAsync("no-such-company", code), HttpStatusCode.NotFound, "devices.enrollment_unknown");
        await TenantMembers.ExpectProblemAsync(await tablet.EnrollAsync(company.Slug, "0000-0000-0000-0000"), HttpStatusCode.NotFound, "devices.enrollment_unknown");
        await TenantMembers.ExpectProblemAsync(await tablet.EnrollAsync(company.Slug, "not a code"), HttpStatusCode.NotFound, "devices.enrollment_unknown");

        // Typed carelessly (lower case, spaces, O for 0) it still works, once.
        var typed = code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal).Replace('0', 'o');
        (await tablet.EnrollAsync(company.Slug, typed)).StatusCode.Should().Be(HttpStatusCode.Created);
        using var second = new Tablet(host);
        await TenantMembers.ExpectProblemAsync(await second.EnrollAsync(company.Slug, code), HttpStatusCode.UnprocessableEntity, "devices.enrollment_expired");

        var late = await CodeAsync(company);
        host.Clock.Advance(TimeSpan.FromMinutes(15));
        await TenantMembers.ExpectProblemAsync(await second.EnrollAsync(company.Slug, late), HttpStatusCode.UnprocessableEntity, "devices.enrollment_expired");
    }

    [Fact]
    public async Task A_proof_by_another_key_is_refused_and_leaves_the_code_usable()
    {
        var company = await CompanyAsync();
        var code = await CodeAsync(company);
        using var tablet = new Tablet(host);
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await TenantMembers.ExpectProblemAsync(await tablet.EnrollAsync(company.Slug, code, proofKey: thief), HttpStatusCode.BadRequest, "devices.key_invalid");
        (await tablet.EnrollAsync(company.Slug, code)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_site_manager_enrols_and_sees_only_the_tablets_of_their_sites_and_an_operator_none()
    {
        var company = await CompanyAsync();
        var otherSite = host.Sites.Add($"S{Guid.NewGuid():N}"[..12]);
        var manager = await api.MemberAsync(company.Id, (Roles.SiteManager, company.Site));
        var operatorToken = await api.MemberAsync(company.Id, (Roles.Operator, company.Site));
        await EnrolAsync(company, company.Site);
        await EnrolAsync(company, otherSite);

        (await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", manager, new { siteId = company.Site })).StatusCode.Should().Be(HttpStatusCode.Created);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", manager, new { siteId = otherSite }), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", company.Admin, new { siteId = Guid.CreateVersion7() }), HttpStatusCode.BadRequest, "devices.site_invalid");
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", operatorToken, new { siteId = company.Site }));
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, Devices, operatorToken));

        var seen = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Devices, manager), HttpStatusCode.OK);
        seen.EnumerateArray().Select(d => d.GetProperty("siteId").GetGuid()).Should().Equal(company.Site);
        var all = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Devices, company.Admin), HttpStatusCode.OK);
        all.GetArrayLength().Should().Be(2);
        var foreign = all.EnumerateArray().Single(d => d.GetProperty("siteId").GetGuid() == otherSite).GetProperty("id").GetGuid();
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/{foreign}/revoke", manager), HttpStatusCode.NotFound, "devices.not_found");
    }

    [Fact]
    public async Task A_device_request_is_bound_to_its_signature_clock_method_path_and_single_use()
    {
        var company = await CompanyAsync();
        using var tablet = await EnrolAsync(company, company.Site);
        var me = $"{Devices}/me";

        var once = tablet.Assertion("GET", me);
        (await tablet.SendWithAsync(HttpMethod.Get, me, once)).StatusCode.Should().Be(HttpStatusCode.OK);
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, once), HttpStatusCode.Unauthorized, "devices.assertion_replayed");

        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("GET", $"{Devices}/unlock")), HttpStatusCode.Unauthorized, "devices.assertion_invalid");
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("POST", me)), HttpStatusCode.Unauthorized, "devices.assertion_invalid");
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("GET", me, lifetime: TimeSpan.FromMinutes(6))), HttpStatusCode.Unauthorized, "devices.assertion_invalid");
        var tampered = tablet.Assertion("GET", me);
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, tampered[..^4] + (tampered[^4] == 'A' ? "BAAA" : "AAAA")), HttpStatusCode.Unauthorized, "devices.assertion_invalid");
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, "not.a.jws"), HttpStatusCode.Unauthorized, "devices.assertion_invalid");

        // Another company's ID in the assertion: the device is looked up there and not found.
        var other = await CompanyAsync();
        await TenantMembers.ExpectProblemAsync(await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("GET", me, tenant: other.Id)), HttpStatusCode.Unauthorized, "devices.assertion_invalid");

        // A clock far off either way: refused with the server's time, so the tablet can correct its offset.
        foreach (var offset in new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(-10) })
        {
            var response = await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("GET", me, issuedAt: host.Clock.GetUtcNow() + offset));
            var problem = await TenantMembers.JsonAsync(response, HttpStatusCode.Unauthorized);
            problem.GetProperty("code").GetString().Should().Be("devices.clock_skew");
            problem.GetProperty("serverTime").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow());
        }

        (await tablet.SendWithAsync(HttpMethod.Get, me, tablet.Assertion("GET", me, issuedAt: host.Clock.GetUtcNow().AddSeconds(50)))).StatusCode.Should().Be(HttpStatusCode.OK, "a minute of leeway");
    }

    [Fact]
    public async Task A_device_reaches_only_device_endpoints_and_a_person_s_token_never_reaches_them()
    {
        var company = await CompanyAsync();
        using var tablet = await EnrolAsync(company, company.Site);

        (await tablet.SendAsync(HttpMethod.Get, "/api/v1/identity/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await tablet.SendAsync(HttpMethod.Get, Devices)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.SendAsync(HttpMethod.Get, $"{Devices}/me", company.Admin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.SendAsync(HttpMethod.Post, $"{Devices}/unlock", company.Admin, new { employeeId = "X", pin = "804613" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OFF-008")]
    public async Task A_revoked_tablet_is_refused_and_a_re_enrolled_one_is_a_new_device()
    {
        var company = await CompanyAsync();
        using var lost = await EnrolAsync(company, company.Site);

        var revoked = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/{lost.Id}/revoke", company.Admin), HttpStatusCode.OK);
        revoked.GetProperty("status").GetString().Should().Be("revoked");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/{lost.Id}/revoke", company.Admin), HttpStatusCode.Conflict, "devices.already_revoked");
        await TenantMembers.ExpectProblemAsync(await lost.SendAsync(HttpMethod.Get, $"{Devices}/me"), HttpStatusCode.Unauthorized, "devices.revoked");
        await TenantMembers.ExpectProblemAsync(await lost.SendAsync(HttpMethod.Post, $"{Devices}/unlock", new { employeeId = "X", pin = "804613" }), HttpStatusCode.Unauthorized, "devices.revoked");
        (await api.LedgerAsync(company.Id)).Should().ContainSingle(e => e.Type == "identity.device_revoked.v1");

        // Cleared site data: the tablet enrols again with a new key and becomes a new device; the old one stays listed.
        using var again = await EnrolAsync(company, company.Site);
        again.Id.Should().NotBe(lost.Id);
        (await again.SendAsync(HttpMethod.Get, $"{Devices}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Devices, company.Admin), HttpStatusCode.OK);
        list.EnumerateArray().Select(d => d.GetProperty("status").GetString()).Should().BeEquivalentTo(["active", "revoked"]);
    }

    [Fact]
    [Trait("Story", "US-P1-OFF-04")]
    public async Task A_field_user_of_the_site_unlocks_online_and_five_wrong_pins_lock_them_for_fifteen_minutes()
    {
        var company = await CompanyAsync();
        using var tablet = await EnrolAsync(company, company.Site);
        var (user, employeeId) = await FieldUserAsync(company, company.Site, "804613");

        var unlocked = await TenantMembers.JsonAsync(await tablet.UnlockAsync(employeeId.ToLowerInvariant(), "804613"), HttpStatusCode.OK);
        unlocked.GetProperty("userId").GetGuid().Should().Be(user);
        unlocked.GetProperty("serverTime").GetDateTimeOffset().Should().Be(host.Clock.GetUtcNow());

        for (var i = 0; i < 4; i++)
        {
            await TenantMembers.ExpectProblemAsync(await tablet.UnlockAsync(employeeId, "111112"), HttpStatusCode.Unauthorized, "identity.pin_wrong");
        }

        await TenantMembers.ExpectProblemAsync(await tablet.UnlockAsync(employeeId, "111112"), HttpStatusCode.Unauthorized, "identity.pin_locked");
        await TenantMembers.ExpectProblemAsync(await tablet.UnlockAsync(employeeId, "804613"), HttpStatusCode.Unauthorized, "identity.pin_locked");
        var locked = (await api.LedgerAsync(company.Id)).Single(e => e.Type == "identity.user_pin_locked.v1").Payload;
        locked.GetProperty("userId").GetGuid().Should().Be(user);
        locked.GetProperty("deviceId").GetGuid().Should().Be(tablet.Id);

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        (await tablet.UnlockAsync(employeeId, "804613")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    [Trait("TestCase", "TC-P1-IAM-004")]
    public async Task Only_active_field_users_of_the_tablet_s_site_unlock_it()
    {
        var company = await CompanyAsync();
        var otherSite = host.Sites.Add($"S{Guid.NewGuid():N}"[..12]);
        using var tablet = await EnrolAsync(company, company.Site);
        var (_, elsewhere) = await FieldUserAsync(company, otherSite, "804613");
        var (gone, deactivated) = await FieldUserAsync(company, company.Site, "804613");
        var (_, noPin) = await FieldUserAsync(company, company.Site, pin: null);
        await ExecuteAsync("UPDATE identity.users SET status = 'Deactivated' WHERE id = $1", gone);

        foreach (var employeeId in new[] { elsewhere, deactivated, noPin, "NOBODY-42" })
        {
            await TenantMembers.ExpectProblemAsync(await tablet.UnlockAsync(employeeId, "804613"), HttpStatusCode.Unauthorized, "identity.unlock_refused");
        }

        await TenantMembers.ExpectProblemAsync(await tablet.UnlockAsync(string.Empty, "804613"), HttpStatusCode.BadRequest, "request.invalid");
    }

    private async Task<Company> CompanyAsync()
    {
        var (id, slug) = await api.CompanyAsync();
        var site = host.Sites.Add($"S{Guid.NewGuid():N}"[..12]);
        return new Company(id, slug, site, await api.MemberAsync(id, (Roles.TenantAdmin, null)));
    }

    private async Task<string> CodeAsync(Company company, Guid? site = null) =>
        (await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, $"{Devices}/enrollments", company.Admin, new { siteId = site ?? company.Site }), HttpStatusCode.Created))
            .GetProperty("code").GetString()!;

    private async Task<Tablet> EnrolAsync(Company company, Guid site)
    {
        var tablet = new Tablet(host);
        (await tablet.EnrollAsync(company.Slug, await CodeAsync(company, site))).StatusCode.Should().Be(HttpStatusCode.Created);
        return tablet;
    }

    /// <summary>A field user with a role on <paramref name="site"/> and a PIN hashed with few iterations (fast tests, same format).</summary>
    private async Task<(Guid Id, string EmployeeId)> FieldUserAsync(Company company, Guid site, string? pin)
    {
        var employeeId = $"OP-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var id = await api.InsertAsync(company.Id, "Field", [(Roles.Operator, site)], subject: null, employeeId);
        if (pin is not null)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 1000, HashAlgorithmName.SHA256, 32);
            await ExecuteAsync("UPDATE identity.users SET pin_hash = $2 WHERE id = $1", id, $"pbkdf2-sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
        }

        return (id, employeeId);
    }

    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record Company(Guid Id, string Slug, Guid Site, string Admin);

    /// <summary>A tablet as the field app is: a P-256 key that signs its enrollment proof and every request (ADR-0028 §3).</summary>
    private sealed class Tablet(IdentityHost host) : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Guid Id { get; private set; }

        public Guid Tenant { get; private set; }

        public async Task<HttpResponseMessage> EnrollAsync(string slug, string code, ECDsa? proofKey = null)
        {
            var point = key.ExportParameters(false).Q;
            var proof = (proofKey ?? key).SignData(Encoding.UTF8.GetBytes($"karry-enroll:{code}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var response = await Send(HttpMethod.Post, $"{Devices}/enroll", null, new
            {
                tenant = slug,
                code,
                publicKey = new { kty = "EC", crv = "P-256", x = Base64Url.EncodeToString(point.X), y = Base64Url.EncodeToString(point.Y) },
                proof = Base64Url.EncodeToString(proof),
                name = "Tablette pesage",
                model = "Galaxy Tab A9",
                platform = "Android 14",
                appVersion = "1.0.0",
            });
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Id = body.GetProperty("deviceId").GetGuid();
                Tenant = body.GetProperty("tenantId").GetGuid();
            }

            return response;
        }

        public Task<HttpResponseMessage> UnlockAsync(string employeeId, string pin) =>
            SendAsync(HttpMethod.Post, $"{Devices}/unlock", new { employeeId, pin });

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
            Send(method, path, Assertion(method.Method, path), body);

        public Task<HttpResponseMessage> SendWithAsync(HttpMethod method, string path, string assertion) => Send(method, path, assertion, null);

        /// <summary>A compact JWS ES256 for one request, issued now by the server's clock unless told otherwise.</summary>
        public string Assertion(string method, string path, DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null, Guid? tenant = null)
        {
            var iat = issuedAt ?? host.Clock.GetUtcNow();
            var header = Part(new { alg = "ES256", typ = "karry-device+jwt", kid = Id.ToString("D") });
            var payload = Part(new
            {
                tid = (tenant ?? Tenant).ToString("D"),
                iat = iat.ToUnixTimeSeconds(),
                exp = (iat + (lifetime ?? TimeSpan.FromMinutes(5))).ToUnixTimeSeconds(),
                jti = Guid.NewGuid().ToString("N"),
                htm = method,
                htu = path,
            });
            var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
        }

        public void Dispose() => key.Dispose();

        private static string Part(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

        private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? assertion, object? body)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (assertion is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Device", assertion);
            }

            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await host.Client.SendAsync(request, Ct);
        }
    }
}
