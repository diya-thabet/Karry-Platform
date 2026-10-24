using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Application;

/// <summary>The import's paging and cursor rules on a fake event source (the live path is in IdentityApiTests).</summary>
[Trait("Story", "US-P1-IAM-01")]
public sealed class ImportSignInFailuresTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider clock = new(Start.AddHours(1));
    private readonly Store store = new();
    private readonly Cursor cursor = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task More_events_in_one_millisecond_than_a_page_are_all_imported_once_and_the_import_ends()
    {
        // 100 spread events, then 250 in one millisecond (more than a page), then 100 more.
        var events = Enumerable.Range(0, 100).Select(i => Event(Start.AddSeconds(i)))
            .Concat(Enumerable.Range(0, 250).Select(_ => Event(Start.AddMinutes(10))))
            .Concat(Enumerable.Range(0, 100).Select(i => Event(Start.AddMinutes(20).AddSeconds(i))))
            .ToList();
        var source = new Source(events);

        var run = await Import(source).RunAsync(Ct);

        store.Recorded.Should().BeEquivalentTo(events.Select(e => e.Id), "every failure in one run, none twice");
        run.Imported.Should().Be(events.Count);
        (await Import(source).RunAsync(Ct)).Imported.Should().Be(0);
        cursor.Position.Should().Be(events.Max(e => e.Time));
    }

    [Fact]
    public async Task Events_without_a_known_user_or_tenant_are_counted_not_recorded()
    {
        var source = new Source([Event(Start, subject: null), Event(Start.AddSeconds(1), subject: "no-tenant"), Event(Start.AddSeconds(2), subject: "unknown-user")]);

        var run = await Import(source).RunAsync(Ct);

        run.Should().Be(new SignInImportRun(Imported: 0, AlreadyImported: 0, Unattributed: 3));
        store.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_run_looks_back_thirty_days_and_later_runs_overlap_by_one_millisecond()
    {
        var source = new Source([Event(Start)]);

        await Import(source).RunAsync(Ct);
        await Import(source).RunAsync(Ct);

        source.Reads[0].Should().Be(clock.GetUtcNow() - ImportSignInFailures.FirstRunLookBack);
        source.Reads[^1].Should().Be(Start.AddMilliseconds(-1), "an event in the cursor's millisecond is never skipped");
    }

    private static KeycloakSignInEvent Event(DateTimeOffset time, string? subject = "known") =>
        new(Guid.NewGuid(), time, "invalid_user_credentials", subject, "10.0.0.1");

    private ImportSignInFailures Import(Source source)
    {
        var services = new ServiceCollection()
            .AddScoped<ITenantContext, TenantContext>()
            .AddSingleton<IIdentityStore>(store)
            .AddSingleton<IImportCursor>(cursor)
            .BuildServiceProvider();
        return new ImportSignInFailures(services.GetRequiredService<IServiceScopeFactory>(), source, clock);
    }

    private sealed class Source(List<KeycloakSignInEvent> events) : ISignInEventSource
    {
        public List<DateTimeOffset> Reads { get; } = [];

        public Task<IReadOnlyList<KeycloakSignInEvent>> ReadFailuresAsync(DateTimeOffset from, int first, int max, CancellationToken cancellationToken)
        {
            Reads.Add(from);
            return Task.FromResult<IReadOnlyList<KeycloakSignInEvent>>([.. events.Where(e => e.Time >= from).OrderBy(e => e.Time).Skip(first).Take(max)]);
        }

        public Task<TenantId?> TenantOfAsync(string subject, CancellationToken cancellationToken) =>
            Task.FromResult<TenantId?>(subject == "no-tenant" ? null : Tenant);
    }

    private sealed class Cursor : IImportCursor
    {
        public DateTimeOffset? Position { get; private set; }

        public Task<DateTimeOffset?> GetAsync(string source, CancellationToken cancellationToken) => Task.FromResult(Position);

        public Task SetAsync(string source, DateTimeOffset position, CancellationToken cancellationToken)
        {
            Position = Position is { } p && p > position ? p : position;
            return Task.CompletedTask;
        }
    }

    private sealed class Store : IIdentityStore
    {
        private readonly UserAccount user = UserAccount.Invite(TimeProvider.System, Tenant, "K", "U", "k@example.test", null, [new(Roles.Operator, null)], null).Value.User;

        public HashSet<Guid> Recorded { get; } = [];

        public Task<UserAccount?> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
            Task.FromResult(subject == "known" ? user : null);

        public Task<bool> AddSignInFailureAsync(SignInFailure failure, CancellationToken cancellationToken) =>
            Task.FromResult(Recorded.Add(failure.Id.Value));

        public Task<UserAccount?> FindUserAsync(UserId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasAdministratorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string? email, string? employeeId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<UserAccount>> AddAsync(UserAccount user, Invitation? invitation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<bool>> SaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
