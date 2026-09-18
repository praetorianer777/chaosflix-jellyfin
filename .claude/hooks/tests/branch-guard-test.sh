#!/usr/bin/env bash
# Tests for .claude/hooks/branch-guard.sh: the workflow rules it enforces
# (issue branches, no pushes to main, tests before a push) and the cases that
# have bypassed it before — quoted text, heredocs, command wrappers, worktrees.
#
# Runs against a throwaway clone with a stubbed run-tests.sh; nothing is built,
# pushed or tested for real.
set -u

SRC="$(cd "$(dirname "$0")/../../.." && pwd)"

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
git clone -q "$SRC" "$W/r" && cd "$W/r"
git switch -q main && cp -r "$SRC/.claude" . && cp "$SRC/run-tests.sh" .
export CLAUDE_PROJECT_DIR="$W/r"
G="git"; C="commit"
fail=0

check() { # expected tool arg label
  local exp="$1" tool="$2" arg="$3"
  local got
  got=$(jq -nc --arg t "$tool" --arg a "$arg" --arg cwd "$W/r" \
    'if $t=="Bash" then {tool_name:$t,cwd:$cwd,tool_input:{command:$a}} else {tool_name:$t,cwd:$cwd,tool_input:{file_path:$a}} end' \
    | .claude/hooks/branch-guard.sh | jq -r '.hookSpecificOutput.permissionDecision // empty')
  got=${got:-allow}
  if [[ "$got" == "$exp" ]]; then echo "ok    $exp  ${arg//$'\n'/⏎}"; else echo "FAIL  want $exp got $got  ${arg//$'\n'/⏎}"; fail=1; fi
}

echo "== on main"
check deny  Write "$W/r/README.md"
check allow Write "/tmp/x"
check allow Bash  "$G status"
check deny  Bash  "$G $C -m x"
check deny  Bash  "$G switch -c foo"
check deny  Bash  "$G switch -qc foo"
check deny  Bash  "$G checkout -qb foo"
check allow Bash  "$G merge --ff-only origin/main"
check allow Bash  "$G reset --hard origin/main"
check deny  Bash  "$G merge origin/main"
check deny  Bash  "$G merge --ff-only some-other-branch"
check deny  Bash  "$G reset --hard HEAD~1"
check allow Bash  "$G switch -qc fix/42-audio && $G $C -m x"
check allow Bash  "$G switch -c fix/42-audio && $G add -A && $G $C -m x"
check deny  Bash  "$G push origin main"
check deny  Bash  "$G push origin fix/42-audio:main"
check ask   Bash  "gh pr merge 3"
check ask   Bash  "GH_PAGER=cat gh pr merge 3"
check allow Bash  "echo $G $C"
check allow Bash  "bash -c \"echo x && $G add -A\""
check allow Bash  "cat > f <<'EOF'
text
$G add -A
EOF
echo done"
check deny  Bash  "cat > f <<'EOF'
text
EOF
$G add -A"
check deny  Bash  "echo a
$G $C -m x"
check allow Bash  "$G -C /tmp/other $C -m x"
check deny  Bash  "$G -C . $C -m x"
check deny  Bash  "time $G $C -m x"
check deny  Bash  "env FOO=1 $G push origin main"
check deny  Bash  "sudo $G $C -m x"
check ask   Bash  "time gh pr merge 3"

git switch -qc fix/1-test
echo "== on fix/1-test"
check allow Write "$W/r/README.md"
check allow Bash  "$G checkout README.md && $G $C -m x"
check deny  Bash  "$G checkout main && $G $C -m x"
check deny  Bash  "$G push origin main"

printf '#!/bin/sh\necho stub ok\n' > run-tests.sh; chmod +x run-tests.sh
check allow Bash  "$G push -u origin HEAD"
printf '#!/bin/sh\necho "Failed: MemoryCacheTests.Expiry"; exit 1\n' > run-tests.sh
check deny  Bash  "$G push -u origin HEAD"

true

# ── Worktrees (#27) ──────────────────────────────────────
W2=$(mktemp -d)
trap 'rm -rf "$W" "$W2"' EXIT
git clone -q "$SRC" "$W2/r" && cd "$W2/r"
git switch -q main && cp -r "$SRC/.claude" . && cp "$SRC/run-tests.sh" .
export CLAUDE_PROJECT_DIR="$W2/r"
G="git"; C="commit"
fail=0

# The worktree is a second checkout of the same repo, on a valid issue branch.
WT="$W2/r/.claude/worktrees/agent-x"
git worktree add -q -b fix/99-agent-work "$WT" >/dev/null 2>&1
printf '#!/bin/sh\necho worktree tests ok\n' > "$WT/run-tests.sh"; chmod +x "$WT/run-tests.sh"
printf '#!/bin/sh\necho MAIN CHECKOUT TESTS RAN; exit 1\n' > "$W2/r/run-tests.sh"; chmod +x "$W2/r/run-tests.sh"

check() { # expected tool arg cwd
  local exp="$1" tool="$2" arg="$3" cwd="$4" got
  got=$(jq -nc --arg t "$tool" --arg a "$arg" --arg cwd "$cwd" \
    'if $t=="Bash" then {tool_name:$t,cwd:$cwd,tool_input:{command:$a}} else {tool_name:$t,cwd:$cwd,tool_input:{file_path:$a}} end' \
    | "$W2/r/.claude/hooks/branch-guard.sh" | jq -r '.hookSpecificOutput.permissionDecision // empty')
  got=${got:-allow}
  if [[ "$got" == "$exp" ]]; then echo "ok    $exp  ${arg//$'\n'/ } [cwd=${cwd##*/}]"
  else echo "FAIL  want $exp got $got  ${arg//$'\n'/ } [cwd=${cwd##*/}]"; fail=1; fi
}

echo "== worktree on issue branch, main checkout on main"
check allow Write "$WT/README.md" "$WT"
check deny  Write "$W2/r/README.md" "$W2/r"
check allow Bash  "$G add -A" "$WT"
check deny  Bash  "$G add -A" "$W2/r"
check allow Bash  "$G $C -m x" "$WT"
check deny  Bash  "$G push origin main" "$WT"
# The gate must run the worktree's own script: the main checkout's copy fails.
check allow Bash  "$G push -u origin HEAD" "$WT"

echo "== worktree switched to main"
git -C "$WT" switch -q main 2>/dev/null || git -C "$WT" checkout -q --detach
check deny Bash "$G $C -m x" "$WT"

echo "== -C into a nested worktree is judged by that worktree"
git -C "$WT" switch -q fix/99-agent-work
check allow Bash "$G -C $WT $C -m x" "$W2/r"
check allow Bash "$G -C $WT add -A" "$W2/r"
git -C "$WT" switch -q main 2>/dev/null || git -C "$WT" checkout -q --detach
check deny  Bash "$G -C $WT $C -m x" "$W2/r"

echo "== unrelated repo is none of our business"
OTHER="$W2/other"; mkdir -p "$OTHER" && git -C "$OTHER" init -q && git -C "$OTHER" commit -q --allow-empty -m init
check allow Write "$OTHER/file.txt" "$OTHER"
check allow Bash  "$G $C -m x" "$OTHER"

git -C "$W2/r" worktree remove --force "$WT" >/dev/null 2>&1

if (( fail )); then
  echo "❌ branch-guard tests failed"
  exit 1
fi
echo "✅ branch-guard tests passed"
