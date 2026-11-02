# Tools & credentials inventory

The external tools and services r8er depends on (not code libraries), why each
one exists, and every credential they need. Use this for key-rotation drills.
Version pins live in `docker-compose.yml`; bump them with the `deps-bump` skill.

## Tools

| Tool | What it is | Problem it solves in r8er | Dev | Prod |
|---|---|---|---|---|
| **Jellyfin** | Open-source media server | Media engine on the user's hardware: library scan, serving, transcoding codecs the browser can't play (HEVC/AC3). The agent tunnels its HTTP over WebRTC, so we never write a transcoder. **Pinned image, never `latest`.** | compose (`jellyfin/jellyfin:12.4.20261026-211555`) | User's NAS/PC, bundled with the agent |
| **Pion** (Go agent) | WebRTC stack in Go | Agent side of the tunnel. *(A library, listed because it's the core tunnel engine.)* | native `go run` | User's NAS/PC |
| **STUN** (Google) | Public address discovery | Lets peers learn their public IP:port for direct hole-punching (86% of POC sessions). Hardcoded `stun:stun.l.google.com:19302` in the agent. | same | same |
| **coturn** | Open-source TURN server | Local relay so the TURN fallback path can be forced and tested at a desk (`iceTransportPolicy: 'relay'`). | compose `--profile relay` | — |
| **Cloudflare Realtime TURN** | Hosted TURN relay | Relays traffic when direct WebRTC fails (strict NAT / guest Wi-Fi, ~14% in POC). Needed because Railway has no UDP ingress. $0.05/GB outbound. | — (coturn instead) | yes |
| **Postgres** | Relational DB | Control-plane data: tenants, users, devices, connection telemetry. | compose `postgres:17` | Railway Postgres |
| **Firebase Auth** | Hosted identity | Sign-in + ID tokens the backend verifies. Auth only (no Firebase storage). | Auth emulator container (`demo-r8er`) | Firebase project |
| **Railway** | PaaS hosting | Runs the backend (API + signaling WS) and Postgres. | — | yes |
| **ffmpeg** | Video toolkit | `scripts/gen-test-media.sh` generates the codec test fixtures. | dev machine | — |
| **Docker / Testcontainers** | Containers + test-driven container lifecycle | Runs compose deps; integration tests use real Postgres / Firebase emulator / Jellyfin instead of mocks. | yes | CI |
| **Playwright** + Google Chrome | Browser automation | The one E2E golden path (`e2e/`): real browser → broker → agent → Jellyfin. Branded Chrome, not bundled Chromium — item 5's `<video>` needs H.264/AAC. Stack comes up via testcontainers-node (random ports). | `npm test --prefix e2e` | CI `e2e` job |
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
| `DATABASE_URL` (or `ConnectionStrings__Default`) | Railway Postgres | Backend | Railway variable reference | Until changed | Change the Postgres password in Railway. The backend picks up `DATABASE_URL` through the variable reference, so redeploy and check `/me`. |
| **Cloudflare TURN Key ID + API token** | Cloudflare Realtime → TURN | POC: `scripts/gen-turn-creds.ps1` (prompts for them). MVP: control plane mints per-session creds. | Dennis's Cloudflare account (not stored in the repo or on Railway yet) | Until revoked | Cloudflare dashboard → Realtime → TURN: create a new key/token, update wherever the control plane reads it (MVP), revoke the old one. |
| Minted TURN username/credential (`R8ER_ICE_USERNAME` / `R8ER_ICE_CREDENTIAL`) | Cloudflare (generated) | Agent + browser | `dev/.cloudflare-turn.json` (git-ignored), agent env | **24h** (`-Ttl`) | Expires on its own. Re-run `gen-turn-creds.ps1`. Never bake a static TURN secret into the client. |
| Enrollment token | r8er (per Add-agent click) | Agent's first start (`R8ER_ENROLL_TOKEN`) | Server: SHA-256 hash in `enrollment_tokens`; plaintext only in the install command | **15 min, single-use** | Nothing to rotate: create a new one in the web app (Add agent). A spent token left in shell history is harmless. |
| Agent credential | r8er (per agent) | Agent → control plane (Bearer on `/agents/ws`) | Agent: `R8ER_STATE_DIR/agent.json`; server: SHA-256 hash in `devices.agent_credential_hash` | Until the agent is re-onboarded | Re-onboard = rotation: delete the state dir (`dev.ps1 -ResetAgent` in dev), Add agent, run the new command. (Server-side revocation: item 6.) |
| Agent DTLS certificate | r8er agent (self-signed, ECDSA P-256) | WebRTC DTLS; browsers pin its fingerprint | Agent: `R8ER_STATE_DIR/cert.pem`; server: `devices.dtls_fingerprint` | 10 years | Re-onboard + re-pair every browser, by design (no rotation mechanism). |
| Browser device key | the browser (WebCrypto ECDSA P-256, non-extractable) | Proves a paired browser to the agent (session-bound signature on the `auth` channel) | Browser: IndexedDB `r8er`/`kv` `deviceKey` (+ agent pins `pin:<deviceId>`); agent: key fingerprint in `R8ER_STATE_DIR/allowlist.json`. Never on the server. | Until site data is cleared | Remove the browser on the agent page, then Pair this device again (new key if site data was cleared). |
| Jellyfin API key | Local Jellyfin | Agent → Jellyfin | Dev: minted by `scripts/setup-jellyfin.sh` (user `r8er` / `r8erdev`) | Until revoked | MVP: the agent owns the key. Dev: re-run the setup script. |
| coturn `dev:dev`, Postgres `r8er:dev`, `VITE_TURN_*` | Local compose | Dev only | `docker-compose.yml`, `.env.example` | dev-only | n/a |

## Rotation drill checklist

1. Pick the credential from the table and create the new one **before** revoking the old one.
2. Update it in Railway (or Cloudflare) and redeploy.
3. Exercise the flow it protects: sign-in for Firebase, `/me` for the DB, a forced-relay session for TURN.
4. Revoke the old credential and confirm the flow still works.
5. Write down the date here or in the PR.
