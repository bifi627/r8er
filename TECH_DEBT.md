# Tech Debt

Deferred hardening, test-harness robustness, and design smells accepted on
purpose. Behavioral defects live in `BUGS.md`. Clear an item by fixing it and
deleting the row (git holds the history).

| # | Area | Description | When to pay it down |
|---|---|---|---|
| T1 | tests / TokenMinter.cs | Emulator error-body parsing assumes an `error` JSON shape; a real HTTP failure (5xx, non-JSON) could be masked. Guard with `TryGetProperty` / `EnsureSuccessStatusCode()`. | Next time the token minter is touched, or if a test fails with a confusing parse error. |
| T3 | tests / AuthReject.Steps | Relies on other step classes' untagged global `[BeforeScenario]` reset for a clean DB; not independent if run alone. | Add its own `[BeforeScenario] ResetAsync()` next time the step file is edited. |
| T4 | control-plane / CORS | POC carries wide-open CORS. Not a hole today (Bearer-header auth, no cookies) but must be scoped before public exposure. | Before the first public/prod deploy. |
| T5 | control-plane / Telemetry.cs | POC connectivity table is raw Npgsql, no EF/migrations, hardcoded — throwaway spike sink. Graduates to a tenant-scoped EF entity when signaling lands. | Item 3/5 (signaling); deleted in that PR. |
