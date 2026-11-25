using System.Security.Cryptography;
using System.Text;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Domain;
using Karry.Modules.Notifications.Infrastructure;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Notifications.Api;

/// <summary>
/// Notifications (ADR-0039): what concerns each person, from the events of the other modules. Every member reads and
/// marks their own, never another's. Routes under <c>/api/v1/notifications</c>.
/// </summary>
public sealed class NotificationsModule : IModule
{
    public string Name => "notifications";

    public IReadOnlyList<PermissionDefinition> Permissions => [];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.AddNotificationsInfrastructure(connectionString);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/mine", MineAsync)
            .AllowAnyTenantMember()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetMyNotifications");

        endpoints.MapPost("/{notificationId:guid}/read", MarkReadAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("MarkNotificationRead");

        endpoints.MapPost("/read-all", MarkAllReadAsync)
            .AllowAnyTenantMember()
            .WithName("MarkAllNotificationsRead");
    }

    /// <summary>
    /// The caller's notifications, newest first (<c>unread</c>: only those not read yet), and how many are unread in
    /// all. The page carries an <c>ETag</c>: with <c>If-None-Match</c> an unchanged page is a 304 (the apps poll).
    /// </summary>
    private static async Task<Results<Ok<NotificationListResponse>, StatusCodeHttpResult, ProblemHttpResult>> MineAsync(
        bool? unread, int? limit, [FromHeader(Name = "If-None-Match")] string? ifNoneMatch, HttpResponse response,
        [FromServices] NotificationReading notifications, CancellationToken cancellationToken)
    {
        var result = await notifications.MineAsync(unread ?? false, limit, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tag = PageTag(result.Value);
        response.Headers.ETag = tag;
        response.Headers.CacheControl = "private, no-cache";
        return string.Equals(ifNoneMatch, tag, StringComparison.Ordinal)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(new NotificationListResponse([.. result.Value.Items.Select(NotificationResponse.From)], result.Value.Unread));
    }

    private static async Task<Results<Ok<NotificationResponse>, ProblemHttpResult>> MarkReadAsync(
        Guid notificationId, [FromServices] NotificationReading notifications, CancellationToken cancellationToken)
    {
        var result = await notifications.MarkReadAsync(notificationId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(NotificationResponse.From(result.Value)) : result.Error.ToProblem();
    }

    private static async Task<Ok<MarkedReadResponse>> MarkAllReadAsync([FromServices] NotificationReading notifications, CancellationToken cancellationToken) =>
        TypedResults.Ok(new MarkedReadResponse(await notifications.MarkAllReadAsync(cancellationToken).ConfigureAwait(false)));

    /// <summary>The page as it is now: each notification, whether it is read, and the unread count.</summary>
    private static string PageTag(NotificationPage page)
    {
        var text = string.Join(';', page.Items.Select(n => $"{n.Id.Value:N}.{(n.ReadAt is null ? 0 : 1)}")) + "|" + page.Unread;
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32]}\"";
    }
}

/// <summary>A person's notifications, newest first, and how many are unread in all.</summary>
public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int Unread);

/// <summary>
/// A notification (ADR-0039 §1): its type and the values its words name (the apps write the sentence in the reader's
/// language), where it opens, its site, when it came and when it was read.
/// </summary>
public sealed record NotificationResponse(
    Guid Id, string Type, IReadOnlyDictionary<string, string> Parameters, string Link, Guid? SiteId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    public static NotificationResponse From(Notification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        return new(n.Id.Value, n.Type, n.Parameters, n.Link, n.SiteId, n.CreatedAt, n.ReadAt);
    }
}

/// <summary>How many notifications were marked read.</summary>
public sealed record MarkedReadResponse(int Count);
