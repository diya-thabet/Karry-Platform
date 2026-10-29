using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Tenancy.Tests;

/// <summary>
/// How the operator console gathers each tenant (US-P1-OPS-03 AC1, ADR-0029 §3): every source answers in a scope of
/// that tenant only, parts are merged, and a failing tenant is marked incomplete without hiding the others.
/// </summary>
[Trait("Story", "US-P1-OPS-03")]
public sealed class TenantOverviewTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero));

    private readonly Tenant north = Tenant.Provision(Clock, "carriere-nord", "Carrière Nord").Value;

    private readonly Tenant south = Tenant.Provision(Clock, "carriere-sud", "Carrière Sud").Value;

    public TenantOverviewTests() => north.ChangeSettings(north.Settings with { Currency = "TND" }, actor: null, Clock.GetUtcNow());

    [Fact]
    public async Task Each_tenant_is_summarized_under_its_own_context_with_every_part_merged()
    {
        var rows = await Overview(failFor: null).ListAsync(TestContext.Current.CancellationToken);

        rows.Select(r => r.Tenant).Should().Equal(north, south);
        var row = rows[0];
        row.Complete.Should().BeTrue();
        row.Usage.Should().BeEquivalentTo(new Dictionary<string, long> { [Metrics.Sites] = Seen(north), [Metrics.Assets] = 7 },
            "only the counted metrics, each asked under the tenant");
        row.Summary.PlanCode.Should().Be($"plan-{north.Id.Value}");
        row.Summary.Mrr.Should().Be(590m);
        row.Summary.MrrCurrency.Should().Be("TND", "the source is given the company's currency");
        row.Summary.LastActivity.Should().Be(Clock.GetUtcNow());
        rows[1].Summary.PlanCode.Should().Be($"plan-{south.Id.Value}");
    }

    [Fact]
    public async Task A_tenant_whose_module_fails_is_listed_as_incomplete_and_the_others_still_answer()
    {
        var rows = await Overview(failFor: north).ListAsync(TestContext.Current.CancellationToken);

        rows[0].Complete.Should().BeFalse();
        rows[0].Usage.Should().BeEmpty();
        rows[0].Summary.Should().Be(new TenantSummaryPart());
        rows[1].Complete.Should().BeTrue();
        rows[1].Summary.Mrr.Should().Be(590m);
    }

    [Fact]
    public void A_part_fills_only_the_gaps_of_the_other()
    {
        var billing = new TenantSummaryPart(PlanCode: "professional", Mrr: 590m, MrrCurrency: "EUR");
        var trust = new TenantSummaryPart(PlanCode: "ignored", LastActivity: Clock.GetUtcNow());

        billing.With(trust).Should().Be(new TenantSummaryPart(PlanCode: "professional", Mrr: 590m, MrrCurrency: "EUR", LastActivity: Clock.GetUtcNow()));
    }

    /// <summary>A number that depends on the tenant, so a count asked under the wrong tenant shows.</summary>
    private static long Seen(Tenant tenant) => tenant.Id.Value.GetHashCode() & 0xFFFF;

    private TenantOverview Overview(Tenant? failFor)
    {
        var services = new ServiceCollection()
            .AddScoped<ITenantContext, TenantContext>()
            .AddScoped<IUsageSource>(sp => new Count(Metrics.Sites, () => Seen(sp.GetRequiredService<ITenantContext>().Current!.Value == north.Id ? north : south)))
            .AddScoped<IUsageSource>(_ => new Count(Metrics.Assets, () => 7))
            .AddScoped<IUsageSource>(_ => new Count(Metrics.StorageBytes, () => throw new InvalidOperationException("not a column of the list")))
            .AddScoped<ITenantSummarySource>(sp => new Part(sp.GetRequiredService<ITenantContext>(), failFor,
                request => new TenantSummaryPart(PlanCode: $"plan-{request.TenantId}", Mrr: 590m, MrrCurrency: request.Currency)))
            .AddScoped<ITenantSummarySource>(sp => new Part(sp.GetRequiredService<ITenantContext>(), null, _ => new TenantSummaryPart(LastActivity: Clock.GetUtcNow())))
            .BuildServiceProvider();
        return new TenantOverview(new Directory([north, south]), services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TenantOverview>.Instance);
    }

    private sealed class Directory(IReadOnlyList<Tenant> tenants) : ITenantRepository
    {
        public Task<Tenant?> FindBySlugAsync(TenantSlug slug, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(tenants);

        public Task<bool> TryAddAsync(Tenant tenant, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Count(string metric, Func<long> count) : IUsageSource
    {
        public string Metric => metric;

        public Task<long> CountAsync(CancellationToken cancellationToken) => Task.FromResult(count());
    }

    private sealed class Part(ITenantContext tenant, Tenant? failFor, Func<TenantSummaryRequest, TenantSummaryPart> answer) : ITenantSummarySource
    {
        public Task<TenantSummaryPart> SummarizeAsync(TenantSummaryRequest request, CancellationToken cancellationToken)
        {
            tenant.Current!.Value.Value.Should().Be(request.TenantId, "a source answers only under the tenant it is asked about");
            return failFor is not null && tenant.Current == failFor.Id
                ? throw new InvalidOperationException("module down")
                : Task.FromResult(answer(request));
        }
    }
}
