# PhoneUnlock relay server

Cloudflare Worker + one Durable Object per paired PC, using the
WebSocket Hibernation API so an all-day-idle personal relay stays on the
Free plan. Forwards `hello` / `unlock_request` / `unlock_response` /
`pairing_offer` exactly per `PhoneUnlockService/Relay/RelayMessages.cs` —
it never constructs or modifies a decision itself (see that file's header
comment; this implementation is built to match it exactly).

## Architecture

```
PC (PhoneUnlockService)  ──wss──►  Worker  ──►  Durable Object "PcRelay"
                                    (routes             (per pc_id — holds
                                     by pc_id/           the live pc socket,
                                     pairing code)       any live phone
Phone (Android app)      ──wss──►                       sockets, hashed
                                                          auth token, and
                                                          registered FCM
                                                          tokens)
```

One DO instance = one PC (`idFromName(pcId)`). No separate database —
matches the handoff note's "pairing state already lives on the PC (DPAPI
file)" design; the relay only persists what it needs to *route*: a
hashed auth token, and (device_id → FCM token) pairs so it can wake a
phone that isn't currently connected.

## Connection scheme (relay-specific — not in RelayMessages.cs)

`RelayMessages.cs` defines the message *payloads*; it deliberately
doesn't define how a socket identifies itself before those payloads
start flowing (`RelayOptions.AuthToken` is explicitly commented
"issued at PC registration time — not implemented yet"). This is where
that gap gets filled:

| Connection | URL | Auth |
|---|---|---|
| PC | `wss://HOST/ws?role=pc&pc_id=<pcId>` | `Authorization: Bearer <token>` header |
| Phone (paired, normal use) | `wss://HOST/ws?role=phone&pc_id=<pcId>&device_id=<deviceId>` | none — see "Why phone connections aren't token-gated" below |
| Phone (during pairing) | `wss://HOST/ws?role=pairing&code=<6-digit code>` | possession of the just-displayed code, 5 min TTL |

Auth is checked **before** `acceptWebSocket()` is called, so the
Durable Object can tag the socket correctly (`pc` / `phone:<id>` /
`pairing`) from the very first moment — this matters because
Hibernation API tags are fixed at accept time and can't be changed
afterward.

### Why phone connections aren't token-gated

The relay is a transparent pipe, not the trust boundary — the real
security boundary is `PhoneUnlockService` verifying the phone's ECDSA
signature over the nonce against the public key it stored at pairing
time (`Security/NonceSignatureVerifier.cs`). A phone socket that only
knows `pc_id` + `device_id` can *receive* unlock requests and *submit*
a response, but it cannot produce a valid signature without the
phone's hardware-backed private key. Worst case if someone connects a
socket they shouldn't: they see a nonce (meaningless without the key)
and can send a bogus `unlock_response`, which just fails signature
verification server-side, same as any other denied/garbage attempt.

## Endpoints

- `POST /pc/register` — one-time setup, run yourself (curl/Postman), not
  automated from the PC itself:
  ```bash
  curl -X POST https://<your-worker>.workers.dev/pc/register \
    -H "Authorization: Bearer $ADMIN_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"pcId":"work-laptop"}'
  # -> {"authToken":"..."}  -- shown once, paste into appsettings.json
  ```
- `GET /ws?role=pc&pc_id=...` — PC's long-lived connection.
- `GET /ws?role=phone&pc_id=...&device_id=...` — phone's connection,
  opened on demand when an FCM push wakes the app (or if the app is
  already foregrounded).
- `GET /ws?role=pairing&code=...` — phone's short-lived connection while
  pairing, resolved to the right PC via the code the PC opened.

## Deploy

```bash
npm install
wrangler kv namespace create PAIRING_KV   # paste the returned id into wrangler.toml
wrangler secret put ADMIN_TOKEN            # any long random string, only you keep it
wrangler secret put FCM_SERVICE_ACCOUNT    # optional, see below — paste full service-account JSON as one line
wrangler deploy
```

`FCM_SERVICE_ACCOUNT` is optional: without it, `sendUnlockPush()` just
logs and no-ops, and unlock still works as long as the phone app
happens to already be connected (e.g. foregrounded) when the PC sends
`unlock_request`. Add it once the Firebase project from
`android_agent_prompt.md` exists, to get the "wake a backgrounded
phone" behavior the app spec calls for.

## Wiring TODOs on the other two components

Small, additive changes only — nothing about the existing nonce/
signature/credential logic changes on either side.

**PhoneUnlockService (C#):**
1. `RelayOptions.Url` needs `?role=pc&pc_id=<pcId>` appended, and
   `RelayClient` needs to send the `Authorization: Bearer <AuthToken>`
   header when opening the websocket (currently `HelloMessage` carries
   `AuthToken` in-band, which this relay doesn't read — the header is
   checked instead, before the socket is even accepted). Keep sending
   `HelloMessage` too; the relay just ignores it now, and it's harmless
   as a sanity echo.
2. `PairingManager.BeginPairing()` should also send a `pairing_open`
   control message (`{"type":"pairing_open","code":"<code>"}`) over the
   already-connected pc socket right after generating the code, so the
   relay knows to expect a `pairing_offer` for it. I can wire this in
   `PairingManager.cs` next if you want — it's a two-line addition
   inside `BeginPairing()`.

**Android app** (once the Studio agent finishes the initial scaffold):
1. Connect to `wss://HOST/ws?role=phone&pc_id=<pcId>&device_id=<deviceId>`
   (pairing: `wss://HOST/ws?role=pairing&code=<code>`) instead of a bare
   `/ws` — `pcId` is learned from the FCM push payload or, during
   pairing, isn't needed client-side at all (the code alone routes it).
2. After connecting on the `phone` role, send one
   `{"type":"register_fcm_token","deviceId":"...","fcmToken":"..."}`
   message (once at pairing completion, and again any time FCM rotates
   the token) so the relay can wake the app later. Everything else —
   `pairing_offer` and `unlock_response` — already matches
   `android_agent_prompt.md` and `RelayMessages.cs` as specified.

## What's deliberately out of scope here

Per the handoff note's own security model, the relay does **not**:
- verify signatures (PhoneUnlockService does),
- decide whether a public key is trustworthy (PairingManager does, on
  first pairing offer, with the fingerprint you sanity-check on the
  phone screen),
- store your Windows password, a fingerprint, or a private key in any
  form.

It only routes live JSON messages between two sockets it's confident
belong to the same `pc_id`, and optionally wakes a sleeping phone.
