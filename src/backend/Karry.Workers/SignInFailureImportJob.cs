using Karry.Modules.Identity.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>Copies Keycloak's failed sign-ins and lockouts to the tenants' ledgers every minute (US-P1-IAM-01 AC4).</summary>
[DisallowConcurrentExecution]
internal sealed class SignInFailureImportJob(ImportSignInFailures import) : IJob
{
    public const string Schedule = "0 * * * * ?"; // every minute, plus once at start-up

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await import.RunAsync(cancellationToken).ConfigureAwait(false);
}
