using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Domain;
using Karry.Modules.Imports.Infrastructure;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Imports.Api;

/// <summary>
/// Loading a company's data from Excel (US-P1-ORG-03, ADR-0027): templates per kind, then upload, validation report,
/// preview and commit. Routes under <c>/api/v1/imports</c>.
/// </summary>
public sealed class ImportsModule : IModule
{
    public const string WorkbookContentType = ImportFiles.ContentType;

    public string Name => "imports";

    public IReadOnlyList<PermissionDefinition> Permissions => ImportsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): templates still work, jobs fail only when called.
            services.AddImportsInfrastructure();
            return;
        }

        services.AddImportsInfrastructure(connectionString, configuration);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/templates/{kind}", TemplateAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .Produces(StatusCodes.Status200OK, contentType: WorkbookContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DownloadImportTemplate");

        endpoints.MapPost("", UploadAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .RequireIdempotencyKey()
            .Accepts<IFormFile>(WorkbookContentType)
            .WithMaxBody(2L * ImportErrors.MaxBytes)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("UploadImport");

        endpoints.MapGet("", ListAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("ListImports");

        endpoints.MapGet("/{importId:guid}", GetAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetImport");

        endpoints.MapGet("/{importId:guid}/issues", IssuesAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListImportIssues");

        endpoints.MapGet("/{importId:guid}/rows", RowsAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListImportRows");

        endpoints.MapPost("/{importId:guid}/commit", CommitAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CommitImport");

        endpoints.MapPost("/{importId:guid}/discard", DiscardAsync)
            .RequirePermission(ImportsPermissions.FilesManage)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DiscardImport");
    }

    /// <summary>The template of a kind (<c>sites</c>, <c>assets</c>, <c>meters</c>, <c>materials</c>, <c>users</c>) in French or English.</summary>
    private static async Task<IResult> TemplateAsync(string kind, string? language, [FromServices] ImportTemplates templates, CancellationToken cancellationToken)
    {
        var template = await templates.TemplateAsync(kind, language, cancellationToken).ConfigureAwait(false);
        return template.IsSuccess
            ? Results.File(template.Value, WorkbookContentType, $"karry-{kind}-{language ?? "fr"}.xlsx")
            : template.Error.ToProblem();
    }

    /// <summary>
    /// Uploads a filled-in template (the body is the <c>.xlsx</c>) for <paramref name="kind"/>; the file is checked,
    /// kept, and validated by the worker. 202 with the job, in state <c>validating</c>.
    /// </summary>
    private static async Task<Results<Accepted<ImportResponse>, ProblemHttpResult>> UploadAsync(
        HttpRequest request, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken, string? kind = null, string? fileName = null)
    {
        // One byte more than allowed is enough to refuse; a larger body is never read into memory.
        var buffer = new byte[ImportErrors.MaxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        var job = await imports.UploadAsync(kind, fileName, buffer[..length], cancellationToken).ConfigureAwait(false);
        return job.IsSuccess
            ? TypedResults.Accepted($"/api/v1/imports/{job.Value.Id}", ImportResponse.From(job.Value))
            : job.Error!.ToProblem();
    }

    /// <summary>The company's imports, newest first; cursor paging (<c>nextCursor</c> is null on the last page).</summary>
    private static async Task<Results<Ok<ImportListResponse>, ProblemHttpResult>> ListAsync(
        [FromServices] ImportJobOperations imports, CancellationToken cancellationToken, string? cursor = null, int? limit = null)
    {
        var page = await imports.ListAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsSuccess
            ? TypedResults.Ok(new ImportListResponse([.. page.Value.Items.Select(ImportResponse.From)], page.Value.NextCursor))
            : page.Error!.ToProblem();
    }

    /// <summary>State and counts of an import; poll it while it is <c>validating</c> or <c>committing</c>.</summary>
    private static async Task<Results<Ok<ImportResponse>, ProblemHttpResult>> GetAsync(
        Guid importId, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken) =>
        Respond(await imports.GetAsync(new ImportJobId(importId), cancellationToken).ConfigureAwait(false));

    /// <summary>The problems of the last validation in file order, with their cells; <c>severity</c> keeps errors or warnings only.</summary>
    private static async Task<Results<Ok<ImportIssueListResponse>, ProblemHttpResult>> IssuesAsync(
        Guid importId, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken, string? severity = null, string? cursor = null, int? limit = null)
    {
        var page = await imports.IssuesAsync(new ImportJobId(importId), severity, cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsSuccess
            ? TypedResults.Ok(new ImportIssueListResponse([.. page.Value.Items.Select(ImportIssueResponse.From)], page.Value.NextCursor))
            : page.Error!.ToProblem();
    }

    /// <summary>The preview: for each row without an error, its key and what a commit would do.</summary>
    private static async Task<Results<Ok<ImportRowListResponse>, ProblemHttpResult>> RowsAsync(
        Guid importId, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken, string? cursor = null, int? limit = null)
    {
        var page = await imports.RowsAsync(new ImportJobId(importId), cursor, limit, cancellationToken).ConfigureAwait(false);
        return page.IsSuccess
            ? TypedResults.Ok(new ImportRowListResponse([.. page.Value.Items.Select(r => new ImportRowResponse(r.Row, r.Key, r.Action))], page.Value.NextCursor))
            : page.Error!.ToProblem();
    }

    /// <summary>Asks the worker to commit a validated import without errors; the job turns <c>committing</c>.</summary>
    private static async Task<Results<Ok<ImportResponse>, ProblemHttpResult>> CommitAsync(
        Guid importId, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken) =>
        Respond(await imports.CommitAsync(new ImportJobId(importId), cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<ImportResponse>, ProblemHttpResult>> DiscardAsync(
        Guid importId, [FromServices] ImportJobOperations imports, CancellationToken cancellationToken) =>
        Respond(await imports.DiscardAsync(new ImportJobId(importId), cancellationToken).ConfigureAwait(false));

    private static Results<Ok<ImportResponse>, ProblemHttpResult> Respond(Result<ImportJob> result) =>
        result.IsSuccess ? TypedResults.Ok(ImportResponse.From(result.Value)) : result.Error!.ToProblem();
}

/// <summary>
/// An import: <c>status</c> is <c>validating</c>, <c>validated</c>, <c>committing</c>, <c>committed</c> or
/// <c>discarded</c>; the counts are those of the last validation, and of the commit once committed.
/// </summary>
public sealed record ImportResponse(
    Guid Id, string Kind, string FileName, long Size, string Sha256, string Status, int Rows, int Errors, int Warnings, int Created, int Updated, int Unchanged,
    string? Scanner, DateTimeOffset UploadedAt, DateTimeOffset ChangedAt, bool FileKept)
{
    public static ImportResponse From(ImportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id.Value, job.Kind, job.FileName, job.Size, job.Sha256, job.Status.ToString().ToLowerInvariant(), job.Rows, job.Errors, job.Warnings,
            job.Created, job.Updated, job.Unchanged, job.Scanner, job.UploadedAt, job.ChangedAt, job.FileDeletedAt is null);
    }
}

public sealed record ImportListResponse(IReadOnlyList<ImportResponse> Items, string? NextCursor);

/// <summary>A problem: <c>row</c> 0 is the file itself, 1 the headers; <c>severity</c> is <c>error</c> (blocks the commit) or <c>warning</c>.</summary>
public sealed record ImportIssueResponse(int Row, string? Column, string? Cell, string Code, string Severity, IReadOnlyDictionary<string, string>? Args)
{
    public static ImportIssueResponse From(ImportJobIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new(issue.Row, issue.Column, issue.Cell, issue.Code, issue.Severity, issue.Args);
    }
}

public sealed record ImportIssueListResponse(IReadOnlyList<ImportIssueResponse> Items, string? NextCursor);

/// <summary>A row of the preview: <c>action</c> is <c>create</c>, <c>update</c> or <c>unchanged</c>.</summary>
public sealed record ImportRowResponse(int Row, string Key, string Action);

public sealed record ImportRowListResponse(IReadOnlyList<ImportRowResponse> Items, string? NextCursor);
