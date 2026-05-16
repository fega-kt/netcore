#!/bin/sh
set -e

COMMIT=$(git rev-parse HEAD 2>/dev/null || echo "unknown")
AUTHOR=$(git log -1 --pretty=format:'%an' 2>/dev/null || echo "unknown")
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
MESSAGE=$(git log -1 --pretty=format:'%s' 2>/dev/null || echo "unknown")
BUILD_TIME=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

docker build \
  --build-arg GIT_COMMIT="$COMMIT" \
  --build-arg GIT_AUTHOR="$AUTHOR" \
  --build-arg GIT_BRANCH="$BRANCH" \
  --build-arg GIT_MESSAGE="$MESSAGE" \
  --build-arg BUILD_TIME="$BUILD_TIME" \
  "$@" .
