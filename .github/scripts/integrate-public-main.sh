#!/usr/bin/env bash
set -euo pipefail

: "${PUBLIC_REPOSITORY_URL:?Set PUBLIC_REPOSITORY_URL}"

if [[ -n "$(git status --porcelain)" ]]; then
  echo "::error::Integration requires a clean working tree"
  exit 1
fi

git fetch --no-tags origin \
  refs/heads/main:refs/remotes/origin/main
# The merge App is installed only on the private repository. Read public Git
# anonymously, even when gh has configured a GitHub-wide credential helper.
GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/bin/false git \
  -c credential.helper= -c credential.https://github.com.helper= \
  -c http.extraHeader= -c "http.${PUBLIC_REPOSITORY_URL}.extraHeader=" \
  fetch --no-tags "$PUBLIC_REPOSITORY_URL" \
  refs/heads/main:refs/remotes/public/main
git switch --detach refs/remotes/origin/main
public_commit=$(git rev-parse refs/remotes/public/main)

if git merge-base --is-ancestor "$public_commit" HEAD; then
  echo "Private main already contains the current public main"
  exit 0
fi

if git merge-base HEAD "$public_commit" >/dev/null; then
  if ! git merge --no-ff -m "Merge public main into private main" "$public_commit"; then
    git merge --abort
    echo "::error::Resolve the merge conflict in the private repository, then retry integration"
    exit 1
  fi
else
  # A fresh public root can be linked automatically only when both trees match.
  # Keeping the private tree in this case preserves the full private history.
  if ! git diff --quiet HEAD "$public_commit" --; then
    echo "::error::Initial public and private trees differ; review and integrate them privately before retrying"
    exit 1
  fi
  git merge --allow-unrelated-histories --no-ff -s ours \
    -m "Connect public release history to private main" "$public_commit"
fi

# A concurrent private push is rejected by Git; it is never overwritten.
if ! git push origin HEAD:refs/heads/main; then
  echo "::error::Private main was not updated; check permissions or concurrent changes and retry integration"
  exit 1
fi
