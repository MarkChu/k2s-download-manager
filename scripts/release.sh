#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$REPO_ROOT"

usage() {
  cat <<-EOF
Usage: $(basename "$0") [TAG] [--local] [--yes]

Creates a git tag (or uses provided TAG), pushes it to origin, and triggers CI.
If --local is provided, also builds and pushes the Docker image to GHCR locally
(requires GHCR_PAT and optional GHCR_USERNAME environment variables).

Examples:
  # auto-increment patch version and push tag (triggers GitHub Actions)
  ./scripts/release.sh

  # create a specific tag and push
  ./scripts/release.sh v1.2.3

  # create tag and also build+push image locally
  GHCR_PAT=... GHCR_USERNAME=markchu ./scripts/release.sh v1.2.3 --local

EOF
}

AUTO_PUSH_LOCAL=0
CONFIRM=1
TAG_ARG=""

for arg in "$@"; do
  case "$arg" in
    --local) AUTO_PUSH_LOCAL=1; shift;;
    --yes) CONFIRM=0; shift;;
    -h|--help) usage; exit 0;;
    *) if [ -z "$TAG_ARG" ]; then TAG_ARG="$arg"; else echo "Unknown arg: $arg"; usage; exit 1; fi; shift;;
  esac
done

get_latest_tag() {
  # prefer semantic sort if available
  tag=$(git tag --sort=-v:refname 2>/dev/null | head -n1 || true)
  if [ -z "$tag" ]; then
    echo "v0.0.0"
  else
    echo "$tag"
  fi
}

bump_patch() {
  local t=$1
  local prefix=""
  if [[ $t == v* ]]; then prefix="v"; t=${t#v}; fi
  IFS='.' read -r major minor patch <<<"$t"
  major=${major:-0}
  minor=${minor:-0}
  patch=${patch:-0}
  patch=$((patch + 1))
  echo "${prefix}${major}.${minor}.${patch}"
}

if [ -n "$TAG_ARG" ]; then
  NEW_TAG="$TAG_ARG"
else
  LATEST=$(get_latest_tag)
  NEW_TAG=$(bump_patch "$LATEST")
fi

echo "Preparing release: $NEW_TAG"

if [ $CONFIRM -eq 1 ]; then
  read -p "Proceed and push tag $NEW_TAG to origin? [y/N] " yn
  case "$yn" in
    [Yy]*) ;;
    *) echo "Aborted."; exit 1;;
  esac
fi

git fetch --tags origin
if git rev-parse "$NEW_TAG" >/dev/null 2>&1; then
  echo "Tag $NEW_TAG already exists locally. Aborting."; exit 1
fi

git tag -a "$NEW_TAG" -m "Release $NEW_TAG"
git push origin "$NEW_TAG"

echo "Pushed tag $NEW_TAG to origin."

if [ "$AUTO_PUSH_LOCAL" -eq 1 ]; then
  echo "Local GHCR push requested."

  if [ -z "${GHCR_PAT:-}" ]; then
    echo "Environment variable GHCR_PAT is not set. Cannot login to GHCR." >&2
    exit 1
  fi

  # determine owner/repo from git remote
  remote_url=$(git remote get-url origin 2>/dev/null || true)
  owner="markchu"
  repo="k2s-download-manager"
  if [ -n "$remote_url" ]; then
    owner=$(echo "$remote_url" | sed -E 's#.*[:/](.+)/(.+?)(\.git)?$#\1#' || true)
    repo=$(echo "$remote_url" | sed -E 's#.*[:/](.+)/(.+?)(\.git)?$#\2#' | sed 's/\.git$//' || true)
  fi
  IMAGE_NAME="ghcr.io/${owner}/${repo}"

  echo "Logging in to ghcr.io as ${GHCR_USERNAME:-$owner}"
  echo "$GHCR_PAT" | docker login ghcr.io -u "${GHCR_USERNAME:-$owner}" --password-stdin

  echo "Building and pushing ${IMAGE_NAME}:${NEW_TAG} and :latest"
  docker buildx build --platform linux/amd64 -t "${IMAGE_NAME}:${NEW_TAG}" -t "${IMAGE_NAME}:latest" --push .
  echo "Docker image pushed to GHCR."
fi

echo "Done."
