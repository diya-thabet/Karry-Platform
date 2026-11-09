using Karry.BuildingBlocks.Sync;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// Handlers of the test command types (ADR-0031): each writes a row of <c>public.test_handled</c> in the upload's
/// transaction, then answers as its type says, so the tests see what was kept and what was rolled back.
/// </summary>
internal abstract class TestCommandHandler : ISyncCommandHandler
{
    public const string Table = "public.test_handled";

    public const string CreateTable = $"""
        CREATE TABLE {Table} (device_id uuid NOT NULL, command_id uuid NOT NULL, type text NOT NULL, site_id uuid NOT NULL, PRIMARY KEY (device_id, command_id));
        GRANT SELECT, INSERT ON {Table} TO karry_app;
        """;

    public abstract string Type { get; }

    public static void AddTo(IServiceCollection services)
    {
        services.AddSyncCommandHandler<Recorded>(Recorded.Name);
        services.AddSyncCommandHandler<Flagged>(Flagged.Name);
        services.AddSyncCommandHandler<Broken>(Broken.Name);
        services.AddSyncCommandHandler<Refused>(Refused.Name);
    }

    public async Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"INSERT INTO {Table} (device_id, command_id, type, site_id) VALUES ($1, $2, $3, $4)",
            (NpgsqlConnection)transaction.Connection, (NpgsqlTransaction)transaction.Transaction);
        insert.Parameters.AddWithValue(command.DeviceId);
        insert.Parameters.AddWithValue(command.CommandId);
        insert.Parameters.AddWithValue(command.Type);
        insert.Parameters.AddWithValue(command.SiteId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return Answer(command);
    }

    protected abstract HandlerOutcome Answer(VerifiedCommand command);

    /// <summary>The type every earlier sync test sends: stored, nothing to say.</summary>
    internal sealed class Recorded : TestCommandHandler
    {
        public const string Name = "test.item_recorded.v1";

        public override string Type => Name;

        protected override HandlerOutcome Answer(VerifiedCommand command) => new HandlerOutcome.Accepted(command.CommandId);
    }

    internal sealed class Flagged : TestCommandHandler
    {
        public const string Name = "test.item_flagged.v1";

        public const string Flag = "test.needs_a_look";

        public override string Type => Name;

        protected override HandlerOutcome Answer(VerifiedCommand command) => new HandlerOutcome.Conflict(command.CommandId, [Flag]);
    }

    /// <summary>Fails after writing: a bug in a handler.</summary>
    internal sealed class Broken : TestCommandHandler
    {
        public const string Name = "test.item_broken.v1";

        public override string Type => Name;

        protected override HandlerOutcome Answer(VerifiedCommand command) => throw new InvalidOperationException("A handler bug.");
    }

    /// <summary>Refuses after writing: a payload that does not fit the type.</summary>
    internal sealed class Refused : TestCommandHandler
    {
        public const string Name = "test.item_refused.v1";

        public const string Code = "test.payload_invalid";

        public override string Type => Name;

        protected override HandlerOutcome Answer(VerifiedCommand command) => new HandlerOutcome.Rejected(Code, Retryable: false);
    }
}
