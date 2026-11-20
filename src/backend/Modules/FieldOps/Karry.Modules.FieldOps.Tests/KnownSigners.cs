using Karry.Modules.Identity.Contracts;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>Identity's field signers (RULE-FO-09): everyone may sign, except the people refused here.</summary>
public sealed class KnownSigners : IFieldSigners
{
    public Dictionary<Guid, FieldSignerRefusal> Refused { get; } = [];

    public Task<FieldSignerRefusal?> RefusalAsync(Guid userId, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Refused.GetValueOrDefault(userId));
}
