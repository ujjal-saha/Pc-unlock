// ---------------------------------------------------------------------
// Mirrors PhoneUnlockService/Relay/RelayMessages.cs exactly. These four
// shapes are the FIXED contract the C# service and the (not yet built)
// Android app speak. The relay must never construct or alter the
// meaningful content of any of these - it only routes them (see the
// big comment block at the top of RelayMessages.cs).
// ---------------------------------------------------------------------

export interface HelloMessage {
  type: "hello";
  pcId: string;
  authToken: string;
}

export interface UnlockRequestMessage {
  type: "unlock_request";
  pcId: string;
  pcName: string;
  nonce: string;
  issuedAtUtc: string;
}

export interface UnlockResponseMessage {
  type: "unlock_response";
  pcId: string;
  deviceId: string;
  nonce: string;
  decision: "approved" | "denied";
  signatureBase64?: string;
}

export interface PairingOfferMessage {
  type: "pairing_offer";
  pairingCode: string;
  deviceId: string;
  deviceDisplayName: string;
  publicKeyBase64: string;
}

export type ContractMessage =
  | HelloMessage
  | UnlockRequestMessage
  | UnlockResponseMessage
  | PairingOfferMessage;

// ---------------------------------------------------------------------
// Relay-only control messages. These are NOT in RelayMessages.cs because
// they don't need to exist on the C#/Kotlin side of the trust boundary -
// they only coordinate routing between the Worker/Durable Object and
// whichever socket is talking to it right now. Both PhoneUnlockService
// and the Android app will need a couple of small, additive changes to
// send these (see relay/README.md "Wiring TODOs") - nothing about the
// existing signature/nonce/credential logic changes.
// ---------------------------------------------------------------------

/** PC -> relay, sent on the already-authenticated "pc" socket right after
 * calling PairingManager.BeginPairing() locally, so the relay knows which
 * PC a soon-to-arrive pairing code belongs to. */
export interface PairingOpenMessage {
  type: "pairing_open";
  code: string;
  ttlSeconds?: number; // defaults to 300, matching PairingManager's PairingWindow
}

/** Phone -> relay, sent once after connecting (pairing or normal), so the
 * relay can wake this device via FCM the next time it's offline for an
 * unlock_request. Low-stakes if forged/wrong: worst case is a missed or
 * misdirected push notification, not a forged unlock (that still requires
 * the phone's hardware-backed private key). */
export interface RegisterFcmTokenMessage {
  type: "register_fcm_token";
  deviceId: string;
  fcmToken: string;
}

/** relay -> either side, generic ack/error so a badly-formed or
 * out-of-order message doesn't just silently vanish. */
export interface RelayAckMessage {
  type: "relay_ack" | "relay_error";
  detail: string;
  pcId?: string;
}

export type AnyMessage =
  | ContractMessage
  | PairingOpenMessage
  | RegisterFcmTokenMessage
  | RelayAckMessage;

export function safeParseMessage(raw: string): AnyMessage | null {
  try {
    const parsed = JSON.parse(raw);
    if (typeof parsed === "object" && parsed !== null && typeof parsed.type === "string") {
      return parsed as AnyMessage;
    }
    return null;
  } catch {
    return null;
  }
}
