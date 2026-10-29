using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Trust.Application;

namespace Karry.Modules.Trust.Api;

public sealed record AuditEntryListResponse(IReadOnlyList<AuditEntryResponse> Entries, string? NextCursor);

/// <summary>
/// A ledger entry as the audit viewer shows it. <c>Event</c> is the entry turned back into its typed event and written
/// through the API's JSON contract, so sensitive fields are masked as in every response (ADR-0029 §2); null when this
/// version does not know the event type. <c>Changes</c>: the before/after diff of events that have both.
/// </summary>
public sealed record AuditEntryResponse(
    long Sequence, Guid EventId, string EventType, DateTimeOffset OccurredAt, string? Actor, string? EntityType,
    string? EntityId, Guid? SiteId, JsonObject? Event, IReadOnlyList<AuditChange> Changes)
{
    public static AuditEntryResponse From(AuditEntry entry, EventSerializer events, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(events);
        var node = EventNode(entry, events, json);
        return new AuditEntryResponse(entry.Sequence, entry.EventId, entry.EventType, entry.OccurredAt, entry.Actor, entry.EntityType,
            entry.EntityId, entry.SiteId, node, AuditChanges.Of(node));
    }

    private static JsonObject? EventNode(AuditEntry entry, EventSerializer events, JsonSerializerOptions json)
    {
        try
        {
            if (events.Deserialize(entry.EventType, entry.Payload) is not { } typed
                || JsonSerializer.SerializeToNode(typed, typed.GetType(), json) is not JsonObject node)
            {
                return null;
            }

            // Already on the entry itself.
            node.Remove("eventId");
            node.Remove("occurredAt");
            return node;
        }
        catch (JsonException)
        {
            return null; // a payload altered outside the application: the integrity check reports it
        }
    }
}

/// <summary>The last integrity check of the company's ledger and who ran it; both null before the first one.</summary>
public sealed record LatestVerificationResponse(LedgerVerificationReport? Verification, string? VerifiedBy);
