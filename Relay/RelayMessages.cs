using System.Text.Json.Serialization;

namespace PhoneUnlockService.Relay;

// ---------------------------------------------------------------------
// Wire contract between PhoneUnlockService <-> relay server <-> phone.
// The relay server itself isn't built yet (see handoff note, item 4 in
// "what's not built"); this is the contract it needs to implement:
// a dumb forwarder that routes by pc_id / pairing_code and never needs to
// understand or verify the crypto itself (verification happens here, in
// PhoneUnlockService, and independently in the phone's replay guard).
//
// All messages are single-line JSON over one WebSocket per side. The
// relay is expected to:
//   1. Let a PC connect and send "hello" once, associating this socket
//      with pc_id for the session.
//   2. Let a phone connect (details TBD - probably also a "hello" with a
//      device_id once paired) and route messages tagged with a pc_id to
//      that PC's socket, and vice versa.
//   3. NEVER itself construct or modify a signature/decision - it is a
//      transparent pipe. If the relay could forge an "approved" message,
//      the whole security model collapses (see handoff note section 4).
// ---------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(UnlockRequestMessage), "unlock_request")]
[JsonDerivedType(typeof(UnlockResponseMessage), "unlock_response")]
[JsonDerivedType(typeof(PairingOfferMessage), "pairing_offer")]
[JsonDerivedType(typeof(RelayAckMessage), "relay_ack")]
public abstract class RelayMessage
{
}

public sealed class RelayAckMessage : RelayMessage
{
    public string Detail { get; set; } = "";
    public string? PcId { get; set; }
}

/// <summary>Sent once by the PC right after connecting, so the relay knows which socket is "this PC".</summary>
public sealed class HelloMessage : RelayMessage
{
    public string PcId { get; set; } = "";

    /// <summary>
    /// Long-lived bearer credential proving this socket really is the PC
    /// it claims to be (issued out-of-band when the relay account/PC
    /// registration is created - not implemented yet, placeholder here).
    /// </summary>
    public string AuthToken { get; set; } = "";
}

/// <summary>PC -> relay -> phone: "please sign this nonce".</summary>
public sealed class UnlockRequestMessage : RelayMessage
{
    public string PcId { get; set; } = "";
    public string PcName { get; set; } = "";
    public string Nonce { get; set; } = "";
    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Phone -> relay -> PC: the signed nonce, or an explicit denial.</summary>
public sealed class UnlockResponseMessage : RelayMessage
{
    public string PcId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Nonce { get; set; } = "";

    /// <summary>"approved" or "denied" - see android_agent_prompt.md step 7.</summary>
    public string Decision { get; set; } = "denied";

    /// <summary>Base64 ASN.1 DER ECDSA signature over UTF-8 bytes of Nonce. Only present when Decision == "approved".</summary>
    public string? SignatureBase64 { get; set; }
}

/// <summary>Phone -> relay -> PC during pairing: "here's my public key for pairing code X".</summary>
public sealed class PairingOfferMessage : RelayMessage
{
    public string PairingCode { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceDisplayName { get; set; } = "";

    /// <summary>Base64 X.509 SubjectPublicKeyInfo of the phone's EC P-256 public key.</summary>
    public string PublicKeyBase64 { get; set; } = "";
}
