using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.SharedKernel.Canonical;
using Karry.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// Uploads (ADR-0028 §6, §3; US-P1-OFF-02 AC3, TC-P1-OFF-005): signed commands stored in <c>sync.inbox</c> in each
/// tablet's sequence, duplicates recognised, a revoked tablet's commands kept quarantined. Each test enrols its own
/// tablet, so its sequence starts at 1.
/// </summary>
public sealed class SyncBatchesTests(SyncHost host) : IClassFixture<SyncHost>
{
    private const string Batches = "/api/v1/sync/batches";

    [Fact]
    public async Task A_batch_is_stored_as_signed_in_the_tablet_s_sequence_and_sending_it_again_changes_nothing()
    {
        using var tablet = await host.TabletAsync(host.A);
        var commands = new[] { tablet.Command(1, new { ticket = "T-1", net = "12.340" }), tablet.Command(2, new { ticket = "T-2", net = "8.100" }) };

        var receipt = await UploadAsync(tablet, commands);

        receipt.Should().BeEquivalentTo(new { Results = commands.Select(c => Result(c, "accepted")), LastSequence = 2 });
        var rows = await InboxAsync(tablet.Id);
        rows.Should().BeEquivalentTo(commands.Select(c => new InboxRow(Id(c), c["sequence"]!.GetValue<long>(), "test.item_recorded.v1", Unsigned(c), c["signature"]!.GetValue<string>(), "Received")),
            options => options.WithStrictOrdering());

        var again = await UploadAsync(tablet, commands);

        again.Should().BeEquivalentTo(new { Results = commands.Select(c => Result(c, "duplicate")), LastSequence = 2 });
        (await InboxAsync(tablet.Id)).Should().HaveCount(2);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task The_same_batch_sent_many_times_at_once_is_stored_once()
    {
        // Hardening plan H-C3: a tablet that retries before its first upload answered. Each answer is the receipt
        // (accepted once, duplicates after) or 409 sync.batch_conflict (send again); never a 500 or a second row.
        using var tablet = await host.TabletAsync(host.A);
        var commands = new[] { tablet.Command(1, new { n = 1 }), tablet.Command(2, new { n = 2 }), tablet.Command(3, new { n = 3 }) };

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Upload(tablet, commands)));

        var receipts = new List<Receipt>();
        foreach (var answer in answers)
        {
            if (answer.StatusCode == HttpStatusCode.Conflict)
            {
                await ExpectProblemAsync(answer, HttpStatusCode.Conflict, "sync.batch_conflict");
                continue;
            }

            answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            receipts.Add((await answer.Content.ReadFromJsonAsync<Receipt>(TestContext.Current.CancellationToken))!);
        }

        receipts.Count(r => r.Results.All(c => c.Status == "accepted")).Should().Be(1, "exactly one upload stored the batch");
        receipts.Where(r => r.Results.Any(c => c.Status != "accepted")).Should().AllSatisfy(r => r.Results.Should().OnlyContain(c => c.Status == "duplicate"));
        (await InboxAsync(tablet.Id)).Select(r => r.Sequence).Should().Equal(1, 2, 3);
        (await UploadAsync(tablet, commands)).Results.Should().OnlyContain(c => c.Status == "duplicate", "sending it again later changes nothing");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Different_commands_with_the_same_numbers_sent_at_once_never_share_a_place_in_the_sequence()
    {
        // Two outboxes of one tablet (a restored backup) numbering differently: one number holds one command.
        using var tablet = await host.TabletAsync(host.A);
        var batches = Enumerable.Range(0, 4).Select(i => new[] { tablet.Command(1, new { from = i }), tablet.Command(2, new { from = i }) }).ToList();

        var answers = await Task.WhenAll(batches.Select(b => Upload(tablet, b)));

        answers.Should().OnlyContain(a => a.StatusCode == HttpStatusCode.OK || a.StatusCode == HttpStatusCode.Conflict, "never a 500");
        var rows = await InboxAsync(tablet.Id);
        rows.Select(r => r.Sequence).Should().Equal(1, 2);
        var winner = batches.Single(b => b.Any(c => Id(c) == rows[0].Id));
        rows.Select(r => r.Id).Should().Equal(winner.Select(Id), "both places come from the same upload");
    }

