using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Tools.LedgerVerifier;

/// <summary>
/// <c>karry-ledger verify --tenant &lt;uuid&gt; [--connection &lt;connection string&gt;]</c> (US-P1-AUD-03 AC1). The connection
/// defaults to the <c>KARRY_LEDGER_CONNECTION</c> environment variable (a read-only role is enough; row-level security
/// scopes the reads to the tenant). Exit codes: 0 = chain intact, 1 = broken (first broken sequence printed),
/// 2 = usage or configuration error.
/// </summary>
public static class LedgerVerifierCli
{
    public const string ConnectionVariable = "KARRY_LEDGER_CONNECTION";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args is not ["verify", .. var options] || !TryParse(options, out var tenant, out var connection))
        {
            await output.WriteLineAsync("usage: karry-ledger verify --tenant <uuid> [--connection <connection string>]").ConfigureAwait(false);
            return 2;
        }

        connection ??= Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            await output.WriteLineAsync($"no database: pass --connection or set {ConnectionVariable}").ConfigureAwait(false);
            return 2;
        }

        await using var services = new ServiceCollection().AddLogging().AddTrustInfrastructure(connection).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        var report = await scope.ServiceProvider.GetRequiredService<VerifyLedgerHandler>().HandleAsync(cancellationToken).ConfigureAwait(false);

        if (report.Valid)
        {
            await output.WriteLineAsync($"OK tenant {tenant}: {report.EntriesChecked} entries, head {report.HeadHash ?? "(empty)"}").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"BROKEN tenant {tenant}: first broken sequence {report.FirstBrokenSequence} ({report.Problem}); {report.EntriesChecked} entries verified before it")
            .ConfigureAwait(false);
        return 1;
    }

    private static bool TryParse(string[] options, out TenantId tenant, out string? connection)
    {
        tenant = default;
        connection = null;
        for (var i = 0; i + 1 < options.Length; i += 2)
        {
            switch (options[i])
            {
                case "--tenant" when Guid.TryParseExact(options[i + 1], "D", out var id) && id != Guid.Empty:
                    tenant = new TenantId(id);
                    break;
                case "--connection":
                    connection = options[i + 1];
                    break;
                default:
                    return false;
            }
        }

        return options.Length % 2 == 0 && tenant.Value != Guid.Empty;
    }
}
