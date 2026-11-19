using System.Data.Common;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.Integrity.Contracts;

namespace Karry.Modules.FieldOps.Application;

/// <summary>A meter event was recorded in Assets: the meter's continuity is evaluated again (ADR-0036 §4, TC-P2-FO-009).</summary>
public sealed class MeterEventContinuity(IShiftEntryStore store, IMeterEvents events, IMeterDirectory meters, IIntegrityFlags integrity) : IMeterEventSubscriber
{
    public Task HandleAsync(MeterEventSummary meterEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
        IntegrityFindings.SyncContinuityAfterEventAsync(store, events, meters, integrity, meterEvent, connection, transaction, cancellationToken);
}
