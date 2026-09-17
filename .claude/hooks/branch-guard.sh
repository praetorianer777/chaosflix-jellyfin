#!/usr/bin/env bash
# PreToolUse on Edit|Write|NotebookEdit|Bash: all work happens on an issue
# branch named <type>/<issue>-<slug>, never on main, and nothing is pushed
# unless ./run-tests.sh passes.
set -uo pipefail

BRANCH_RE='^(feat|fix|chore|docs|refactor|test|perf|ci|build|revert)/[0-9]+-[a-z0-9][a-z0-9._-]*$'
HINT="Work only on issue branches named <type>/<issue>-<slug> (e.g. fix/42-audio-regression). Find or create the GitHub issue first (see the gh skill), then: gh issue develop <N> --name <type>/<N>-<slug> --base main --checkout"

input="$(cat)"
tool="$(jq -r '.tool_name // empty' <<< "$input")"
repo="$(realpath -m "${CLAUDE_PROJECT_DIR:-$(jq -r '.cwd // empty' <<< "$input")}")"
cwd="$(jq -r '.cwd // empty' <<< "$input")"
cwd="${cwd:-$repo}"

decide() {
  jq -n --arg d "$1" --arg r "$2" '{hookSpecificOutput: {hookEventName: "PreToolUse", permissionDecision: $d, permissionDecisionReason: $r}}'
  exit 0
}
deny() { decide deny "$1"; }
ask() { decide ask "$1"; }

branch="$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || echo '(detached HEAD)')"
valid() { [[ "$1" =~ $BRANCH_RE ]]; }
is_branch() { git -C "$repo" show-ref --verify --quiet "refs/heads/$1"; }

run_tests() {
  local log
  log="$(mktemp)"
  if ! "$repo/run-tests.sh" > "$log" 2>&1; then
    local tail_out
    tail_out="$(tail -n 60 "$log")"
    rm -f "$log"
    deny "Push blocked: ./run-tests.sh failed. Fix the failures, commit, and push again.
$tail_out"
  fi
  rm -f "$log"
}

case "$tool" in
  Edit|Write|NotebookEdit)
    path="$(jq -r '.tool_input.file_path // .tool_input.notebook_path // empty' <<< "$input")"
    [[ -n "$path" ]] || exit 0
    path="$(realpath -m "$path")"
    # Files outside the repo (scratchpad, memory) are not project work.
    [[ "$path" == "$repo"/* ]] || exit 0
    valid "$branch" || deny "Refusing to edit $path on branch '$branch'. $HINT"
    ;;

  Bash)
    cmd="$(jq -r '.tool_input.command // empty' <<< "$input")"
    [[ -n "$cmd" ]] || exit 0

    # Heredoc bodies and quoted strings are data (commit messages, issue
    # bodies), not commands; blank them before splitting into segments.
    segments="$(perl -0pe '
      s/(<<-?\s*([\x27"]?)(\w+)\2[^\n]*\n).*?^\s*\3[ \t]*$/$1/gms;
      s/"(?:[^"\\]|\\.)*"/Q/gs;
      s/\x27[^\x27]*\x27/Q/gs;
      s/\s*(?:&&|\|\||;|\||\n)\s*/\n/g;
    ' <<< "$cmd")"

    pushing=0
    # Walk the segments in order so "git switch -c fix/1-x && git commit"
    # is judged against the branch the commit will actually land on.
    while IFS= read -r seg; do
      read -ra t <<< "$seg"
      k=0
      while [[ "${t[k]:-}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; do ((k++)); done
      t=("${t[@]:k}")
      (( ${#t[@]} )) || continue

      if [[ "${t[0]}" == "gh" ]]; then
        [[ "${t[1]:-} ${t[2]:-}" == "pr merge" ]] && ask "Merging a PR is the user's decision."
        continue
      fi
      [[ "${t[0]}" == "git" ]] || continue

      target="$cwd"
      i=1
      while [[ "${t[i]:-}" == -* ]]; do
        [[ "${t[i]}" == -C ]] && target="$(cd "$cwd" 2>/dev/null && realpath -m "${t[i+1]:-.}")"
        [[ "${t[i]}" == -C || "${t[i]}" == -c ]] && ((i++))
        ((i++))
      done
      [[ "$target" == "$repo" || "$target" == "$repo"/* ]] || continue
      sub="${t[i]:-}"
      args=("${t[@]:i+1}")

      case "$sub" in
        checkout|switch)
          created=0
          for ((j = 0; j < ${#args[@]}; j++)); do
            if [[ "${args[j]}" =~ ^-(b|B|c|C)$ ]]; then
              new="${args[j+1]:-}"
              valid "$new" || deny "Branch name '$new' is not allowed. $HINT"
              branch="$new"
              created=1
            fi
          done
          if (( !created )) && [[ "${#args[@]}" -ge 1 ]] && is_branch "${args[0]}"; then
            branch="${args[0]}"
          fi
          ;;
        branch)
          if [[ "${#args[@]}" -ge 1 && "${args[0]}" != -* ]]; then
            valid "${args[0]}" || deny "Branch name '${args[0]}' is not allowed. $HINT"
          fi
          ;;
        add|mv|rm|restore|apply|commit|merge|rebase|cherry-pick|revert|reset|am)
          valid "$branch" || deny "Refusing 'git $sub' on branch '$branch'. $HINT"
          ;;
        push)
          for a in "${args[@]}"; do
            [[ "$a" =~ (^|:|/)(main|master)$ || "$a" == --all || "$a" == --mirror ]] \
              && deny "Pushing to main is not allowed; push the issue branch and open a PR instead."
          done
          valid "$branch" || deny "Refusing to push from branch '$branch'. $HINT"
          pushing=1
          ;;
      esac
    done <<< "$segments"

    (( pushing )) && run_tests
    ;;
esac
exit 0
