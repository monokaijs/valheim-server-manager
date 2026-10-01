#!/usr/bin/env bash
# Mirror an already-published public GHCR image; never build or create a release.
set -euo pipefail

# Publishing always targets the user-approved account and project repository.
DOCKERHUB_IMAGE=monokaijs/valheim-server-manager

fail() { echo "$1" >&2; exit 1; }

if [[ ! "${RELEASE_VERSION:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  fail 'Version must be X.Y.Z.'
fi
if [[ ! "${IMAGE_DIGEST:-}" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  fail 'Source must be a full sha256 image digest.'
fi
if [[ ! "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then
  fail 'A GitHub owner/repository is required.'
fi
UPDATE_LATEST=${UPDATE_LATEST:-false}
if [[ "$UPDATE_LATEST" != true && "$UPDATE_LATEST" != false ]]; then
  fail 'update_latest must be true or false.'
fi
if [[ ! -f "${REGISTRY_AUTH_FILE:-}" ]]; then
  fail 'Docker Hub authentication file is missing.'
fi

repository=$(printf '%s' "$GITHUB_REPOSITORY" | tr '[:upper:]' '[:lower:]')
source="docker://ghcr.io/${repository}@${IMAGE_DIGEST}"
source_version=$(skopeo --override-os linux --override-arch amd64 inspect --no-creds --no-tags \
  --format '{{ index .Labels "org.opencontainers.image.version" }}' "$source")
if [[ "$source_version" != "$RELEASE_VERSION" ]]; then
  fail 'Pinned image version does not match the requested release.'
fi

# Validate before any writes, then recheck immediately before updating latest.
verify_latest() {
  local latest_digest
  latest_digest=$(skopeo --override-os linux --override-arch amd64 inspect --no-creds --no-tags \
    --format '{{.Digest}}' "docker://ghcr.io/${repository}:latest")
  if [[ "$latest_digest" != "$IMAGE_DIGEST" ]]; then
    fail 'GHCR latest differs from the pinned image; refusing to overwrite Docker Hub latest.'
  fi
}
if [[ "$UPDATE_LATEST" == true ]]; then verify_latest; fi

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mirror_tag() {
  local tag=$1
  # Keep the entire OCI index, including provenance, and fail on digest changes.
  skopeo copy --all --preserve-digests --src-no-creds \
    --dest-authfile "$REGISTRY_AUTH_FILE" --digestfile "$scratch/digest" \
    "$source" "docker://docker.io/${DOCKERHUB_IMAGE}:${tag}"
  if [[ "$(cat "$scratch/digest")" != "$IMAGE_DIGEST" ]]; then
    fail 'Mirrored digest differs from the source.'
  fi
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf 'Published `docker.io/%s:%s` at `%s`\n' "$DOCKERHUB_IMAGE" "$tag" "$IMAGE_DIGEST" >> "$GITHUB_STEP_SUMMARY"
  fi
}
mirror_tag "$RELEASE_VERSION"
mirror_tag "v${RELEASE_VERSION}"
if [[ "$UPDATE_LATEST" == true ]]; then
  verify_latest
  mirror_tag latest
fi
