using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Integrity.Application;
using Karry.Modules.Integrity.Domain;
using Karry.Modules.Integrity.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
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

        endpoints.MapPost("/flags/{flagId:guid}/settle", ProposeSettlementAsync)
            .RequirePermission(IntegrityPermissions.FlagsSettle)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ProposeIntegrityFlagSettlement");

        endpoints.MapPost("/flags/{flagId:guid}/settlement/approve", ConfirmSettlementAsync)
            .RequirePermission(IntegrityPermissions.FlagsSettle)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApproveIntegrityFlagSettlement");

        endpoints.MapPost("/flags/{flagId:guid}/settlement/decline", DeclineSettlementAsync)
            .RequirePermission(IntegrityPermissions.FlagsSettle)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DeclineIntegrityFlagSettlement");

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
    /// Settles an open continuity gap (RULE-FO-01) with a reason code and what happened (US-P2-FO-08); another person
    /// approves it. <c>unexplained</c> raises RULE-FO-11 at once; <c>data_entry_error</c> is refused: the entry is
    /// corrected instead. Recent sign-in; <c>If-Match</c>: the flag's <c>version</c>.
    /// </summary>
    private static async Task<Results<Ok<IntegrityFlagResponse>, ProblemHttpResult>> ProposeSettlementAsync(
        Guid flagId, SettlementInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] FlagSettlements settlements, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Settled(await settlements.ProposeAsync(flagId, request, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    /// <summary>Approves another person's settlement: the gap closes as <c>settled:&lt;reason&gt;</c> and is never raised again. Recent sign-in.</summary>
    private static async Task<Results<Ok<IntegrityFlagResponse>, ProblemHttpResult>> ConfirmSettlementAsync(
        Guid flagId, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response, [FromServices] FlagSettlements settlements, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Settled(await settlements.ConfirmAsync(flagId, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    /// <summary>Declines (or, by its proposer, withdraws) a settlement: the gap stays open.</summary>
    private static async Task<Results<Ok<IntegrityFlagResponse>, ProblemHttpResult>> DeclineSettlementAsync(
        Guid flagId, SettlementDeclineInput request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpResponse response,
        [FromServices] FlagSettlements settlements, CancellationToken cancellationToken)
    {
        var expected = RecordVersionHttp.Expected(ifMatch);
        return expected.IsSuccess
            ? Settled(await settlements.DeclineAsync(flagId, request, expected.Value, cancellationToken).ConfigureAwait(false), response)
            : expected.Error.ToProblem();
    }

    private static Results<Ok<IntegrityFlagResponse>, ProblemHttpResult> Settled(Result<IntegrityFlag> result, HttpResponse response)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        response.SetETag(result.Value.Version);
        return TypedResults.Ok(IntegrityFlagResponse.From(result.Value));
    }

    /// <summary>
    /// Flags of the caller's sites, newest first: <c>status</c> open (default), closed or all; by site, rule or subject;
    /// <c>cursor</c> is the last flag ID of the previous page.
    /// </summary>
    private static async Task<Results<Ok<IntegrityFlagListResponse>, ProblemHttpResult>> SearchFlagsAsync(
        Guid? siteId, string? rule, string? status, string? subjectType, Guid? subjectId, Guid? cursor, int? limit,
        [FromServices] FlagReading flags, CancellationToken cancellationToken)
    {
        var result = await flags.SearchAsync(siteId, rule, status, subjectType, subjectId, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(new IntegrityFlagListResponse([.. result.Value.Items.Select(IntegrityFlagResponse.From)], result.Value.NextCursor))
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
public sealed record IntegrityFlagListResponse(IReadOnlyList<IntegrityFlagResponse> Items, Guid? NextCursor);

/// <summary>An entity a flag is about or linked to.</summary>
public sealed record IntegrityFlagSubjectResponse(string Type, Guid Id);

/// <summary>A settlement waiting for another person's approval (ADR-0037 §7).</summary>
public sealed record IntegrityFlagSettlementResponse(string Reason, string Comment, string? ProposedBy, DateTimeOffset ProposedAt);

/// <summary>
/// A flag (ADR-0036 §2): the rule and its severity, what it is about and linked to, the evidence and the values its
/// explanation names; <c>closedAt</c>, <c>closeReason</c> (<c>settled:&lt;reason&gt;</c> when a person settled it) and
/// <c>closedBy</c> once it no longer holds or was settled; <c>settlement</c> while one waits for approval;
/// <c>fraudSuspectedAt</c> when RULE-FO-11 was raised about it; <c>version</c> for <c>If-Match</c>.
/// </summary>
public sealed record IntegrityFlagResponse(
    Guid Id, string Rule, string Severity, IntegrityFlagSubjectResponse Subject, string Key, Guid? SiteId, IReadOnlyList<IntegrityFlagSubjectResponse> Related,
    IReadOnlyList<string> Evidence, IReadOnlyDictionary<string, string> Details, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? CloseReason)
{
    public string? ClosedBy { get; init; }

    public IntegrityFlagSettlementResponse? Settlement { get; init; }

    public DateTimeOffset? FraudSuspectedAt { get; init; }

    public string Version { get; init; } = "";

    public static IntegrityFlagResponse From(IntegrityFlag f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new(f.Id.Value, f.Rule, f.Severity, new(f.SubjectType, f.SubjectId), f.Key, f.SiteId, [.. f.Related.Select(r => new IntegrityFlagSubjectResponse(r.Type, r.Id))],
            f.Evidence, f.Details, f.OpenedAt, f.ClosedAt, f.CloseReason)
        {
            ClosedBy = f.ClosedBy,
            Settlement = f is { SettlementReason: { } reason, SettlementComment: { } comment, SettlementProposedAt: { } at }
                ? new(reason, comment, f.SettlementProposedBy, at)
                : null,
            FraudSuspectedAt = f.FraudSuspectedAt,
            Version = RecordVersionHttp.ETag(f.Version),
        };
    }
}
