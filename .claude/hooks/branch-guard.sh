#!/usr/bin/env bash
# PreToolUse on Edit|Write|NotebookEdit|Bash: all work happens on an issue
# branch named <type>/<issue>-<slug>, never on main, and nothing is pushed
# unless ./run-tests.sh passes.
set -uo pipefail

BRANCH_RE='^(feat|fix|chore|docs|refactor|test|perf|ci|build|revert)/[0-9]+-[a-z0-9][a-z0-9._-]*$'
HINT="Work only on issue branches named <type>/<issue>-<slug> (e.g. fix/42-audio-regression). Find or create the GitHub issue first (see the gh skill), then: gh issue develop <N> --name <type>/<N>-<slug> --base main --checkout"

input="$(cat)"
tool="$(jq -r '.tool_name // empty' <<< "$input")"
project="$(realpath -m "${CLAUDE_PROJECT_DIR:-$(jq -r '.cwd // empty' <<< "$input")}")"
cwd="$(jq -r '.cwd // empty' <<< "$input")"
cwd="${cwd:-$project}"

# Worktrees are separate checkouts of this repo with their own branch and their
# own copy of run-tests.sh, so the checkout is resolved from what is being acted
# on, not from the project directory. They share a common git dir, which is what
# tells a worktree of this repo apart from an unrelated repo on disk.
# rev-parse prints the common dir relative to its own working directory, so it
# is resolved there rather than wherever this hook happens to run.
git_common_dir() { (cd "$1" 2>/dev/null && realpath -m "$(git rev-parse --git-common-dir 2>/dev/null)"); }
project_git_dir="$(git_common_dir "$project")"

checkout_for() {
  local dir="$1" top common
  top="$(git -C "$dir" rev-parse --show-toplevel 2>/dev/null)" || return 1
  common="$(git_common_dir "$dir")"
  [[ "$common" == "$project_git_dir" ]] || return 1
  printf '%s' "$top"
}

decide() {
  jq -n --arg d "$1" --arg r "$2" '{hookSpecificOutput: {hookEventName: "PreToolUse", permissionDecision: $d, permissionDecisionReason: $r}}'
  exit 0
}
deny() { decide deny "$1"; }
ask() { decide ask "$1"; }

valid() { [[ "$1" =~ $BRANCH_RE ]]; }

# True for `git merge --ff-only <upstream>` / `git reset --hard <upstream>` where
# <upstream> is exactly the tracking branch of the checked-out branch.
syncs_with_upstream() {
  local repo="$1" branch="$2"; shift 2
  local upstream target="" mode=0
  upstream="$(git -C "$repo" rev-parse --abbrev-ref --symbolic-full-name "$branch@{upstream}" 2>/dev/null)" || return 1
  [[ -n "$upstream" ]] || return 1
  for a in "$@"; do
    case "$a" in
      --ff-only|--hard) mode=1 ;;
      -*) return 1 ;;
      *) [[ -n "$target" ]] && return 1; target="$a" ;;
    esac
  done
  (( mode )) && [[ "$target" == "$upstream" ]]
}
is_branch() { git -C "$repo" show-ref --verify --quiet "refs/heads/$1"; }

run_tests() {
  local log
  log="$(mktemp)"
  # run-tests.sh gives each checkout its own stack, so gates running in
  # parallel worktrees do not collide (#95).
  if ! (cd "$repo" && ./run-tests.sh) > "$log" 2>&1; then
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
    # Files outside this repo (scratchpad, memory) are not project work.
    repo="$(checkout_for "$(dirname "$path")")" || exit 0
    branch="$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || echo '(detached HEAD)')"
    valid "$branch" || deny "Refusing to edit $path on branch '$branch'. $HINT"
    ;;

  Bash)
    cmd="$(jq -r '.tool_input.command // empty' <<< "$input")"
    [[ -n "$cmd" ]] || exit 0
    repo="$(checkout_for "$cwd")" || exit 0
    branch="$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || echo '(detached HEAD)')"

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
      # Strip env assignments and wrappers (time git push, env -i git commit, ...).
      k=0
      while [[ "${t[k]:-}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] \
        || [[ "${t[k]:-}" =~ ^(time|command|exec|nohup|nice|stdbuf|env|sudo|doas)$ ]]; do
        [[ "${t[k]:-}" == env ]] && while [[ "${t[k+1]:-}" == -* ]]; do ((k++)); done
        ((k++))
      done
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
      # -C into another checkout is judged by that checkout's branch. Worktrees
      # live under .claude/worktrees inside the repo directory, so the path
      # alone does not say which checkout it belongs to — ask git.
      target_repo="$(checkout_for "$target")" || continue
      if [[ "$target_repo" != "$repo" ]]; then
        repo="$target_repo"
        branch="$(git -C "$repo" symbolic-ref --quiet --short HEAD 2>/dev/null || echo '(detached HEAD)')"
      fi
      sub="${t[i]:-}"
      # Redirections are not arguments of the command: "git merge --ff-only
      # origin/main 2>&1" used to look like a merge with two targets, and every
      # rule that inspects the argument list was reading them (#76).
      args=()
      skip_operand=0
      for a in "${t[@]:i+1}"; do
        if (( skip_operand )); then skip_operand=0; continue; fi
        case "$a" in
          [0-9]*'>'*|'>'*|'<'*)
            [[ "$a" =~ (\>|\<)$ ]] && skip_operand=1
            continue
            ;;
        esac
        args+=("$a")
      done

      case "$sub" in
        checkout|switch)
          created=0
          for ((j = 0; j < ${#args[@]}; j++)); do
            # A short-flag cluster counts too: -qc, -qb, --quiet -c, ...
            if [[ "${args[j]}" =~ ^-[a-zA-Z]*[bBcC]$ ]]; then
              new="${args[j+1]:-}"
              valid "$new" || deny "Branch name '$new' is not allowed. $HINT"
              branch="$new"
              created=1
            fi
          done
          if (( !created )); then
            # The branch is the first argument that names one: with "git checkout
            # -q main" only args[0] was looked at, so the flag hid the branch and
            # everything after it in the same command was judged against the
            # branch we were on before (#76).
            for a in "${args[@]}"; do
              [[ "$a" == -* ]] && continue
              if is_branch "$a"; then branch="$a"; fi
              break
            done
          fi
          ;;
        branch)
          if [[ "${#args[@]}" -ge 1 && "${args[0]}" != -* ]]; then
            valid "${args[0]}" || deny "Branch name '${args[0]}' is not allowed. $HINT"
          fi
          ;;
        add|mv|rm|restore|apply|commit|merge|rebase|cherry-pick|revert|reset|am)
          # Catching up with the remote is not working on main: refusing it is
          # what left main behind after every merged PR, until a release was cut
          # from stale code (#64). Only a move onto the branch's own upstream is
          # allowed, and only as a fast-forward or a reset to exactly that ref.
          if [[ "$sub" == merge || "$sub" == reset ]] && syncs_with_upstream "$repo" "$branch" "${args[@]}"; then
            continue
          fi
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
