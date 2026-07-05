"""Collapse a Claude Code JSONL session transcript to a compact dialogue skeleton.

Keeps the human <-> main-agent dialogue (user turns + assistant text); drops the
tool I/O, thinking, sidechains, and metadata that make raw transcripts huge.
Used by the `replay` skill before any extraction subagent reads a transcript.
"""
import json
import re
import sys

_COMMAND_PREFIXES = ("<command-name>", "<local-command-stdout>", "<local-command-caveat>")
_REMINDER_RE = re.compile(r"<system-reminder>.*?</system-reminder>", re.DOTALL)

_DROP_TYPES = {
    "system", "file-history-snapshot", "attachment", "mode",
    "permission-mode", "ai-title", "last-prompt",
}


def _assistant_text(content):
    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        parts = [b.get("text", "") for b in content
                 if isinstance(b, dict) and b.get("type") == "text"]
        return "\n".join(p for p in parts if p).strip()
    return ""


def _user_text(content):
    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        # a tool_result block means this 'user' record is a tool echo, not prose
        if any(isinstance(b, dict) and b.get("type") == "tool_result" for b in content):
            return ""
        parts = [b.get("text", "") for b in content
                 if isinstance(b, dict) and b.get("type") == "text"]
        return "\n".join(p for p in parts if p).strip()
    return ""


def reduce_records(records):
    out = []
    for r in records:
        if not isinstance(r, dict):
            continue
        if r.get("isSidechain") or r.get("isMeta"):
            continue
        t = r.get("type")
        if t in _DROP_TYPES:
            continue
        msg = r.get("message") or {}
        if t == "user":
            if "toolUseResult" in r:
                continue
            text = _user_text(msg.get("content"))
            if text.startswith(_COMMAND_PREFIXES):  # slash-command scaffolding, not prose
                continue
            text = _REMINDER_RE.sub("", text).strip()  # drop injected harness context
            role = "user"
        elif t == "assistant":
            text, role = _assistant_text(msg.get("content")), "assistant"
        else:
            continue
        if not text:
            continue
        out.append({
            "role": role,
            "text": text,
            "timestamp": r.get("timestamp"),
            "session_id": r.get("sessionId"),
            "git_branch": r.get("gitBranch"),
        })
    return out


def reduce_file(path):
    records = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except ValueError:
                continue
    return reduce_records(records)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: python reduce_transcript.py <transcript.jsonl>", file=sys.stderr)
        sys.exit(2)
    # Windows consoles default to cp1252; transcripts contain non-ASCII (✓, em-dashes,
    # emoji). Force UTF-8 so the JSON dump never dies on an unencodable character.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(reduce_file(sys.argv[1]), ensure_ascii=False, indent=2))
