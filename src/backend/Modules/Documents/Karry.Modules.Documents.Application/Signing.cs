using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Documents.Application;

/// <summary>What a signing run did: envelopes signed (<c>Done</c>); whether the signing service was there; companies that failed.</summary>
public sealed record SigningRun(int Done, bool Unavailable, int Failed);

/// <summary>
/// Signs the issued envelopes with the server key (ADR-0040 §3, ADR-0033 §3), company by company, as countersigning does:
/// a document is valid from its ledger entry on, and gains its signature when the signing service answers. When it does
/// not, the run stops and the next one tries again.
/// </summary>
public sealed partial class SignDocuments(IServiceScopeFactory scopes, IDigestSigner signer, TimeProvider clock, ILogger<SignDocuments> logger)
{
    public const int BatchSize = 100;

    public async Task<SigningRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> companies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var (done, failed) = (0, 0);
        foreach (var company in companies)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company);
                var store = scope.ServiceProvider.GetRequiredService<IDocumentEnvelopeStore>();
                foreach (var envelope in await store.UnsignedAsync(BatchSize, cancellationToken).ConfigureAwait(false))
                {
                    if (await signer.TrySignAsync(envelope.SignedDigest(), cancellationToken).ConfigureAwait(false) is not { } signature)
                    {
                        return new SigningRun(done, Unavailable: true, failed);
                    }

                    envelope.Sign(clock, signature.KeyId, signature.Signature);
                    await store.SaveAsync(cancellationToken).ConfigureAwait(false);
                    done++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, company.Value);
            }
        }

        return new SigningRun(done, Unavailable: false, failed);
    }

    [LoggerMessage(EventId = 7701, Level = LogLevel.Error, Message = "Signing the documents of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
