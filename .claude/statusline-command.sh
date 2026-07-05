#!/usr/bin/env bash
# Claude Code status line: dir | branch | model | ctx% | 5h% | 7d%
input=$(cat)
printf '%s' "$input" | python -c "
import sys, json, os, subprocess, datetime, time, tempfile, hashlib

d = json.load(sys.stdin)

GREEN  = '\033[32m'
YELLOW = '\033[33m'
RED    = '\033[31m'
DIM    = '\033[2m'
RESET  = '\033[0m'

def color_for(pct):
    if pct is None:
        return DIM
    if pct >= 90:
        return RED
    if pct >= 70:
        return YELLOW
    return GREEN

def fmt_pct(label, pct):
    if pct is None:
        return None
    return f'{color_for(pct)}{label} {int(pct)}%{RESET}'

cwd = (d.get('workspace') or {}).get('current_dir') or d.get('cwd') or ''
model = (d.get('model') or {}).get('display_name', '')
dir_name = os.path.basename(cwd.rstrip('/\\\\')) if cwd else ''

# Branch is the only expensive part (git subprocess). Cache it per-cwd with a
# short TTL so rapid re-renders during heavy work skip git entirely, and a slow
# or locked git can never blank the bar — we fall back to the last cached value.
BRANCH_TTL = 10  # seconds

def read_cache(path):
    try:
        with open(path, 'r', encoding='utf-8') as f:
            return f.read().strip()
    except Exception:
        return None

def to_native(p):
    # MSYS '/c/foo' -> 'C:/foo' so native git.exe (no MSYS path mangling) can parse it
    if len(p) >= 3 and p[0] == '/' and p[2] == '/' and p[1].isalpha():
        return p[1].upper() + ':/' + p[3:]
    return p

def git_branch(cwd):
    cwd = to_native(cwd)
    try:
        r = subprocess.run(['git', '-C', cwd, 'rev-parse', '--abbrev-ref', 'HEAD'],
                           capture_output=True, text=True, timeout=0.5)
        if r.returncode != 0:
            return None
        name = r.stdout.strip()
        if name != 'HEAD':
            return name
        r = subprocess.run(['git', '-C', cwd, 'rev-parse', '--short', 'HEAD'],
                           capture_output=True, text=True, timeout=0.5)
        return r.stdout.strip() if r.returncode == 0 else None
    except Exception:
        return None

branch = ''
if cwd:
    key = hashlib.md5(cwd.encode('utf-8')).hexdigest()
    cache_path = os.path.join(tempfile.gettempdir(), 'cc-statusline-branch-' + key)
    fresh = False
    try:
        fresh = (time.time() - os.path.getmtime(cache_path)) < BRANCH_TTL
    except OSError:
        fresh = False
    if fresh:
        branch = read_cache(cache_path) or ''
    else:
        name = git_branch(cwd)
        if name is not None:
            branch = name
            try:
                with open(cache_path, 'w', encoding='utf-8') as f:
                    f.write(name)
            except Exception:
                pass
        else:
            # git failed/timed out — reuse stale cache rather than dropping branch
            branch = read_cache(cache_path) or ''

ctx = d.get('context_window') or {}
ctx_pct = ctx.get('used_percentage')
ctx_used_tokens = ctx.get('total_input_tokens')
ctx_window_size = ctx.get('context_window_size')

def fmt_tokens(n):
    if n is None:
        return ''
    if n >= 1_000_000:
        return f'{n/1_000_000:.1f}M'.replace('.0M', 'M')
    if n >= 1000:
        return f'{n/1000:.1f}k'.replace('.0k', 'k')
    return str(n)
rate = d.get('rate_limits') or {}
five_h = (rate.get('five_hour') or {}).get('used_percentage')
five_h_resets = (rate.get('five_hour') or {}).get('resets_at')
seven_d = (rate.get('seven_day') or {}).get('used_percentage')

def fmt_reset(epoch):
    if epoch is None:
        return ''
    try:
        return datetime.datetime.fromtimestamp(int(epoch)).strftime('%H:%M')
    except Exception:
        return ''

parts = []
if dir_name:
    parts.append(dir_name)
if branch:
    parts.append(branch)
if model:
    parts.append(model)
ctx_part = fmt_pct('ctx', ctx_pct)
if ctx_part:
    used_str = fmt_tokens(ctx_used_tokens)
    total_str = fmt_tokens(ctx_window_size)
    if used_str and total_str:
        ctx_part = f'{ctx_part} {DIM}({used_str}/{total_str}){RESET}'
    elif used_str:
        ctx_part = f'{ctx_part} {DIM}({used_str}){RESET}'
    parts.append(ctx_part)
five_part = fmt_pct('5h', five_h)
if five_part:
    reset_str = fmt_reset(five_h_resets)
    if reset_str:
        five_part = f'{five_part} {DIM}({reset_str}){RESET}'
    parts.append(five_part)
seven_part = fmt_pct('7d', seven_d)
if seven_part:
    parts.append(seven_part)

out = ' | '.join(parts)
if not out:
    out = dir_name or model or 'claude'
sys.stdout.write(out)
"
