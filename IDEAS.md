# Ideas

Wanted but not yet built, and neither a defect (`BUGS.md`) nor debt
(`TECH_DEBT.md`). Promote to a task when picked up; delete when done or
dropped. No numbers — reference items by heading.

### Phone as a local dev client (LAN + HTTPS)
**Area:** dev env

Local dev only works from the PC itself: Vite + backend bind `localhost`, the
page hardcodes `http://localhost:5028` (API) and `:9099` (auth emulator), and
plain `http://<pc-ip>` is not a secure context → no `crypto.subtle` → no device
key → a phone can't pair. Owner wants the phone as second local client. Plan:
Vite `--host` + self-signed HTTPS (`@vitejs/plugin-basic-ssl`, new dev dep —
justify), backend on `0.0.0.0` with HTTPS (or proxied through Vite so the phone
needs one cert), API/emulator URLs derived from `location.hostname`, `dev.ps1`
switch (e.g. `-Lan`). Phone clicks through one cert warning. Agent page
(`http://<pc-ip>:8097`) stays plain HTTP — needs no WebCrypto. Interim: test
phones against the Railway dev deployment (HTTPS).

**When:** before the iOS device test / item 5 streaming client, whichever comes first.
