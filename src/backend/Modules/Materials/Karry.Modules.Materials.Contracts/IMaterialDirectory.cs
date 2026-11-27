namespace Karry.Modules.Materials.Contracts;

/// <summary>
/// A material as other modules see it. <c>IsProduct</c>: a fraction that is sold (not raw rock); <c>Archived</c>: no
/// longer produced or sold.
/// </summary>
public sealed record MaterialSummary(Guid Id, string Code, string Name, bool IsProduct, bool Archived);

/// <summary>The current tenant's materials, for other modules (e.g. Sales products map to a fraction). Read in the caller's unit of work (RLS).</summary>
public interface IMaterialDirectory
{
    /// <summary>The material, or null when there is none in this company.</summary>
    Task<MaterialSummary?> FindAsync(Guid materialId, CancellationToken cancellationToken);
}
