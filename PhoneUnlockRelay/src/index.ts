import { PcRelay, type Env } from "./PcRelay";

export { PcRelay };

const PC_ID_PATTERN = /^[a-zA-Z0-9_-]{3,64}$/;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/pc/register" && request.method === "POST") {
      return handleRegister(request, env);
    }

    if (url.pathname === "/ws") {
      return handleWebSocketUpgrade(request, env, url);
    }

    return new Response("Not found", { status: 404 });
  },
};

/** One-time setup call: POST /pc/register {"pcId": "..."} with header
 * `Authorization: Bearer <ADMIN_TOKEN>`. Returns {"authToken": "..."}
 * exactly once - that's the value that goes into appsettings.json's
 * Relay.AuthToken. Run this yourself (curl/Postman); the C# service
 * doesn't call it automatically, since it's a one-time trust decision
 * ("this relay account now belongs to this PC") that shouldn't be
 * automatable from the same machine being registered. */
async function handleRegister(request: Request, env: Env): Promise<Response> {
  const auth = request.headers.get("Authorization") ?? "";
  if (!env.ADMIN_TOKEN || auth !== `Bearer ${env.ADMIN_TOKEN}`) {
    return new Response("Unauthorized", { status: 401 });
  }

  let body: { pcId?: string };
  try {
    body = await request.json();
  } catch {
    return new Response("Expected JSON body { \"pcId\": \"...\" }", { status: 400 });
  }

  if (!body.pcId || !PC_ID_PATTERN.test(body.pcId)) {
    return new Response("pcId must be 3-64 chars of [a-zA-Z0-9_-]", { status: 400 });
  }

  const id = env.PC_RELAY.idFromName(body.pcId);
  const stub = env.PC_RELAY.get(id);
  return stub.fetch("https://internal/register", {
    method: "POST",
    body: JSON.stringify({ pcId: body.pcId }),
  });
}

/** GET /ws?role=pc&pc_id=...          (header: Authorization: Bearer <token>)
 *  GET /ws?role=phone&pc_id=...&device_id=...
 *  GET /ws?role=pairing&code=...
 *
 * The Worker's only job is figuring out *which PC's* Durable Object
 * should handle this socket - pc/phone connections carry pc_id directly,
 * pairing connections only carry a short code, resolved via the KV
 * mapping a "pc" socket wrote when it sent a pairing_open message.
 * Everything else (auth, tagging, routing) happens inside that DO. */
async function handleWebSocketUpgrade(request: Request, env: Env, url: URL): Promise<Response> {
  if (request.headers.get("Upgrade") !== "websocket") {
    return new Response("Expected websocket", { status: 426 });
  }

  const role = url.searchParams.get("role");
  let pcId: string | null;

  if (role === "pairing") {
    const code = url.searchParams.get("code");
    if (!code) return new Response("Missing code", { status: 400 });
    pcId = await env.PAIRING_KV.get(`pairing:${code}`);
    if (!pcId) return new Response("Unknown or expired pairing code", { status: 404 });
  } else if (role === "pc" || role === "phone") {
    pcId = url.searchParams.get("pc_id");
  } else {
    return new Response("role must be pc, phone, or pairing", { status: 400 });
  }

  if (!pcId || !PC_ID_PATTERN.test(pcId)) {
    return new Response("Missing/invalid pc_id", { status: 400 });
  }

  const id = env.PC_RELAY.idFromName(pcId);
  const stub = env.PC_RELAY.get(id);
  return stub.fetch(request);
}
