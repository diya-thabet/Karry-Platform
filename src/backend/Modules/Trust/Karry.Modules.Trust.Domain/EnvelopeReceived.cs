using Karry.SharedKernel.Domain;

namespace Karry.Modules.Trust.Domain;

/// <summary>
/// The ledger entry of a stored signature envelope (ADR-0033 §4): it binds the envelope hash to the server's receipt
/// time. A typed event so that the audit viewer can read it back and show the envelope's verification (the "Verified"
/// badge, US-P2-SIG-04 AC2); without a type the audit API answered no event and the badge never appeared.
/// </summary>
[EventType("trust.envelope_received.v1")]
[AuditSubject("envelope", nameof(EnvelopeId))]
public sealed record EnvelopeReceived(
    Guid EnvelopeId, string EnvelopeHash, string RecordType, string RecordId, int RecordVersion, Guid DeviceId, Guid CommandId, DateTimeOffset ReceivedAt)
    : DomainEvent;
