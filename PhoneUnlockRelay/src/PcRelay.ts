import { safeParseMessage, type AnyMessage } from "./types";
import { sendUnlockPush } from "./fcm";

export interface Env {
  PC_RELAY: DurableObjectNamespace;
  PAIRING_KV: KVNamespace;
  ADMIN_TOKEN: string;
  FCM_SERVICE_ACCOUNT?: string;
}

/** Metadata attached to each websocket via serializeAttachment(), so it
 * survives hibernation (tags alone only support lookup, not arbitrary
 * per-socket data). */
interface SocketMeta {
  role: "pc" | "phone" | "pairing";
  deviceId?: string;
  code?: string;
}

interface PendingUnlock {
  payload: string;
  expiresAt: number;
}

async function sha256Hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function randomToken(bytes = 32): string {
  const arr = new Uint8Array(bytes);
  crypto.getRandomValues(arr);
  return [...arr].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export class PcRelay {
  state: DurableObjectState;
  env: Env;

  constructor(state: DurableObjectState, env: Env) {
    this.state = state;
    this.env = env;
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/register" && request.method === "POST") {
      return this.handleRegister(request);
    }

    if (request.headers.get("Upgrade") === "websocket") {
      return this.handleUpgrade(request, url);
    }

    return new Response("Not found", { status: 404 });
  }

  /** Called once via POST /pc/register on the Worker. Generates and
   * stores (hashed) the bearer token this PC will use for every future
   * "pc" connection. Returns the plaintext token exactly once - paste it
   * into appsettings.json's Relay.AuthToken and it's never shown again. */
  private async handleRegister(request: Request): Promise<Response> {
    const existing = await this.state.storage.get<string>("authTokenHash");
    if (existing) {
      return new Response(
        JSON.stringify({ error: "This PC is already registered. Delete its Durable Object storage to re-register." }),
        { status: 409, headers: { "Content-Type": "application/json" } },
      );
    }

    let body: { pcId?: string } = {};
    try {
      body = await request.json();
    } catch {
      // pcId is optional here (only used for logging/pairing_open lookups)
    }

    const token = randomToken();
    await this.state.storage.put("authTokenHash", await sha256Hex(token));
    if (body.pcId) await this.state.storage.put("pcIdCache", body.pcId);
    return new Response(JSON.stringify({ authToken: token }), {
      headers: { "Content-Type": "application/json" },
    });
  }

  private async handleUpgrade(request: Request, url: URL): Promise<Response> {
    const role = url.searchParams.get("role");

    if (role === "pc") {
      const authHeader = request.headers.get("Authorization") ?? "";
      const token = authHeader.startsWith("Bearer ") ? authHeader.slice(7) : "";
      const storedHash = await this.state.storage.get<string>("authTokenHash");
      if (!storedHash || !token || (await sha256Hex(token)) !== storedHash) {
        return new Response("Unauthorized", { status: 401 });
      }
      return this.accept(request, { role: "pc" }, ["pc"]);
    }

    if (role === "phone") {
      const deviceId = url.searchParams.get("device_id");
      if (!deviceId) return new Response("Missing device_id", { status: 400 });
      return this.accept(request, { role: "phone", deviceId }, ["phone", `device:${deviceId}`]);
    }

    if (role === "pairing") {
      const code = url.searchParams.get("code") ?? "";
      return this.accept(request, { role: "pairing", code }, ["pairing"]);
    }

    return new Response("Missing/invalid role", { status: 400 });
  }

  private accept(request: Request, meta: SocketMeta, tags: string[]): Response {
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.state.acceptWebSocket(server, tags);
    server.serializeAttachment(meta);
    if (meta.role === "phone") void this.sendPendingUnlock(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  private async sendPendingUnlock(phoneSocket: WebSocket): Promise<void> {
    const pending = await this.state.storage.get<PendingUnlock>("pendingUnlock");
    if (!pending) return;

    if (pending.expiresAt <= Date.now()) {
      await this.state.storage.delete("pendingUnlock");
      return;
    }

    phoneSocket.send(pending.payload);
    await this.state.storage.delete("pendingUnlock");
  }

  // -------------------------------------------------------------------
  // Hibernatable handlers - Cloudflare wakes the DO and calls these as
  // messages arrive; the object can go back to sleep between them, which
  // is what keeps an all-day-idle personal relay on the free plan.
  // -------------------------------------------------------------------

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    if (typeof raw !== "string") return; // no binary frames in this protocol
    const meta = ws.deserializeAttachment() as SocketMeta | null;
    const msg = safeParseMessage(raw);
    if (!meta || !msg) return;

    switch (meta.role) {
      case "pc":
        await this.onPcMessage(ws, msg);
        break;
      case "phone":
        await this.onPhoneMessage(ws, meta, msg);
        break;
      case "pairing":
        await this.onPairingMessage(ws, meta, msg);
        break;
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    try {
      ws.close();
    } catch {
      // already closing
    }
  }

  async webSocketError(): Promise<void> {
    // Hibernation API auto-reconnect is the client's job (see
    // RelayClient.cs's reconnect/backoff); nothing to do here beyond
    // letting the socket close naturally.
  }

  // -------------------------------------------------------------------

  private async onPcMessage(ws: WebSocket, msg: AnyMessage): Promise<void> {
    switch (msg.type) {
      case "hello":
        // Auth already happened at upgrade time via the Authorization
        // header; this is just a sanity echo of the existing HelloMessage
        // the C# RelayClient already sends first. No action needed.
        return;

      case "unlock_request": {
        const phoneSockets = this.state.getWebSockets("phone");
        const payload = JSON.stringify(msg);
        for (const phoneWs of phoneSockets) phoneWs.send(payload);

        if (phoneSockets.length === 0) {
          await this.state.storage.put<PendingUnlock>("pendingUnlock", {
            payload,
            expiresAt: Date.now() + 60_000,
          });
          await this.wakePairedDevices(msg.pcId, msg.pcName, msg.nonce);
        } else {
          await this.state.storage.delete("pendingUnlock");
        }
        return;
      }

      case "pairing_open": {
        const ttl = msg.ttlSeconds ?? 300;
        // The pc_id is baked into this DO's own identity (idFromName),
        // so we resolve it once from storage rather than trusting a
        // client-supplied value.
        const pcId = await this.getOwnPcId();
        await this.env.PAIRING_KV.put(`pairing:${msg.code}`, pcId, { expirationTtl: Math.max(60, ttl) });
        ws.send(JSON.stringify({ type: "relay_ack", detail: `pairing code ${msg.code} open` }));
        return;
      }

      default:
        ws.send(JSON.stringify({ type: "relay_error", detail: `unexpected message type on pc socket: ${msg.type}` }));
    }
  }

  private async onPhoneMessage(ws: WebSocket, meta: SocketMeta, msg: AnyMessage): Promise<void> {
    switch (msg.type) {
      case "unlock_response": {
        const pcSockets = this.state.getWebSockets("pc");
        const payload = JSON.stringify(msg);
        for (const pcWs of pcSockets) pcWs.send(payload);
        return;
      }

      case "register_fcm_token": {
        if (!meta.deviceId || meta.deviceId !== msg.deviceId) {
          ws.send(JSON.stringify({ type: "relay_error", detail: "deviceId mismatch" }));
          return;
        }
        await this.state.storage.put(`fcm:${msg.deviceId}`, msg.fcmToken);
        const ids = new Set(await this.state.storage.get<string[]>("deviceIds") ?? []);
        ids.add(msg.deviceId);
        await this.state.storage.put("deviceIds", [...ids]);
        ws.send(JSON.stringify({ type: "relay_ack", detail: "fcm token registered" }));
        return;
      }

      default:
        ws.send(JSON.stringify({ type: "relay_error", detail: `unexpected message type on phone socket: ${msg.type}` }));
    }
  }

  private async onPairingMessage(ws: WebSocket, meta: SocketMeta, msg: AnyMessage): Promise<void> {
    if (msg.type !== "pairing_offer") {
      ws.send(JSON.stringify({ type: "relay_error", detail: `expected pairing_offer, got ${msg.type}` }));
      return;
    }
    if (msg.pairingCode !== meta.code) {
      ws.send(JSON.stringify({ type: "relay_error", detail: "pairing code mismatch" }));
      return;
    }

    const pcSockets = this.state.getWebSockets("pc");
    if (pcSockets.length === 0) {
      ws.send(JSON.stringify({ type: "relay_error", detail: "PC is not currently connected to the relay" }));
      return;
    }

    // Forward the offer verbatim - PairingManager.OnPairingOfferReceived
    // on the PC side does all the actual trust decision-making and
    // public-key validation. The relay never touches the key material.
    const payload = JSON.stringify(msg);
    for (const pcWs of pcSockets) pcWs.send(payload);

    const pcId = await this.getOwnPcId();
    ws.send(JSON.stringify({ type: "relay_ack", detail: "pairing offer forwarded", pcId }));
  }

  private async getOwnPcId(): Promise<string> {
    const cached = await this.state.storage.get<string>("pcIdCache");
    if (cached) return cached;
    // Best-effort only; used purely for the pairing_open KV write above.
    // If it's ever missing (shouldn't happen post-registration), fall
    // back to the DO's own id string.
    return this.state.id.toString();
  }

  private async wakePairedDevices(pcId: string, pcName: string, nonce: string): Promise<void> {
    const deviceIds = (await this.state.storage.get<string[]>("deviceIds")) ?? [];
    for (const deviceId of deviceIds) {
      const fcmToken = await this.state.storage.get<string>(`fcm:${deviceId}`);
      if (!fcmToken) continue;
      await sendUnlockPush(this.env.FCM_SERVICE_ACCOUNT, fcmToken, { pcId, pcName, nonce });
    }
  }
}
