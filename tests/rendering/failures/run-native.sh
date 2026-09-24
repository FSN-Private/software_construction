#!/usr/bin/env bash
set -euo pipefail

: "${DARKTABLE_IMAGE:?Set DARKTABLE_IMAGE to an immutable renderer image digest.}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT
chmod 0777 "$workspace"

expect_failure() {
  local name="$1"
  shift
  if "$@"; then
    echo "Expected native rendering failure: $name" >&2
    exit 1
  fi
  test ! -s "$workspace/$name.jpg"
}

expect_failure missing-source docker run --rm --volume "$root/corpus:/input:ro" --volume "$workspace:/work" "$DARKTABLE_IMAGE" /input/not-present.CR2 /input/current.xmp /work/missing-source.jpg --width 512 --height 512 --library :memory:

printf '<x:xmpmeta>' > "$workspace/malformed.xmp"
expect_failure malformed-xmp docker run --rm --volume "$root/corpus:/input:ro" --volume "$workspace:/work" "$DARKTABLE_IMAGE" /input/IMG_9406.CR2 /work/malformed.xmp /work/malformed-xmp.jpg --width 512 --height 512 --library :memory:

printf 'not an image' > "$workspace/unsupported.raw"
expect_failure unsupported-source docker run --rm --volume "$root/corpus:/input:ro" --volume "$workspace:/work" "$DARKTABLE_IMAGE" /work/unsupported.raw /input/current.xmp /work/unsupported-source.jpg --width 512 --height 512 --library :memory:

if timeout 0.01s docker run --rm --volume "$root/corpus:/input:ro" --volume "$workspace:/work" "$DARKTABLE_IMAGE" /input/IMG_9406.CR2 /input/current.xmp /work/timeout.jpg --width 512 --height 512 --library :memory:; then
  echo 'Expected native rendering timeout.' >&2
  exit 1
fi
test ! -s "$workspace/timeout.jpg"

printf 'Native renderer failure checks passed for %s\n' "$DARKTABLE_IMAGE"
