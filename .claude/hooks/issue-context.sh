#!/usr/bin/env bash
# SessionStart: tells Claude which issue the current branch belongs to,
# or that it must pick/create one before touching the code.
set -uo pipefail

repo="${CLAUDE_PROJECT_DIR:-$PWD}"
branch="$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || echo '(detached HEAD)')"

if [[ "$branch" =~ ^[a-z]+/([0-9]+)- ]]; then
  n="${BASH_REMATCH[1]}"
  title="$(cd "$repo" && timeout 5 gh issue view "$n" --json title,state --jq '"\(.title) [\(.state)]"' 2>/dev/null)"
  msg="Current branch '$branch' belongs to issue #$n${title:+: $title}. Keep all changes scoped to that issue; open the PR with 'Closes #$n'."
else
  msg="Current branch is '$branch', which is not an issue branch. Project rule: all code changes, commits and pushes happen on a branch <type>/<issue>-<slug> linked to a GitHub issue (enforced by .claude/hooks/branch-guard.sh). Before editing, find or create the issue and branch with the gh skill."
fi

jq -n --arg m "$msg" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $m}}'
