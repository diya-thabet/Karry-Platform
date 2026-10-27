using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Application;
using Karry.Modules.Identity.Application;
using Karry.Modules.Materials.Application;
using Karry.Modules.Organization.Application;
using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;

namespace Karry.Host;

/// <summary>
/// Demo data for local and demo environments: two tenants used by the isolation suite and demos, each with one user
/// per default role when Keycloak is configured (<c>Identity:Keycloak:BaseUrl</c>; password <c>Identity:DemoPassword</c>).
/// A demo tenant without a subscription (seeded before Billing existed) gets its trial here, the same way provisioning
/// starts it. Demo companies get settings (legal name, colour, currency, time zone) while they still have the defaults,
/// sites with shifts and public holidays while they have none (the sites bought are raised to match), and materials
/// with their densities while they have none.
/// </summary>
internal static class DemoSeed
{
    public static readonly ProvisionTenant[] Tenants =
    [
        new("demo-carriere-a", "Carrière Démo A"),
        new("demo-carriere-b", "Carrière Démo B"),
    ];

    /// <summary>Demo company settings, applied while a demo tenant still has the defaults (a change made in the portal stays).</summary>
    private static readonly Dictionary<string, UpdateTenantSettings> Settings = new(StringComparer.Ordinal)
    {
        ["demo-carriere-a"] = new("Carrière Démo A SARL", "#1F4E79", "fr", "TND", "metric", "Africa/Tunis", 1),
        ["demo-carriere-b"] = new("Carrière Démo B SA", "#8A3B12", "fr", "XOF", "metric", "Africa/Conakry", 1),
    };

    private static readonly string[] Week = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly ShiftPatternInput TwoTwelves = new(
    [
        new ShiftInput("JOUR", "Jour", "06:00", "18:00", Week),
        new ShiftInput("NUIT", "Nuit", "18:00", "06:00", Week),
    ]);

    /// <summary>3 × 8 h from Monday to Saturday: the plant stops on Sundays.</summary>
    private static readonly ShiftPatternInput ThreeEights = new(
    [
        new ShiftInput("M", "Matin", "06:00", "14:00", Week[..6]),
        new ShiftInput("A", "Après-midi", "14:00", "22:00", Week[..6]),
        new ShiftInput("N", "Nuit", "22:00", "06:00", Week[..6]),
    ]);

    private static readonly DemoHoliday[] Tunisia =
    [
        new(1, 1, "Jour de l’an"), new(3, 20, "Fête de l’indépendance"), new(4, 9, "Journée des martyrs"), new(5, 1, "Fête du travail"),
        new(7, 25, "Fête de la République"), new(8, 13, "Fête de la femme"), new(10, 15, "Fête de l’évacuation"), new(12, 17, "Fête de la révolution"),
    ];

    private static readonly DemoHoliday[] Guinea =
    [
        new(1, 1, "Jour de l’an"), new(5, 1, "Fête du travail"), new(5, 25, "Journée de l’Afrique"), new(8, 15, "Assomption"),
        new(10, 2, "Fête de l’indépendance"), new(11, 1, "Toussaint"), new(12, 25, "Noël"),
    ];

    /// <summary>
    /// Demo sites (US-P1-ORG-01/02). Company A: a quarry on 2 × 12 h with its boundary and a sand-washing plant on 3 × 8 h;
    /// company B: one quarry. Holidays are fixed-date ones only (movable feasts are left to the company). Locations are
    /// illustrative.
    /// </summary>
    private static readonly Dictionary<string, DemoSite[]> Sites = new(StringComparer.Ordinal)
    {
        ["demo-carriere-a"] =
        [
            new(new SiteInput("CAR-NORD", "Carrière Nord", "quarry", new AddressInput("Route de Mornag km 14", "Mornag", "Ben Arous", "2090", "TN"),
                new LocationInput(36.609, 10.32),
                [[[10.315, 36.605], [10.325, 36.605], [10.325, 36.613], [10.315, 36.613], [10.315, 36.605]]], "Africa/Tunis", null), TwoTwelves, Tunisia),
            new(new SiteInput("LAV-SUD", "Laverie de sable Sud", "sand-washing", new AddressInput("Zone industrielle", "Zaghouan", "Zaghouan", "1100", "TN"),
                new LocationInput(36.402, 10.143), null, "Africa/Tunis", null), ThreeEights, Tunisia),
        ],
        ["demo-carriere-b"] =
        [
            new(new SiteInput("CAR-KAK", "Carrière de Kakoulima", "quarry", new AddressInput("Route de Dubréka", "Dubréka", "Kindia", null, "GN"),
                new LocationInput(9.79, -13.43), null, "Africa/Conakry", null), TwoTwelves, Guinea),
        ],
    };

