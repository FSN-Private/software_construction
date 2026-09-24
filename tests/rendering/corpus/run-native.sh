#!/usr/bin/env bash
set -euo pipefail

: "${DARKTABLE_IMAGE:?Set DARKTABLE_IMAGE to an immutable renderer image digest.}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT
chmod 0777 "$workspace"

(cd "$root" && sha256sum --check IMG_9406.CR2.sha256)

declare -a hashes=()
for xmp in historical-v1.xmp historical-v2.xmp historical-v3.xmp current.xmp; do
  output="${xmp%.xmp}.jpg"
  MSYS_NO_PATHCONV=1 docker run --rm \
    --entrypoint /opt/darktable/bin/darktable-cli \
    --volume "$root:/input:ro" \
    --volume "$workspace:/output" \
    "$DARKTABLE_IMAGE" \
    /input/IMG_9406.CR2 "/input/$xmp" "/output/$output" \
    --width 512 --height 512 --library :memory:

  test -s "$workspace/$output"
  xmp_hash="$(sha256sum "$root/$xmp" | awk '{print $1}')"
  output_hash="$(sha256sum "$workspace/$output" | awk '{print $1}')"
  hashes+=("$output_hash")
  printf '%s xmp_sha256=%s output_sha256=%s bytes=%s\n' "$xmp" "$xmp_hash" "$output_hash" "$(stat --format=%s "$workspace/$output")"
done

test "$(printf '%s\n' "${hashes[@]}" | sort --unique | wc --lines)" -eq 4
