using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfVerificationRecords(TrustDbContext db, ITenantContext tenant) : IVerificationRecords
{
    private const int ProblemLength = 1000;

    public async Task RecordAsync(RecordedVerification verification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var report = verification.Report;
        db.Verifications.Add(new VerificationRow
        {
            Id = Guid.CreateVersion7(report.VerifiedAt),
            TenantId = tenant.Current ?? throw new InvalidOperationException("An integrity check runs for a tenant."),
            VerifiedAt = report.VerifiedAt,
            VerifiedBy = verification.VerifiedBy,
            Valid = report.Valid,
            EntriesChecked = report.EntriesChecked,
            FirstBrokenSequence = report.FirstBrokenSequence,
            Problem = report.Problem is { Length: > ProblemLength } text ? text[..ProblemLength] : report.Problem,
            HeadHash = report.HeadHash,
            AnchorsChecked = report.AnchorsChecked,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordedVerification?> LatestAsync(CancellationToken cancellationToken)
    {
        var row = await db.Verifications.AsNoTracking().OrderByDescending(v => v.VerifiedAt).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null
            ? null
            : new RecordedVerification(
                new LedgerVerificationReport(row.Valid, row.EntriesChecked, row.FirstBrokenSequence, row.Problem, row.HeadHash, row.AnchorsChecked, row.VerifiedAt),
                row.VerifiedBy);
    }
}
