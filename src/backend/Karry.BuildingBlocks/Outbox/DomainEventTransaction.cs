using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>The connection and transaction of an outbox dispatch, for a contract of another module to join (ADR-0015).</summary>
public static class DomainEventTransaction
{
    public static (DbConnection Connection, DbTransaction Transaction) Of(DomainEventContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var transaction = context.Db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A domain event is dispatched in a transaction.");
        return (context.Db.Database.GetDbConnection(), transaction);
    }
}