    [Fact]
    public async Task Commands_must_follow_on_without_gaps_and_a_new_command_cannot_reuse_a_number()
    {
        using var tablet = await host.TabletAsync(host.A);
        var (first, second) = (tablet.Command(1, new { n = 1 }), tablet.Command(2, new { n = 2 }));

        var early = await UploadAsync(tablet, [second]);
        var inOrder = await UploadAsync(tablet, [first, second]);
        var reused = tablet.Command(2, new { n = 3 });
        var clash = await UploadAsync(tablet, [reused]);

        early.Should().BeEquivalentTo(new { Results = new[] { Result(second, "rejected", "sync.sequence_gap") }, LastSequence = 0 });
        inOrder.LastSequence.Should().Be(2);
        clash.Should().BeEquivalentTo(new { Results = new[] { Result(reused, "rejected", "sync.sequence_reused") }, LastSequence = 2 });
        (await InboxAsync(tablet.Id)).Select(r => r.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_command_changed_after_signing_or_signed_by_another_tablet_is_rejected_and_nothing_after_it_is_stored()
    {
        using var tablet = await host.TabletAsync(host.A);
        using var other = await host.TabletAsync(host.A);
        var changed = tablet.Command(1, new { net = "12.340" });
        changed["payload"]!["net"] = "99.000";
        var foreign = other.Command(1, new { net = "1.000" });
        var next = tablet.Command(2, new { net = "2.000" });

        var receipt = await UploadAsync(tablet, [changed, foreign, next]);

        receipt.Results.Should().BeEquivalentTo(
            [Result(changed, "rejected", "sync.signature_invalid"), Result(foreign, "rejected", "sync.signature_invalid"), Result(next, "rejected", "sync.sequence_gap")],
            options => options.WithStrictOrdering());
        (await InboxAsync(tablet.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_tablet_s_commands_are_kept_quarantined_never_in_the_sequence_and_the_tablet_is_told()
    {
        using var tablet = await host.TabletAsync(host.A);
        var before = tablet.Command(1, new { n = 1 });
        await UploadAsync(tablet, [before]);
        (await host.SendAsync(HttpMethod.Post, $"{TestTablet.Devices}/{tablet.Id}/revoke", host.Admin(host.A))).EnsureSuccessStatusCode();
        var after = new[] { tablet.Command(2, new { n = 2 }), tablet.Command(9, new { n = 9 }) };

        await ExpectProblemAsync(await Upload(tablet, [before, .. after]), HttpStatusCode.Forbidden, "devices.revoked");
        await ExpectProblemAsync(await Upload(tablet, after), HttpStatusCode.Forbidden, "devices.revoked");

        (await InboxAsync(tablet.Id)).Select(r => (r.Sequence, r.Status)).Should().Equal((1, "Received"), (2, "Quarantined"), (9, "Quarantined"));
        await ExpectProblemAsync(await tablet.SendAsync(HttpMethod.Get, "/api/v1/sync/changes"), HttpStatusCode.Unauthorized, "devices.revoked");
    }

    [Fact]
    public async Task A_malformed_batch_is_refused_whole()
    {
        using var tablet = await host.TabletAsync(host.A);
        var good = tablet.Command(1, new { n = 1 });
        var inexact = tablet.Command(1, new { n = 1 });
        inexact["payload"] = JsonNode.Parse("{\"amount\":12345678901234567890}");
        var upperCase = tablet.Command(1, new { n = 1 });
        upperCase["commandId"] = upperCase["commandId"]!.GetValue<string>().ToUpperInvariant();
        var notV7 = tablet.Command(1, new { n = 1 }, Guid.NewGuid());
        var unversioned = tablet.Command(1, new { n = 1 }, type: "test.item_recorded");
        var noOffset = tablet.Command(1, new { n = 1 });
        noOffset["deviceTime"] = "2026-10-28T09:00:00";
        var arrayPayload = tablet.Command(1, new List<int> { 1 });

        foreach (var command in new[] { inexact, upperCase, notV7, unversioned, noOffset, arrayPayload })
        {
            await ExpectProblemAsync(await Upload(tablet, [good.DeepClone().AsObject(), command]), HttpStatusCode.BadRequest, "request.invalid");
        }

        await ExpectProblemAsync(await Upload(tablet, []), HttpStatusCode.BadRequest, "request.invalid");
        await ExpectProblemAsync(await Upload(tablet, [.. Enumerable.Range(1, 101).Select(i => tablet.Command(i, new { i }))]), HttpStatusCode.BadRequest, "request.invalid");
        (await InboxAsync(tablet.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_a_tablet_uploads()
    {
        var response = await host.SendAsync(HttpMethod.Post, Batches, host.Admin(host.A), new { commands = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Each_company_keeps_its_own_inbox_even_for_the_same_command_id()
    {
        using var tabletA = await host.TabletAsync(host.A);
        using var tabletB = await host.TabletAsync(host.B);
        var commandA = tabletA.Command(1, new { company = "A" });
        await UploadAsync(tabletA, [commandA]);

        var receipt = await UploadAsync(tabletB, [tabletB.Command(1, new { company = "B" }, Id(commandA))]);

        receipt.Results.Single().Status.Should().Be("accepted");
        (await InboxAsync(tabletA.Id)).Single().Body.Should().Contain("\"A\"");
        (await InboxAsync(tabletB.Id)).Single().Body.Should().Contain("\"B\"");
        (await AsTenantAsync(host.B.Id, "SELECT count(*) FROM sync.inbox WHERE device_id = $1", tabletA.Id)).Should().Be(0L);
    }

    [Fact]
    public async Task The_inbox_is_append_only_for_the_application()
    {
        using var tablet = await host.TabletAsync(host.A);
        await UploadAsync(tablet, [tablet.Command(1, new { n = 1 })]);

        var refused = async () => await AsTenantAsync(host.A.Id, "UPDATE sync.inbox SET body = '{}' WHERE device_id = $1", tablet.Id);

        (await refused.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    [Trait("Story", "US-P2-OFF-03")]
    public async Task Each_command_is_handled_once_in_the_upload_and_its_outcome_kept()
    {
        using var tablet = await host.TabletAsync(host.A);
        var recorded = tablet.Command(1, new { n = 1 });
        var flagged = tablet.Command(2, new { n = 2 }, type: TestCommandHandler.Flagged.Name);

        var receipt = await UploadAsync(tablet, [recorded, flagged]);

        receipt.Results.Should().BeEquivalentTo(new[]
        {
            Result(recorded, "accepted"),
            Result(flagged, "conflict") with { Flags = [TestCommandHandler.Flagged.Flag] },
        }, options => options.WithStrictOrdering());
        receipt.LastSequence.Should().Be(2);
        receipt.ServerTime.Should().Be(host.Clock.GetUtcNow());
        (await HandledAsync(tablet.Id)).Should().Equal((Id(recorded), tablet.Site), (Id(flagged), tablet.Site));
        (await ProcessedAsync(tablet.Id)).Should().Equal((Id(recorded), "Accepted", ""), (Id(flagged), "Conflict", TestCommandHandler.Flagged.Flag));

        (await UploadAsync(tablet, [recorded, flagged])).Results.Should().OnlyContain(r => r.Status == "duplicate");
        (await HandledAsync(tablet.Id)).Should().HaveCount(2, "a command sent again is not handled again");
    }

    [Fact]
    [Trait("Story", "US-P2-OFF-03")]
    [Trait("TestCase", "TC-P2-SYNC-009")]
    public async Task A_failing_handler_leaves_nothing_of_its_command_and_the_batch_goes_on()
    {
        using var tablet = await host.TabletAsync(host.A);
        var first = tablet.Command(1, new { n = 1 });
        var broken = tablet.Command(2, new { n = 2 }, type: TestCommandHandler.Broken.Name);
        var after = tablet.Command(3, new { n = 3 });

        var receipt = await UploadAsync(tablet, [first, broken, after]);

        receipt.Results.Should().BeEquivalentTo(new[]
        {
            Result(first, "accepted"), Result(broken, "rejected", "sync.handler_failed"), Result(after, "rejected", "sync.sequence_gap"),
        }, options => options.WithStrictOrdering());
        receipt.LastSequence.Should().Be(1);
        (await HandledAsync(tablet.Id)).Select(h => h.Command).Should().Equal(Id(first));
        (await InboxAsync(tablet.Id)).Select(r => r.Sequence).Should().Equal(1);
    }

    [Fact]
    [Trait("Story", "US-P2-OFF-03")]
    public async Task A_handler_that_refuses_a_command_leaves_nothing_of_it()
    {
        using var tablet = await host.TabletAsync(host.A);
        var refused = tablet.Command(1, new { n = 1 }, type: TestCommandHandler.Refused.Name);

        var receipt = await UploadAsync(tablet, [refused]);

        receipt.Results.Should().BeEquivalentTo([Result(refused, "rejected", TestCommandHandler.Refused.Code)]);
        (await HandledAsync(tablet.Id)).Should().BeEmpty();
        (await InboxAsync(tablet.Id)).Should().BeEmpty();
    }

    [Fact]
    [Trait("Story", "US-P2-OFF-03")]
    [Trait("TestCase", "TC-P2-SYNC-013")]
    public async Task A_command_of_a_type_no_module_handles_is_kept_and_holds_its_number()
    {
        // ADR-0031 §5: a newer app talking to an older server loses nothing; it sends the command again after the update.
        using var tablet = await host.TabletAsync(host.A);
        var unknown = tablet.Command(1, new { n = 1 }, type: "test.item_from_the_future.v1");
        var after = tablet.Command(2, new { n = 2 });

        var receipt = await UploadAsync(tablet, [unknown, after]);
        var again = await UploadAsync(tablet, [unknown]);

        receipt.Results.Should().BeEquivalentTo(new[]
        {
            Result(unknown, "rejected", "sync.type_unknown"), Result(after, "rejected", "sync.sequence_gap"),
        }, options => options.WithStrictOrdering());
        again.Results.Should().BeEquivalentTo([Result(unknown, "rejected", "sync.type_unknown")]);
        (await UnhandledAsync(tablet.Id)).Should().Equal((Id(unknown), Unsigned(unknown), "sync.type_unknown"));
        (await InboxAsync(tablet.Id)).Should().BeEmpty();
    }

    private async Task<List<(Guid Command, Guid Site)>> HandledAsync(Guid deviceId) =>
        await RowsAsync($"SELECT h.command_id, h.site_id FROM {TestCommandHandler.Table} h JOIN sync.inbox i ON i.id = h.command_id AND i.device_id = h.device_id WHERE h.device_id = $1 ORDER BY i.sequence",
            deviceId, r => (r.GetGuid(0), r.GetGuid(1)));

    private async Task<List<(Guid Command, string Outcome, string Flags)>> ProcessedAsync(Guid deviceId) =>
        await RowsAsync("SELECT p.command_id, p.outcome, array_to_string(p.flags, ',') FROM sync.processed_commands p JOIN sync.inbox i ON i.id = p.command_id AND i.device_id = p.device_id WHERE p.device_id = $1 ORDER BY i.sequence",
            deviceId, r => (r.GetGuid(0), r.GetString(1), r.GetString(2)));

    private async Task<List<(Guid Command, string Body, string Reason)>> UnhandledAsync(Guid deviceId) =>
        await RowsAsync("SELECT id, body, reason FROM sync.unhandled_commands WHERE device_id = $1 ORDER BY sequence",
            deviceId, r => (r.GetGuid(0), r.GetString(1), r.GetString(2)));

    private async Task<List<T>> RowsAsync<T>(string sql, Guid deviceId, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(sql, connection);
        query.Parameters.AddWithValue(deviceId);
        await using var reader = await query.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private sealed record Receipt(IReadOnlyList<CommandResult> Results, long LastSequence, DateTimeOffset ServerTime);

    private sealed record CommandResult(Guid CommandId, string Status, string? Code, bool Retryable, IReadOnlyList<string> Flags);

    private sealed record InboxRow(Guid Id, long Sequence, string Type, string Body, string Signature, string Status);

    /// <summary>The receipt of a command; a rejection is retryable when sending it again later can succeed.</summary>
    private static CommandResult Result(JsonObject command, string status, string? code = null) =>
        new(Id(command), status, code, code is "sync.sequence_gap" or "sync.type_unknown" or "sync.handler_failed", []);

    private static Guid Id(JsonObject command) => Guid.Parse(command["commandId"]!.GetValue<string>());

    /// <summary>The text the tablet signed: RFC 8785 of the command without its signature.</summary>
    private static string Unsigned(JsonObject command)
    {
        var copy = command.DeepClone().AsObject();
        copy.Remove("signature");
        return Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(copy.ToJsonString()));
    }

    private static Task<HttpResponseMessage> Upload(TestTablet tablet, JsonObject[] commands) =>
        tablet.SendAsync(HttpMethod.Post, Batches, new { commands });

    private static async Task<Receipt> UploadAsync(TestTablet tablet, JsonObject[] commands)
    {
        var response = await Upload(tablet, commands);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Receipt>())!;
    }

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
    }

    private async Task<List<InboxRow>> InboxAsync(Guid deviceId)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT id, sequence, type, body, signature, status FROM sync.inbox WHERE device_id = $1 ORDER BY sequence, status", connection);
        query.Parameters.AddWithValue(deviceId);
        await using var reader = await query.ExecuteReaderAsync();
        var rows = new List<InboxRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new InboxRow(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>Runs <paramref name="sql"/> (<c>$1</c> = the device) as the application role working for <paramref name="tenant"/>.</summary>
    private async Task<object?> AsTenantAsync(Guid tenant, string sql, Guid deviceId)
    {
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
        {
            set.Parameters.AddWithValue(tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(deviceId);
        return await command.ExecuteScalarAsync();
    }
}