    /// <summary>
    /// Demo materials (US-P1-AST-04) with densities from 1 January. Company A: limestone and the products of its quarry and
    /// sand-washing plant; company B: granite. Values are typical ones within the formula handbook's ranges (draft until
    /// the domain expert signs it off), not measurements.
    /// </summary>
    private static readonly Dictionary<string, MaterialInput[]> Materials = new(StringComparer.Ordinal)
    {
        ["demo-carriere-a"] =
        [
            new("CAL-BRUT", "Calcaire abattu", "raw-rock", new DensityInput(2.56m, 1.60m, null, 0.02m), null),
            new("0/31.5", "Grave non traitée 0/31.5", "product", new DensityInput(null, 1.65m, 2.20m, 0.04m), null),
            new("G-4/6", "Gravillon 4/6", "product", new DensityInput(null, 1.50m, null, 0.01m), null),
            new("S-0/4", "Sable lavé 0/4", "product", new DensityInput(null, 1.55m, 1.80m, 0.08m), null),
        ],
        ["demo-carriere-b"] =
        [
            new("GRA-BRUT", "Granite abattu", "raw-rock", new DensityInput(2.65m, 1.70m, null, 0.01m), null),
            new("0/31.5", "Grave 0/31.5", "product", new DensityInput(null, 1.70m, 2.25m, 0.03m), null),
        ],
    };

    /// <summary>Local-only default (meets the realm's 12-character policy); never used outside seed-demo.</summary>
    public const string DemoPassword = "karry-demo-local-only";

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        foreach (var tenant in Tenants)
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ProvisionTenantHandler>()
                .HandleAsync(tenant, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Demo tenant {tenant.Slug}: {result.Error.Code}");
            }

            await using (var settings = services.CreateAsyncScope())
            {
                var id = new TenantId(result.Value.Id);
                settings.ServiceProvider.GetRequiredService<ITenantContext>().Set(id);
                var current = await settings.ServiceProvider.GetRequiredService<ITenantSettingsStore>().FindAsync(id, cancellationToken).ConfigureAwait(false);
                if (current is { SettingsChangedAt: null })
                {
                    var applied = await settings.ServiceProvider.GetRequiredService<TenantSettingsOperations>()
                        .UpdateAsync(id, Settings[tenant.Slug], actor: "seed-demo", cancellationToken).ConfigureAwait(false);
                    if (!applied.IsSuccess)
                    {
                        throw new InvalidOperationException($"Demo tenant {tenant.Slug} settings: {applied.Error.Code}");
                    }
                }
            }

            await using (var billing = services.CreateAsyncScope())
            {
                var id = new TenantId(result.Value.Id);
                billing.ServiceProvider.GetRequiredService<ITenantContext>().Set(id);
                await billing.ServiceProvider.GetServices<ITenantProvisionedHandler>().OfType<TrialOnProvisioning>().Single()
                    .HandleAsync(new TenantSummary(id, tenant.Slug, tenant.Name), cancellationToken).ConfigureAwait(false);

                // The demo sites must be bought, as a customer would (ADR-0024: the sites meter counts against them).
                var subscriptions = billing.ServiceProvider.GetRequiredService<SubscriptionOperations>();
                var terms = (await subscriptions.CurrentAsync(cancellationToken).ConfigureAwait(false))!.Terms;
                if (terms.Sites < Sites[tenant.Slug].Length)
                {
                    var bought = await subscriptions.ChangeAsync(
                        new ChangeSubscription(terms.PlanCode, terms.PlanVersion, PlanPriceDto.PeriodName(terms.Period), Sites[tenant.Slug].Length, terms.ExtraAssets,
                            terms.AddOns, terms.ExtraFeatures, terms.LimitOverrides),
                        actor: "seed-demo", cancellationToken).ConfigureAwait(false);
                    if (!bought.IsSuccess)
                    {
                        throw new InvalidOperationException($"Demo tenant {tenant.Slug} sites: {bought.Error.Code}");
                    }
                }
            }

            await using (var organization = services.CreateAsyncScope())
            {
                var seeded = await organization.ServiceProvider.GetRequiredService<DemoSitesSeeder>()
                    .SeedAsync(new TenantId(result.Value.Id), Sites[tenant.Slug], cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"{tenant.Slug}: {seeded} demo site(s) created");
            }

            await using (var catalogue = services.CreateAsyncScope())
            {
                var seeded = await catalogue.ServiceProvider.GetRequiredService<DemoMaterialsSeeder>()
                    .SeedAsync(new TenantId(result.Value.Id), Materials[tenant.Slug], cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"{tenant.Slug}: {seeded} demo material(s) created");
            }

            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (string.IsNullOrWhiteSpace(configuration["Identity:Keycloak:BaseUrl"]))
            {
                Console.WriteLine($"{tenant.Slug}: Identity:Keycloak:BaseUrl not set, no demo users");
                continue;
            }

            await using var users = services.CreateAsyncScope();
            var created = await users.ServiceProvider.GetRequiredService<DemoUsersSeeder>().SeedAsync(
                new TenantId(result.Value.Id), configuration["Identity:DemoPassword"] ?? DemoPassword, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"{tenant.Slug}: {created} demo user(s) created");
        }
    }
}
