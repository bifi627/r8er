# replay test fixtures

Synthetic transcripts + a throwaway seed memory store for re-testing the skill
after edits (writing-skills scenarios). Scenarios run against **copies** of these
in `C:\tmp\replay-sandbox\`, never the live memory store.

## Seed memory store (`memory/`)

- `project_deploy_target` — "The app deploys to Heroku." (the target of a contradiction)
- `feedback_test_runner` — "Run tests with `make test`." (unrelated control; must be left untouched)

## Transcripts (`transcripts/`) — the test oracle

| Signal | Where | Expected `replay` verdict |
|--------|-------|---------------------------|
| Durable explicit preference: "always run `npm run lint` before committing" | sess-A **and** sess-B (2 sessions) | **AUTO-ADD** a `feedback` memory (explicit, recurrence ≥2 sessions) |
| One-off debug fact: "port 5001 busy, use 5002" | sess-A | **DROP** as noise (ephemeral, not a durable rule) |
| Contradiction: "moved off Heroku → Railway now" | sess-C | **ASK**, then UPDATE `project_deploy_target` (contradicts a curated memory) |
| Implicit style: assistant consistently writes block-brace code, never corrected | sess-B + sess-C (assistant turns) | **ASK** (implicit signal, gated on cross-session consistency; never auto) |

`feedback_test_runner` is touched by no transcript → expect **NOOP** (left as-is).

Every applied memory must carry `metadata.originSessionId`. A pre-apply backup of
the sandbox `memory/` must exist, and `MEMORY.md` must reconcile with the files.
