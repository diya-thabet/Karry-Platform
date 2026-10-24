using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// Puts the tenant into PostgreSQL for row-level security (US-P1-TEN-02 AC2, ADR-0004, spike WP-P0-12). The setting is
/// always <c>SET LOCAL</c> (transaction-local, never session-level: TC-P0-RLS-005), so it cannot leak to the next user
/// of a pooled connection:
/// <list type="bullet">
/// <item><b>Command outside a transaction</b> (queries): prefixed with <c>SET LOCAL …;</c> in the same batch, which
/// PostgreSQL runs as one implicit transaction: no extra round trip.</item>
/// <item><b>Command inside a transaction</b> (every SaveChanges, explicit transactions): <c>SET LOCAL</c> runs once as
/// the transaction's own statement before its first command. Not prefixed, because EF Core reads rows-affected per
/// statement of a modification batch and an extra statement would shift them.</item>
/// </list>
/// The value is a <see cref="Guid"/> formatted by .NET ("D": hex digits and dashes), so it cannot carry SQL.
/// Without a tenant nothing is set: RLS then returns no rows and refuses writes (fail closed).
/// Npgsql reuses one transaction object for consecutive transactions on a connection, so "already set" is forgotten
/// whenever a transaction starts or is attached (P1.8 finding: the second save of a unit of work was refused).
/// </summary>
public sealed class TenantCommandInterceptor(ITenantContext tenant) : DbCommandInterceptor, IDbTransactionInterceptor
{
    public const string SettingName = "app.tenant_id";

    private readonly ConditionalWeakTable<DbTransaction, object> preparedTransactions = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result) => Forget(result);

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Forget(result));

    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result) => Forget(result);

    public ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Forget(result));

    /// <summary>The statement that sets the tenant for the current transaction.</summary>
    public static string SetLocalStatement(Guid tenantId) => $"SET LOCAL {SettingName} = '{tenantId:D}';\n";

    private void Apply(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (tenant.Current is not { } current)
        {
            return;
        }

        if (command.Transaction is null)
        {
            command.CommandText = SetLocalStatement(current.Value) + command.CommandText;
        }
        else if (MarkPrepared(command.Transaction))
        {
            using var set = SetCommand(command, current.Value);
            set.ExecuteNonQuery();
        }
    }

    private async Task ApplyAsync(DbCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (tenant.Current is not { } current)
        {
            return;
        }

        if (command.Transaction is null)
        {
            command.CommandText = SetLocalStatement(current.Value) + command.CommandText;
        }
        else if (MarkPrepared(command.Transaction))
        {
            await using var set = SetCommand(command, current.Value);
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A new transaction (possibly a recycled object) has no tenant yet.</summary>
    private DbTransaction Forget(DbTransaction transaction)
    {
        preparedTransactions.Remove(transaction);
        return transaction;
    }

    /// <summary>True the first time a transaction is seen (it then needs its SET LOCAL).</summary>
    private bool MarkPrepared(DbTransaction transaction) => preparedTransactions.TryAdd(transaction, this);

    private static DbCommand SetCommand(DbCommand command, Guid tenantId)
    {
        var set = command.Connection!.CreateCommand();
        set.Transaction = command.Transaction;
        set.CommandText = SetLocalStatement(tenantId);
        return set;
    }
}
