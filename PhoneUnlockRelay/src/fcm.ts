// ---------------------------------------------------------------------
// Minimal Firebase Cloud Messaging (HTTP v1) sender, used only to wake
// the phone app when no "phone" websocket is currently connected for the
// device an unlock_request is targeting (see android_agent_prompt.md:
// "a plain background WebSocket will get killed by Doze/battery
// optimization on most OEMs, so FCM is required, not optional").
//
// This is data-only (no visible notification text beyond what the app's
// own FCM service decides to show), matching the app spec: "no
// user-visible notification text needed beyond 'Unlock request from
// {pc_name}'" - that copy is the app's job, not the relay's.
//
// Requires the FCM_SERVICE_ACCOUNT secret (full service-account JSON,
// see wrangler.toml). If it's not configured, sendUnlockPush() just
// no-ops and logs - unlock still works as long as the phone app happens
// to already be connected.
// ---------------------------------------------------------------------

interface ServiceAccount {
  project_id: string;
  client_email: string;
  private_key: string;
}

interface CachedToken {
  accessToken: string;
  expiresAtMs: number;
}

let cachedToken: CachedToken | null = null;

function base64UrlEncode(bytes: ArrayBuffer | Uint8Array): string {
  const arr = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = "";
  for (const b of arr) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function pemToPkcs8(pem: string): ArrayBuffer {
  const body = pem
    .replace(/-----BEGIN PRIVATE KEY-----/, "")
    .replace(/-----END PRIVATE KEY-----/, "")
    .replace(/\s+/g, "");
  const binary = atob(body);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

async function signJwt(sa: ServiceAccount): Promise<string> {
  const nowSec = Math.floor(Date.now() / 1000);
  const header = { alg: "RS256", typ: "JWT" };
  const claims = {
    iss: sa.client_email,
    scope: "https://www.googleapis.com/auth/firebase.messaging",
    aud: "https://oauth2.googleapis.com/token",
    iat: nowSec,
    exp: nowSec + 3600,
  };

  const encodedHeader = base64UrlEncode(new TextEncoder().encode(JSON.stringify(header)));
  const encodedClaims = base64UrlEncode(new TextEncoder().encode(JSON.stringify(claims)));
  const signingInput = `${encodedHeader}.${encodedClaims}`;

  const key = await crypto.subtle.importKey(
    "pkcs8",
    pemToPkcs8(sa.private_key),
    { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    "RSASSA-PKCS1-v1_5",
    key,
    new TextEncoder().encode(signingInput),
  );

  return `${signingInput}.${base64UrlEncode(signature)}`;
}

async function getAccessToken(sa: ServiceAccount): Promise<string> {
  if (cachedToken && cachedToken.expiresAtMs > Date.now() + 60_000) {
    return cachedToken.accessToken;
  }

  const assertion = await signJwt(sa);
  const resp = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
      assertion,
    }),
  });

  if (!resp.ok) {
    throw new Error(`FCM token exchange failed: ${resp.status} ${await resp.text()}`);
  }

  const json = (await resp.json()) as { access_token: string; expires_in: number };
  cachedToken = {
    accessToken: json.access_token,
    expiresAtMs: Date.now() + json.expires_in * 1000,
  };
  return cachedToken.accessToken;
}

export async function sendUnlockPush(
  serviceAccountJson: string | undefined,
  fcmToken: string,
  data: { pcId: string; pcName: string; nonce: string },
): Promise<void> {
  if (!serviceAccountJson) {
    console.log("FCM_SERVICE_ACCOUNT not configured, skipping push (relying on live socket only)");
    return;
  }

  const sa = JSON.parse(serviceAccountJson) as ServiceAccount;
  const accessToken = await getAccessToken(sa);

  const resp = await fetch(
    `https://fcm.googleapis.com/v1/projects/${sa.project_id}/messages:send`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${accessToken}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        message: {
          token: fcmToken,
          android: { priority: "high" },
          data: {
            pc_id: data.pcId,
            pc_name: data.pcName,
            nonce: data.nonce,
          },
        },
      }),
    },
  );

  if (!resp.ok) {
    // Don't throw - a failed wake push shouldn't take down the unlock
    // attempt if the phone happens to reconnect on its own in time.
    console.error(`FCM send failed: ${resp.status} ${await resp.text()}`);
  }
}
