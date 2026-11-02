# Tools & credentials inventory

The external tools and services r8er depends on (not code libraries), why each
one exists, and every credential they need. Use this for key-rotation drills.
Version pins live in `docker-compose.yml`; bump them with the `deps-bump` skill.

## Tools

| Tool | What it is | Problem it solves in r8er | Dev | Prod |
|---|---|---|---|---|
| **Jellyfin** | Open-source media server | Media engine on the user's hardware: library scan, serving, transcoding codecs the browser can't play (HEVC/AC3). The gateway tunnels its HTTP over WebRTC, so we never write a transcoder. **Pinned image, never `latest`.** | compose (`jellyfin/jellyfin:12.4.20261026-211555`) | User's NAS/PC, bundled with the gateway |
| **Pion** (Go gateway) | WebRTC stack in Go | Gateway side of the tunnel. *(A library, listed because it's the core tunnel engine.)* | native `go run` | User's NAS/PC |
| **STUN** (Google) | Public address discovery | Lets peers learn their public IP:port for direct hole-punching (86% of POC sessions). Hardcoded `stun:stun.l.google.com:19302` in the gateway. | same | same |
| **coturn** | Open-source TURN server | Local relay so the TURN fallback path can be forced and tested at a desk (`?relay` → `iceTransportPolicy: 'relay'`). The e2e starts its own on ports 3479 / 49210-49214. | compose `--profile relay`; e2e container | — |
| **Cloudflare Realtime TURN** | Hosted TURN relay | Relays traffic when direct WebRTC fails (strict NAT / guest Wi-Fi, ~14% in POC). Needed because Railway has no UDP ingress. $0.05/GB outbound. | — (coturn instead) | yes |
| **Postgres** | Relational DB | Control-plane data: tenants, users, gateways, connection telemetry. | compose `postgres:17` | Railway Postgres |
| **Firebase Auth** | Hosted identity | Sign-in + ID tokens the backend verifies. Auth only (no Firebase storage). | Auth emulator container (`demo-r8er`) | Firebase project |
| **Railway** | PaaS hosting | Runs the backend (API + signaling WS) and Postgres. | — | yes |
| **ffmpeg** | Video toolkit | `scripts/gen-test-media.sh` generates the codec test fixtures. | dev machine | — |
| **Docker / Testcontainers** | Containers + test-driven container lifecycle | Runs compose deps; integration tests use real Postgres / Firebase emulator / Jellyfin instead of mocks. | yes | CI |
| **Playwright** + Google Chrome | Browser automation | The one E2E golden path (`e2e/`): real browser → broker → gateway → Jellyfin. Branded Chrome, not bundled Chromium — item 5's `<video>` needs H.264/AAC. Stack comes up via testcontainers-node (random ports). | `npm test --prefix e2e` | CI `e2e` job |
| **GitHub Actions** | CI | Runs `scripts/check.sh`-equivalent jobs. Uses no repo secrets today. | — | CI |
| *Cloudflare R2* | Object storage | **Deferred (QOL)**: cloud files, picked over GCS for zero egress. Not wired yet. | — | — |

## Credentials

Dev credentials are fixed, throwaway values with no rotation needed. Every prod
credential is set by a human at deploy time. None are in the repo.

Where to request or rotate credentials by hand:

- **Firebase service account key:** [Firebase console → Project settings → Service accounts](https://console.firebase.google.com/project/_/settings/serviceaccounts/adminsdk) (generate), [GCP → IAM → Service accounts](https://console.cloud.google.com/iam-admin/serviceaccounts) (list/delete keys)
- **Firebase web API key restrictions:** [GCP → APIs & Services → Credentials](https://console.cloud.google.com/apis/credentials)
- **Cloudflare TURN key + API token:** [Cloudflare dashboard](https://dash.cloudflare.com/) → Realtime → TURN Server ([docs](https://developers.cloudflare.com/realtime/turn/))
- **Railway env vars + Postgres:** [Railway dashboard](https://railway.com/dashboard) → r8er project → service → Variables

| Credential | Belongs to | Used by | Where it lives | Lifetime | How to rotate |
|---|---|---|---|---|---|
| `FIREBASE_SERVICE_ACCOUNT_JSON` | Firebase / GCP service account | Backend (FirebaseAdmin init). Prod fails fast if empty. | Railway env var | Until revoked | GCP console → IAM → Service accounts → Keys: create a new key, paste the JSON into Railway, redeploy, check sign-in works, **then** delete the old key. |
| `FIREBASE_PROJECT_ID` | Firebase | Backend | Railway env var | not a secret | Only changes if the project changes. If unset it falls back to `demo-r8er` and real logins fail. |
| `VITE_FIREBASE_API_KEY`, `VITE_FIREBASE_AUTH_DOMAIN`, `VITE_FIREBASE_PROJECT_ID` | Firebase web app | Frontend bundle | Build-time env | not a secret (public by design) | Restrict the key to allowed domains in GCP. A rotated key needs a frontend rebuild. |
| `CORS_ORIGINS` | r8er backend config | Backend CORS allowlist: comma-separated SPA origins (scheme + host + port, no trailing slash, no path), e.g. `https://app.example.com,https://r8er-web.up.railway.app`. Unset = `http://localhost:5173` (vite dev). | Railway env var | not a secret | Update when the SPA's domain changes, then redeploy. If it's wrong, the SPA fails with a CORS error in the browser console. |
| `DATABASE_URL` (or `ConnectionStrings__Default`) | Railway Postgres | Backend | Railway variable reference | Until changed | Change the Postgres password in Railway. The backend picks up `DATABASE_URL` through the variable reference, so redeploy and check `/me`. |
| **Cloudflare TURN Key ID + API token** (`CLOUDFLARE_TURN_KEY_ID`, `CLOUDFLARE_TURN_API_TOKEN`) | Cloudflare Realtime → TURN | Backend `GET /turn` mints 1 h browser creds per connection. Also `scripts/gen-turn-creds.ps1` (gateway TURN, optional). | Railway env vars on the backend (set by a human at deploy). Unset = dev path (`R8ER_TURN_*` or STUN only). | Until revoked | Cloudflare dashboard → Realtime → TURN: create a new key/token, set both Railway vars, redeploy, check a `?relay` session, revoke the old one. |
| Minted TURN username/credential | Cloudflare (generated) | Browser (from `/turn`, 1 h); gateway only if `R8ER_ICE_*` is set (`gen-turn-creds.ps1`, 24 h) | Browser memory; gateway env / `dev/.cloudflare-turn.json` (git-ignored) | **1 h** browser, **24 h** script | Expires on its own. Never bake a static TURN secret into the client. |
| Enrollment token | r8er (per Add-gateway click) | Gateway's first start (`R8ER_ENROLL_TOKEN`) | API: SHA-256 hash in `enrollment_tokens`; plaintext only in the install command | **15 min, single-use** | Nothing to rotate: create a new one in the web app (Add gateway). A spent token left in shell history is harmless. |
| Gateway credential | r8er (per gateway) | Gateway → control plane (Bearer on `/gateways/ws`) | Gateway: `R8ER_STATE_DIR/gateway.json`; API: SHA-256 hash in `gateways.CredentialHash` | Until the gateway is re-onboarded | Re-onboard = rotation: delete the state dir (`dev.ps1 -ResetGateway` in dev), Add gateway, run the new command. (Server-side revocation: item 6.) |
| Gateway DTLS certificate | r8er gateway (self-signed, ECDSA P-256) | WebRTC DTLS; browsers pin its fingerprint | Gateway: `R8ER_STATE_DIR/cert.pem`; API: `gateways.DtlsFingerprint` | 10 years | Re-onboard + re-pair every browser, by design (no rotation mechanism). |
| Browser client key | the browser (WebCrypto ECDSA P-256, non-extractable) | Proves a paired browser to the gateway (session-bound signature on the `auth` channel) | Browser: IndexedDB `r8er`/`kv` `clientKey` (+ gateway pins `pin:<gatewayId>`); gateway: key fingerprint in `R8ER_STATE_DIR/allowlist.json`. Never on the server. | Until site data is cleared | Remove the browser on the gateway page, then Pair this browser again (new key if site data was cleared). |
| Jellyfin API key (`R8ER_JELLYFIN_API_KEY`) | Local Jellyfin | Gateway → Jellyfin: injected into every tunnelled request, stripped from browser requests, redacted from responses. Browsers never hold it. | Gateway env. Dev: minted by `scripts/setup-jellyfin.sh` (user `r8er` / `r8erdev`). E2E: minted per run by `e2e/jellyfin.ts`. Real installs: Jellyfin dashboard → API Keys (until item 4 automates it). | Until revoked | Jellyfin dashboard → API Keys: create a new key, restart the gateway with it, delete the old key. Dev: re-run the setup script. |
| coturn `dev:dev` (+ e2e's own `e2e:e2e`), Postgres `r8er:dev`, `R8ER_TURN_*` | Local compose / e2e | Dev only | `docker-compose.yml`, `.env.example`, `e2e/global-setup.ts` | dev-only | n/a |

## Rotation drill checklist

1. Pick the credential from the table and create the new one **before** revoking the old one.
2. Update it in Railway (or Cloudflare) and redeploy.
3. Exercise the flow it protects: sign-in for Firebase, `/me` for the DB, a forced-relay session for TURN.
4. Revoke the old credential and confirm the flow still works.
5. Write down the date here or in the PR.
