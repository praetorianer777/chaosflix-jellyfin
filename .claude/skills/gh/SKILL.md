---
name: gh
description: Use the GitHub CLI (gh) for this repo — find or create issues, create issue branches, open and update PRs, check CI runs and releases. Use before starting any code change (every change needs an issue and an issue branch) and whenever GitHub issues, PRs, Actions or releases are involved.
---

# GitHub CLI (`gh`) for chaosflix-jellyfin

Repo: `praetorianer777/chaosflix-jellyfin`, default branch `main`.

## Rules

- Every change starts from a GitHub issue and lives on a branch `<type>/<issue>-<slug>`
  (`feat|fix|chore|docs|refactor|test|perf|ci|build|revert`, slug lowercase with dashes).
  `.claude/hooks/branch-guard.sh` blocks edits, commits and pushes anywhere else.
- Never push to `main`, never merge a PR yourself — the user merges.
- `gh` runs non-interactively here: always pass `--title`/`--body` (or `--body-file`),
  never rely on prompts or an editor. Prefer `--json … --jq …` for reading.
- Creating issues, PRs or comments is outward-facing: confirm with the user first
  unless they already asked for it.

## Preflight

```bash
gh auth status          # not logged in → ask the user to run: ! gh auth login
```

## 1. Find or create the issue

```bash
gh issue list --state open --json number,title,labels --jq '.[] | "#\(.number) \(.title)"'
gh issue list --search "audio in:title,body" --state all
gh issue view 42 --json number,title,body,state,comments
gh issue create --title "Audio missing after remux" --body "$(cat <<'EOF'
## Problem
…
## Expected
…
EOF
)"
```

`gh issue create` prints the issue URL; the number is its last path segment.

## 2. Create the issue branch

```bash
git fetch origin
gh issue develop 42 --name fix/42-audio-after-remux --base main --checkout
```

This links the branch to the issue on GitHub. If the branch already exists:
`gh issue develop --list 42`, then `git switch <branch>`.

## 3. Commit and push

Commit via `/commit`. Run `./run-tests.sh` (plugin build, all `*.Tests.csproj`, Playwright
suite in `e2e/`) and fix failures before pushing — the branch guard runs it on every
`git push` and blocks the push if it fails. Push only the issue branch:

```bash
./run-tests.sh
git push -u origin HEAD
```

## 4. Pull request

```bash
gh pr create --base main --title "fix: restore audio after remux" --body "$(cat <<'EOF'
Closes #42

## Summary
…

## Testing
…
EOF
)"
gh pr view --json number,url,state,reviewDecision,statusCheckRollup
gh pr checks --watch
gh pr edit --add-label bug
gh pr comment --body "…"
```

Always put `Closes #<issue>` in the PR body.

## Reading feedback and CI

```bash
gh pr view 17 --comments
gh api repos/{owner}/{repo}/pulls/17/comments --jq '.[] | "\(.path):\(.line) \(.body)"'
gh run list --branch "$(git branch --show-current)" --limit 5
gh run view <run-id> --log-failed
```

## Releases

Releases are built with `./release.sh` (commits and tags on `main`) — that is the
user's job, not part of issue work. Read-only:

```bash
gh release list --limit 5
gh release view v0.0.29
```

## Pitfalls

- `gh api` placeholders `{owner}`/`{repo}` resolve from the current repo; quote the path in fish.
- Output is paged when attached to a TTY; set `GH_PAGER=cat` if a command hangs.
- `gh pr create` fails if the branch is not pushed yet — push first.
