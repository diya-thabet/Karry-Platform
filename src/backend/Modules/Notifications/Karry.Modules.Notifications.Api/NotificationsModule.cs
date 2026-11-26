using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
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

    public IReadOnlyList<PermissionDefinition> Permissions => NotificationsPermissions.All;

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

        endpoints.MapGet("/preferences", MyPreferencesAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("GetMyNotificationPreferences");

        endpoints.MapPut("/preferences/{noticeType}/{channel}", SetMyPreferenceAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetMyNotificationPreference");

        endpoints.MapGet("/rules", ListRulesAsync)
            .RequirePermission(NotificationsPermissions.RulesManage)
            .WithName("ListNotificationRules");

        // Web Push (ADR-0039 §5), behind notifications.web-push: a person's browser, or a tablet for its site.
        endpoints.MapGet("/push/key", PushKeyAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetPushKey");

        endpoints.MapPut("/push/subscription", SubscribeAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SubscribeToPush");

        endpoints.MapPost("/push/subscription/remove", UnsubscribeAsync)
            .AllowAnyTenantMember()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("UnsubscribeFromPush");

        endpoints.MapGet("/push/device/key", PushKeyAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetDevicePushKey");

        endpoints.MapPut("/push/device/subscription", SubscribeDeviceAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SubscribeDeviceToPush");

        endpoints.MapDelete("/push/device/subscription", UnsubscribeDeviceAsync)
            .RequireDevice()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithName("UnsubscribeDeviceFromPush");

        endpoints.MapPut("/rules/{noticeType}", SwitchRuleAsync)
            .RequirePermission(NotificationsPermissions.RulesManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SwitchNotificationRule");
    }

    /// <summary>The VAPID public key a browser subscribes with (base64url); 404 while push is off for the company.</summary>
    private static async Task<Results<Ok<PushKeyResponse>, ProblemHttpResult>> PushKeyAsync([FromServices] PushSubscriptions push, CancellationToken cancellationToken)
    {
        var result = await push.KeyAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(new PushKeyResponse(result.Value)) : result.Error.ToProblem();
    }

    /// <summary>Subscribes the caller's browser: the endpoint must be a browser push service's HTTPS address.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SubscribeAsync(PushSubscriptionRequest request, [FromServices] PushSubscriptions push, CancellationToken cancellationToken)
    {
        var result = await push.SubscribeMineAsync(request.Endpoint, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>Removes the caller's subscription of this endpoint (sent in the body: an endpoint is never put in a URL).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> UnsubscribeAsync(PushSubscriptionRequest request, [FromServices] PushSubscriptions push, CancellationToken cancellationToken)
    {
        var result = await push.UnsubscribeMineAsync(request.Endpoint, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>Subscribes the calling tablet for its site: it is alerted when an entry of the site is rejected.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SubscribeDeviceAsync(
        PushSubscriptionRequest request, ClaimsPrincipal user, [FromServices] PushSubscriptions push, CancellationToken cancellationToken)
    {
        var result = await push.SubscribeDeviceAsync(DeviceCaller.From(user)!.Value, request.Endpoint, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<NoContent> UnsubscribeDeviceAsync(ClaimsPrincipal user, [FromServices] PushSubscriptions push, CancellationToken cancellationToken)
    {
        await push.UnsubscribeDeviceAsync(DeviceCaller.From(user)!.Value, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>The caller's channel choices: for each notice type, the channels they may turn off (digest, push).</summary>
    private static async Task<Results<Ok<IReadOnlyList<NoticePreferenceResponse>>, ProblemHttpResult>> MyPreferencesAsync(
        [FromServices] NoticeSettings settings, CancellationToken cancellationToken)
    {
        var result = await settings.MineAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(NoticePreferenceResponse.From(result.Value)) : result.Error.ToProblem();
    }

    /// <summary>Turns one of the caller's channels on or off for a notice type; the in-app centre is always on.</summary>
    private static async Task<Results<Ok<IReadOnlyList<NoticePreferenceResponse>>, ProblemHttpResult>> SetMyPreferenceAsync(
        string noticeType, string channel, SwitchRequest request, [FromServices] NoticeSettings settings, CancellationToken cancellationToken)
    {
        var result = await settings.SetMineAsync(noticeType, channel, request.Enabled, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(NoticePreferenceResponse.From(result.Value)) : result.Error.ToProblem();
    }

    /// <summary>The company's notice rules: each type, whether it is on, its channels and who switched it last.</summary>
    private static async Task<Ok<IReadOnlyList<NoticeRuleView>>> ListRulesAsync([FromServices] NoticeSettings settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(await settings.RulesAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Switches a rule for the whole company (ledgered). Switching one off silences an alert, so it needs a sign-in
    /// within the last 5 minutes (401 <c>auth.step_up_required</c> otherwise).
    /// </summary>
    private static async Task<Results<Ok<NoticeRuleView>, ProblemHttpResult>> SwitchRuleAsync(
        string noticeType, SwitchRequest request, [FromServices] NoticeSettings settings, CancellationToken cancellationToken)
    {
        var result = await settings.SwitchAsync(noticeType, request.Enabled, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
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

/// <summary>On or off.</summary>
public sealed record SwitchRequest(bool Enabled);

/// <summary>A person's choice for one notice type: whether the company sends it at all, and each channel they may turn off.</summary>
public sealed record NoticePreferenceResponse(string Type, bool RuleEnabled, IReadOnlyDictionary<string, bool> Channels)
{
    public static IReadOnlyList<NoticePreferenceResponse> From(IReadOnlyList<NoticePreferenceView> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        return [.. views.Select(v => new NoticePreferenceResponse(v.Type, v.RuleEnabled, v.Channels))];
    }
}

/// <summary>The VAPID public key (base64url) a browser subscribes with.</summary>
public sealed record PushKeyResponse(string PublicKey);

/// <summary>A browser's push subscription endpoint, as <c>PushSubscription.endpoint</c> gives it.</summary>
public sealed record PushSubscriptionRequest(string? Endpoint);
