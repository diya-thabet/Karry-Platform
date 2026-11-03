using Karry.BuildingBlocks.Storage;
using Npgsql;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// Which failures are a dependency down for a moment rather than a fault (hardening plan H-R1, H-R4): the API answers
/// them 503 <see cref="ProblemTypes.UnavailableCode"/> with <c>Retry-After</c>, and logs them as warnings.
/// </summary>
public static class Outages
{
    /// <summary>What <c>Retry-After</c> asks of the client: about the time a database fail-over or a storage restart takes.</summary>
    public const int RetryAfterSeconds = 30;

    /// <summary>The dependency that is down (for the log), or null when the exception is a fault.</summary>
    public static string? Describe(Exception? exception)
    {
        // EF Core and the modules wrap the driver's exception: look through the whole chain.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ObjectStoreUnavailableException:
                    return "Object storage";
                case NpgsqlException { IsTransient: true }:
                    return "PostgreSQL";
            }
        }

        return null;
    }
}
