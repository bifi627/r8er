# r8er — Control Plane Foundation (MVP item 1) — Design

**Date:** 2026-07-12
**Phase:** 1 (MVP), build-order item 1 of 6.
**Basis:** `init/2026-07-05-r8er-implementation-plan.md` §Phase 1,
`init/2026-07-05-r8er-handover-notes.md` (auth/tenancy fine print). Those win on
any disagreement.
**Status:** Design approved; ready for an implementation plan.

## Goal

The schema and auth spine every later MVP item hangs off: a signed-in user, the
tenant (= household) they own, the device registry that pairing (item 2) will
populate — with tenant isolation enforced structurally from migration #1, and a
Testcontainers integration-test harness that exercises the real auth path.

**One reviewable PR.** "Done" = `scripts/check.sh` green, the auth+isolation flow
driven through the integration tests, this doc's decisions reflected in code.

## Decisions settled in brainstorming (do not relitigate)

| Fork | Decision | Why |
|---|---|---|
| User ↔ tenant | **Direct FK `users.tenant_id`**, tenant auto-provisioned on first sign-in | One user = one household in MVP; multi-member is QOL. Retrofit later is an additive, non-security migration — unlike fingerprint pins. YAGNI over a speculative join table. |
| Token verification | **FirebaseAdmin SDK** (`VerifyIdTokenAsync`) | Only option honoring the non-negotiable "same code path dev+prod, only issuer differs": the emulator issues unsigned (`alg=none`) tokens, and FirebaseAdmin skips the signature *only* when `FIREBASE_AUTH_EMULATOR_HOST` is set. A hand-rolled JWKS/JwtBearer path would need signature validation disabled in dev → a different code path → rule violation. |
| Devices in migration #1 | **Core row now, pairing lifecycle columns in item 2** | The DTLS fingerprint pin gets a home from day 1 (retrofitting pins onto paired devices is the painful path — plan §item 2). Claim-code/status columns wait for item 2's own brainstorm — no columns the MVP can't yet fill. |
| Integration test style | **Reqnroll (behaviors) + xUnit (edges)**, one shared Testcontainers fixture | Owner values BDD behavior tests; isolation scenarios double as the Checkpoint-3 "tenant isolation written up" artifact. xUnit for fine-grained edges. |

## Schema (migration #1)

```
tenants
  id          uuid  PK  default gen_random_uuid()
  name        text  NULL          -- cosmetic; UI renames. Owner is implicit (the one user).
  created_at  timestamptz NOT NULL default now()

users
  id           uuid PK  default gen_random_uuid()      -- OUR pk, never the Firebase UID
  firebase_uid text NOT NULL UNIQUE                     -- maps Firebase identity → us
  email        text NULL                                -- from the token; the shell shows it
  tenant_id    uuid NOT NULL REFERENCES tenants(id)
  created_at   timestamptz NOT NULL default now()

devices                                                 -- tenant-owned; first table the filter guards
  id                    uuid PK  default gen_random_uuid()
  tenant_id             uuid NOT NULL REFERENCES tenants(id)
  name                  text NULL
  dtls_fingerprint      text NULL   -- SHA-256 of agent cert, PINNED at pairing (item 2 fills)
  agent_credential_hash text NULL   -- long-lived agent credential, hashed (item 2 fills)
  last_seen_at          timestamptz NULL   -- heartbeat → online/offline in the UI
  created_at            timestamptz NOT NULL default now()
```

Deliberately omitted (YAGNI): `tenants.owner_user_id` (owner = the sole user in
MVP; add it with multi-member), any pairing state machine (`claim_code`,
`status`, `paired_at` → item 2), roles/memberships (QOL). `gen_random_uuid()` is
Postgres-native (pgcrypto/`gen_random_uuid` is built into PG 13+, and this project
runs PG 17) — no app-side GUID generation needed, but EF may still assign the key
client-side; either is fine.

## Tenant isolation — the crown-jewel invariant

Structural, not per-query. One marker interface, one global filter, one
per-request tenant value:

```csharp
public interface ITenantOwned { Guid TenantId { get; } }   // Device : ITenantOwned

// R8erDbContext holds a per-request tenant id, set by the auth layer:
public Guid? CurrentTenantId { get; set; }

// OnModelCreating — applied once, to every ITenantOwned entity via one loop:
foreach (var et in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
    modelBuilder.Entity(et.ClrType)
        .HasQueryFilter(/* e => e.TenantId == CurrentTenantId */);
```

- **Filter applies to `devices` and every future tenant-owned table — never to
  `users`/`tenants`.** Those must be readable *before* a tenant is known (you read
  the user to learn their tenant). They are keyed/looked up by `firebase_uid` /
  `id`, not tenant-filtered.
- New tenant-owned table = implement `ITenantOwned`; the filter is automatic. The
  safe path is the default path.
- `CurrentTenantId` null (unauthenticated context) ⇒ filter matches nothing ⇒ a
  bug fails closed, not open.

## Auth + provisioning

A custom `AuthenticationHandler` registered as scheme `"Firebase"`, so `[Authorize]`
and `HttpContext.User` work idiomatically:

1. Read `Authorization: Bearer <idToken>`. Missing/malformed → 401.
2. `await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken, ct)` → `firebase_uid`
   (+ email). **Identical call in dev and prod**; `FIREBASE_AUTH_EMULATOR_HOST`
   (dev) vs a real service account (prod) is the only difference. No dev bypass.
3. Resolve `users` by `firebase_uid`. If absent → **one transaction**: insert
   `tenants` row, then `users` row pointing at it. The `firebase_uid` UNIQUE
   constraint settles the concurrent-first-request race — the loser catches the
   unique violation and re-reads.
