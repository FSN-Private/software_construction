#!/usr/bin/env bash
set -euo pipefail

: "${DARKTABLE_IMAGE:?Set DARKTABLE_IMAGE to an immutable renderer image digest.}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT
chmod 0777 "$workspace"

(cd "$root" && sha256sum --check IMG_9406.CR2.sha256)

declare -a jpeg_hashes=()
declare -a pixel_hashes=()
for xmp in historical-v1.xmp historical-v2.xmp historical-v3.xmp current.xmp; do
  name="${xmp%.xmp}"
  output="$name.jpg"
  pixels="$name.pfm"
  MSYS_NO_PATHCONV=1 docker run --rm \
    --entrypoint /opt/darktable/bin/darktable-cli \
    --volume "$root:/input:ro" \
    --volume "$workspace:/output" \
    "$DARKTABLE_IMAGE" \
    /input/IMG_9406.CR2 "/input/$xmp" "/output/$output" \
    --width 512 --height 512 --library :memory:

  test -s "$workspace/$output"
  MSYS_NO_PATHCONV=1 docker run --rm \
    --entrypoint /opt/darktable/bin/darktable-cli \
    --volume "$root:/input:ro" \
    --volume "$workspace:/output" \
    "$DARKTABLE_IMAGE" \
    /input/IMG_9406.CR2 "/input/$xmp" "/output/$pixels" \
    --width 512 --height 512 --library :memory:

  test -s "$workspace/$pixels"
  test "$(sed -n '1p' "$workspace/$pixels")" = PF
  test "$(sed -n '2p' "$workspace/$pixels")" = '512 512'
  cmp --silent "$workspace/$pixels" "$root/reference/$pixels"
  xmp_hash="$(sha256sum "$root/$xmp" | awk '{print $1}')"
  output_hash="$(sha256sum "$workspace/$output" | awk '{print $1}')"
  pixel_hash="$(sha256sum "$workspace/$pixels" | awk '{print $1}')"
  jpeg_hashes+=("$output_hash")
  pixel_hashes+=("$pixel_hash")
  printf '%s xmp_sha256=%s output_sha256=%s pixel_sha256=%s bytes=%s\n' "$xmp" "$xmp_hash" "$output_hash" "$pixel_hash" "$(stat --format=%s "$workspace/$output")"
done

test "$(printf '%s\n' "${jpeg_hashes[@]}" | sort --unique | wc --lines)" -eq 4
test "$(printf '%s\n' "${pixel_hashes[@]}" | sort --unique | wc --lines)" -eq 4
