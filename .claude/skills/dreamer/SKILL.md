---
name: dreamer
description: Use when auditing, pruning, or cleaning up the agent's own file-based memory store (the memory/ directory and its MEMORY.md index) — e.g. memories have gone stale after the project moved on, the index and files have drifted apart, wiki-links dangle, or frontmatter `name:` no longer matches filenames. Also sweeps the project skills (.claude/skills) for repo-state claims that have drifted. Triggered by "review your memory", "clean up the memories", or /dreamer.
---

# Dreamer — memory consolidation

## Overview

Like sleep consolidating the day: prune what the repo now records on its own, repair the index and the link graph, normalize identifiers, and re-ground every surviving claim against the **live codebase** — never against the memory's own (point-in-time) text.

**Core principle: a memory's age is not evidence. The repo is.** Verify before you trust, delete, or keep.

## When to use

- The user asks to review / clean / audit memory, or invokes `/dreamer`.
- After a big project shift (features shipped, branches merged) that may have stranded memories.
- When MEMORY.md and the files have visibly drifted.

Not for: writing a single new memory (just write it), or editing project code.

## The five passes

Let `MEM` = the memory directory. Do passes 1–4 as analysis + edits, then run pass 5 ALWAYS.

1. **Reconcile index ↔ files.** Every MEMORY.md line links an existing file; every file (except MEMORY.md) has exactly one line. Orphan (file, no line) → add a line. Dangling index link (line, no file) → remove the line (or restore the file if the deletion was wrong).

2. **Staleness audit — verify against git/code, NOT the memory.** For each `project`/tracking memory, check its claim against current `git log` / `git branch` / `grep`.
   - DELETE tracking/status memories whose work shipped — branch state, "next up", "not yet merged/pushed" are now repo-derivable.
   - DELETE any memory the current code contradicts.
   - Do NOT touch durable preferences here just because they're old (see pass 4 / Keep-vs-delete).
   - **Same audit, second store: sweep `.claude/skills/*/SKILL.md`.** Skills that assert repo state (file names, versions, pending-work lists, "currently X") drift exactly like tracking memories. Verify each such claim against the repo; fix drifted ones in place — prefer rephrasing to a durable rule or runtime derivation over updating the snapshot.

3. **Reference-anchor check.** Each `reference` memory cites files / symbols / line refs — confirm they still resolve.
   - Cited example GONE but the underlying lesson durable (a framework behavior, a recurring gotcha): **UPDATE** — mark the example historical, fix the citation/links. Do NOT delete the lesson.
   - Subject was purely app-specific and removed entirely, no durable kernel: delete is fine.

4. **Normalize identifiers (the part ad-hoc review skips).**
   - Canonical id = **filename without `.md`**. Set `name:` frontmatter equal to it, for *every* surviving file (not just the ones you edited).
   - Every `[[wikilink]]` must use that canonical slug and resolve to a real file. Rewrite hyphenated/mangled slugs (`[[branch-workflow]]` → `[[project_branch_workflow]]`).
   - `[[ ]]` means **memory→memory link only**. References to skills/tools/files use backticks (`` `deps-bump` ``), never `[[ ]]`.

5. **Verify by scanning — never by reasoning.** "No broken links remain" asserted from reasoning is not evidence. Run the block below; all checks must come back clean.

## Verification block (run at the end; set MEM first)

```bash
cd "$MEM"
echo "index lines:"; grep -c '^- \[' MEMORY.md
echo "files:";       ls -1 *.md | grep -v '^MEMORY.md$' | wc -l   # must equal index lines
grep -oP '\]\(\K[^)]+\.md' MEMORY.md | sort -u > /tmp/idx
ls -1 *.md | grep -v '^MEMORY.md$' | sort -u > /tmp/fil
echo "in index, no file:"; comm -23 /tmp/idx /tmp/fil           # must be empty
echo "file, not in index:"; comm -13 /tmp/idx /tmp/fil          # must be empty
for f in *.md; do [ "$f" = MEMORY.md ] && continue; b=${f%.md}; \
  n=$(grep -m1 '^name:' "$f" | sed 's/^name:[[:space:]]*//' | tr -d '"'"'"' \r'); \
  [ "$n" != "$b" ] && echo "NAME OFF: $b ($n)"; done                # must print nothing
for x in $(grep -rhoP '\[\[\K[^\]]+' *.md | sort -u); do \
  [ -f "$x.md" ] || echo "DANGLING: [[$x]]"; done                   # must print nothing
```

Clean = the two counts are equal and all four lists are empty. (Any skill-name still in `[[ ]]` would show as DANGLING — that means you missed a de-bracket in pass 4.)

## Keep vs delete

| Memory | Verdict |
|--------|---------|
| `project` tracking: branch / PR / "next up" whose work shipped | DELETE (repo-derivable) |
| Any memory the current code contradicts | DELETE |
| `reference`: cited example gone, lesson durable | UPDATE (mark historical, fix links) |
| `reference` / `project`: subject removed, no durable kernel | DELETE |
| `feedback` / `user` preference, still accurate | KEEP — age is irrelevant |

## Common mistakes

- **Trusting the memory's own text.** It's point-in-time; re-derive from the repo.
- **Claiming the link graph is clean without scanning.** Reason → wrong; scan → sure.
- **Leaving `name:` mismatched on surviving files.** "Looks clean" hides slug drift; only the scan catches it. Normalize *every* file, not just edited ones.
- **Deleting durable feedback for being old**, or **keeping a tracking memory because it's long/detailed.** Judge by repo-derivability, not size or age.
- **Editing files but not MEMORY.md** (or vice versa). The index and the files move together — a delete is two edits, an add is two edits.