4. Set `db.CurrentTenantId = user.tenant_id` (via the scoped DbContext / a scoped
   accessor) for the rest of the request → the global filter is live.

`FirebaseApp.Create(...)` is initialized once at startup with `FIREBASE_PROJECT_ID`
(`demo-r8er` in dev). Membership/authorization ("what may they touch") stays in
Postgres, never Firebase custom claims (keeps Firebase swappable — handover note).

## Endpoints (this PR only)

Minimum to prove the spine end-to-end. CRUD stays boring — plain EF Core, no
repository ceremony.

- `GET /me` → `{ user id, email, tenant id }` for the signed-in user.
- `GET /devices` → the caller's tenant's devices (empty until item 2 pairs one).
  Proves the global filter returns only the caller's rows.

Device creation/rename/removal belongs to the pairing flow (item 2); not here.

## Frontend (this PR)

Plain React SPA (no meta-framework — auth-gated, no SEO). Firebase JS SDK pointed
at the emulator in dev (`VITE_FIREBASE_AUTH_EMULATOR`). Sign-in (Google + email),
then an empty authed shell: shows the user's email (from `/me`) and an empty device
list (from `/devices`). Signed-out → sign-in screen. That's the whole surface —
the streaming UI is item 5.

## Integration tests (same PR — the "done" evidence)

The harness is part of item 1's deliverable: the tenant-isolation test cannot
exist without this schema, and it is the most important test in the MVP.

**Fixture (shared across the suite; containers are expensive):**
- **Postgres** via `Testcontainers.PostgreSql`; migrations applied on startup.
- **Firebase Auth emulator** via a `GenericContainer` — reusing the *same image +
  command as `docker-compose.yml`* (`node:22-alpine`,
  `firebase-tools emulators:start --only auth --project demo-r8er`, port 9099),
  wait-for-log `"All emulators ready"`. For CI determinism, prefer a tiny
  prebuilt Dockerfile (node + firebase-tools baked in) built by Testcontainers,
  so startup isn't an `npx` npm pull. `ponytail: prebuilt image; fall back to
  npx-at-startup if maintaining a Dockerfile isn't worth it.`
- `WebApplicationFactory<Program>` overrides config so `ConnectionStrings__Default`
  and `FIREBASE_AUTH_EMULATOR_HOST` point at the two containers' mapped ports;
  `FIREBASE_PROJECT_ID=demo-r8er`.

**Real tokens, real path (no bypass):** tests mint an ID token by calling the
emulator's Identity Toolkit REST endpoint
(`POST …/identitytoolkit.googleapis.com/v1/accounts:signUp?key=<any>` with
`returnSecureToken: true`). That token flows through the production
`VerifyIdTokenAsync` path — the tests exercise the same auth code prod uses.

**Reset:** per-test truncate-all in the fixture (lazy, no dep). Respawn is the
standard alternative if truncate gets unwieldy. Tenant-isolation tests want
multiple tenants coexisting, so a per-test reset is the right shape.

**Split:**
- **Reqnroll `.feature` files** — headline behaviors: first-sign-in provisioning,
  tenant isolation (a device owned by tenant A is invisible to tenant B), auth
  reject (missing/invalid token → 401). The isolation feature is the
  Checkpoint-3 written artifact.
- **xUnit facts** — edges: provisioning race (two concurrent first requests →
  exactly one tenant+user), the filter holds even when a query "forgets"
  `.Where(tenant)` (proves the *filter*, not the caller, enforces isolation),
  malformed-token handling.

This harness becomes the pattern every later MVP item extends toward the
high-coverage goal.

## New dependencies (justified — lockfile diffs are review triggers)

| Package | Why |
|---|---|
| `Microsoft.EntityFrameworkCore` + `Npgsql.EntityFrameworkCore.PostgreSQL` | The decided ORM ("plain EF Core CRUD"); replaces the POC's raw Npgsql for real entities. |
| `Microsoft.EntityFrameworkCore.Design` | Migrations tooling (`dotnet ef`). |
| `FirebaseAdmin` | Firebase ID-token verification; the only option satisfying the same-code-path non-negotiable. Google's official SDK. |
| `Testcontainers.PostgreSql` | Postgres container for integration tests. |
| `Reqnroll` + xUnit adapter | BDD behavior tests (owner-chosen). |

Raw `Npgsql` stays referenced while the POC telemetry table lives (below).

## Out of scope (deferred on purpose)

- **Pairing flow** — claim code, QR, fingerprint *capture*, agent credential
  issuance: item 2. Migration #1 only reserves the nullable columns.
- **Signaling broker** — item 3. The POC's hardcoded-room signaling
  (`Signaling.cs`) is untouched here.
- **Telemetry EF entity** — the raw-Npgsql POC table (`Telemetry.cs`,
  `poc_connectivity`) stays as-is; it graduates to a tenant-scoped EF entity when
  signaling lands (item 3/5) and is deleted in that PR. Not migrated now.
- **Multi-member / memberships / roles** — QOL.
- **TURN credentials, media browsing, streaming, privacy indicator** — items 5–6.

## Open items for the implementation plan

- Exact package versions + Testcontainers / Reqnroll / FirebaseAdmin API
  specifics — verify against current docs (context7) at implementation time.
- Whether the scoped tenant value lives on the DbContext directly or a separate
  scoped `ITenantContext` accessor the DbContext reads — an implementation call;
  both satisfy this design.
- Emulator test image: prebuilt Dockerfile vs `npx` — decide by measuring CI
  startup.
