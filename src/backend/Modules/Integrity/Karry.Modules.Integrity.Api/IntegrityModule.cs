using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Integrity.Application;
using Karry.Modules.Integrity.Domain;
using Karry.Modules.Integrity.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Integrity.Api;

/// <summary>
/// Integrity (ADR-0036): the flags the rules of the other modules raise, opened and closed over time, never deleted.
/// Routes under <c>/api/v1/integrity</c>.
/// </summary>
public sealed class IntegrityModule : IModule
{
    public string Name => "integrity";

    public IReadOnlyList<PermissionDefinition> Permissions => IntegrityPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddIntegrityInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/flags", SearchFlagsAsync)
            .RequirePermission(IntegrityPermissions.FlagsRead)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SearchIntegrityFlags");

        // Everyone who reads flags may see the thresholds that raised them.
        endpoints.MapGet("/rules", ListRulesAsync)
            .RequirePermission(IntegrityPermissions.FlagsRead)
            .WithName("ListIntegrityRules");

        endpoints.MapPut("/rules/{rule}", SetRuleAsync)
            .RequirePermission(IntegrityPermissions.RulesManage)
            .RequireRecentSignIn()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SetIntegrityRule");
    }

    /// <summary>Every rule of the catalogue as the company runs it: switched on, thresholds with their range and unit.</summary>
    private static async Task<Ok<RuleListResponse>> ListRulesAsync([FromServices] RuleSettingOperations rules, CancellationToken cancellationToken) =>
        TypedResults.Ok(new RuleListResponse([.. (await rules.ListAsync(cancellationToken).ConfigureAwait(false)).Select(RuleResponse.From)]));

    /// <summary>
    /// Switches a rule on or off and sets its thresholds (a threshold not named keeps its value). A security rule cannot
    /// be switched off. <c>If-Match</c>: the rule's <c>version</c> the form was filled from (<c>"0"</c>: the defaults); a
    /// newer one is a 409 (H-C1).
    /// </summary>
    private static async Task<Results<Ok<RuleResponse>, ProblemHttpResult>> SetRuleAsync(
        string rule, RuleSettingsInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] RuleSettingOperations rules, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        if (!expected.IsSuccess)
        {
            return expected.Error.ToProblem();
        }

        var result = await rules.SetAsync(rule, request, expected.Value, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(RuleResponse.From(result.Value));
    }

    /// <summary>
    /// Flags of the caller's sites, newest first: <c>status</c> open (default), closed or all; by site, rule or subject;
    /// <c>cursor</c> is the last flag ID of the previous page.
    /// </summary>
    private static async Task<Results<Ok<FlagListResponse>, ProblemHttpResult>> SearchFlagsAsync(
        Guid? siteId, string? rule, string? status, string? subjectType, Guid? subjectId, Guid? cursor, int? limit,
        [FromServices] FlagReading flags, CancellationToken cancellationToken)
    {
        var result = await flags.SearchAsync(siteId, rule, status, subjectType, subjectId, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new FlagListResponse([.. result.Value.Items.Select(FlagResponse.From)], result.Value.NextCursor))
            : result.Error.ToProblem();
    }
}

/// <summary>The rules of the catalogue, in its order.</summary>
public sealed record RuleListResponse(IReadOnlyList<RuleResponse> Items);

/// <summary>A threshold of a rule: the company's value, the catalogue's default, the range it may take and its unit.</summary>
public sealed record ThresholdResponse(string Name, decimal Value, decimal Default, decimal Min, decimal Max, int Decimals, string Unit);

/// <summary>
/// A rule as the company runs it (ADR-0036 §1): <c>security</c> rules are always on; <c>changedAt</c> null while the
/// company runs the catalogue's defaults; <c>version</c> for <c>If-Match</c>.
/// </summary>
public sealed record RuleResponse(
    string Rule, string Severity, bool Security, bool Enabled, IReadOnlyList<ThresholdResponse> Thresholds, DateTimeOffset? ChangedAt, string? ChangedBy, string Version)
{
    public static RuleResponse From(EffectiveRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var values = rule.Thresholds;
        return new(
            rule.Definition.Code, rule.Definition.Severity, rule.Definition.Security, rule.Enabled,
            [.. rule.Definition.Thresholds.Select(t => new ThresholdResponse(t.Name, values[t.Name], t.Default, t.Min, t.Max, t.Decimals, t.Unit))],
            rule.Setting?.ChangedAt, rule.Setting?.ChangedBy, RecordVersionHttp.ETag(rule.Version));
    }
}

/// <summary>A page of flags, newest first; <c>nextCursor</c> when more follow.</summary>
public sealed record FlagListResponse(IReadOnlyList<FlagResponse> Items, Guid? NextCursor);

/// <summary>An entity a flag is about or linked to.</summary>
public sealed record FlagSubjectResponse(string Type, Guid Id);

/// <summary>
/// A flag (ADR-0036 §2): the rule and its severity, what it is about and linked to, the evidence and the values its
/// explanation names; <c>closedAt</c> and <c>closeReason</c> once it no longer holds or was settled.
/// </summary>
public sealed record FlagResponse(
    Guid Id, string Rule, string Severity, FlagSubjectResponse Subject, string Key, Guid? SiteId, IReadOnlyList<FlagSubjectResponse> Related,
    IReadOnlyList<string> Evidence, IReadOnlyDictionary<string, string> Details, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? CloseReason)
{
    public static FlagResponse From(IntegrityFlag f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new(f.Id.Value, f.Rule, f.Severity, new(f.SubjectType, f.SubjectId), f.Key, f.SiteId, [.. f.Related.Select(r => new FlagSubjectResponse(r.Type, r.Id))],
            f.Evidence, f.Details, f.OpenedAt, f.ClosedAt, f.CloseReason);
    }
}
