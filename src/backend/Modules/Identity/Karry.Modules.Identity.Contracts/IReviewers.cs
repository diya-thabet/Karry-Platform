namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// Who a reviewer is as a person (ADR-0037 §4): a controller signs in to the portal with an office account; when they
/// also drive, their field account (the one the tablets unlock) is linked to it by the company administrator. Separation
/// of duties compares records' producers with that field account.
/// </summary>
public interface IReviewers
{
    /// <summary>The field account linked to the office account of <paramref name="subject"/>, or null (none, or no such person).</summary>
    Task<Guid?> FieldAccountOfAsync(string subject, CancellationToken cancellationToken);
}
