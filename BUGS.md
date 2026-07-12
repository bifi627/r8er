# Known Bugs

Behavioral defects and config landmines. Fixed items move to the changelog / git,
not here. Tech-debt (design smells, deferred hardening) lives in `TECH_DEBT.md`.

| # | Area | Severity | Description | Fix |
|---|---|---|---|---|
| B1 | control-plane / deploy | High (prod-only) | `FIREBASE_PROJECT_ID` defaults to `demo-r8er`; if unset in prod, real token verification fails for every login. Fails closed (no logins), not open. | Set `FIREBASE_PROJECT_ID` in the Railway prod env; consider failing startup fast if it's still `demo-r8er` outside dev. |
| B2 | frontend / App.tsx | Low (UX) | The authed shell `.catch(() => {})`-swallows `getMe()`/`getDevices()` errors — a failed call shows an empty shell with no signal. | Surface the error in the UI once the shell grows real content (item 5). |
| B3 | control-plane / UserProvisioner | Low | A non-race `DbUpdateException` during first-sign-in provisioning surfaces as HTTP 500, not 401. Accepted for now (the race path is handled; this is the genuinely-broken-DB path). | Leave as-is; revisit if it masks a real auth failure. |
