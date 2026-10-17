#!/usr/bin/env bash
set -euo pipefail

: "${DARKTABLE_IMAGE:?Set DARKTABLE_IMAGE to an immutable renderer image digest.}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT
chmod 0777 "$workspace"

(cd "$root" && sha256sum --check IMG_9406.CR2.sha256)

# Match the non-root, read-only Fargate filesystem before checking pixels.
MSYS_NO_PATHCONV=1 docker run --rm --read-only --entrypoint /bin/sh "$DARKTABLE_IMAGE" -eu -c '
  test "$(id -u)" = 65532
  workspace="$(mktemp -d /tmp/render/qualification.XXXXXX)"
  touch "$workspace/writable"
  rm "$workspace/writable"
  rmdir "$workspace"
'

declare -a jpeg_hashes=()
declare -a pixel_hashes=()
for xmp in historical-v1.xmp historical-v2.xmp historical-v3.xmp current.xmp; do
  name="${xmp%.xmp}"
  xmp_hash="$(sha256sum "$root/$xmp" | awk '{print $1}')"
  for size in 512 2048; do
  suffix=""
  test "$size" = 512 || suffix="-$size"
  output="$name$suffix.jpg"
  pixels="$name$suffix.pfm"
  MSYS_NO_PATHCONV=1 docker run --rm --read-only \
    --env HOME=/tmp/render --env XDG_CACHE_HOME=/tmp/render/cache --env XDG_CONFIG_HOME=/tmp/render/config \
    --entrypoint /opt/darktable/bin/darktable-cli \
    --volume "$root:/input:ro" \
    --volume "$workspace:/output" \
    "$DARKTABLE_IMAGE" \
    /input/IMG_9406.CR2 "/input/$xmp" "/output/$output" \
    --width "$size" --height "$size" --library :memory:

  test -s "$workspace/$output"
  MSYS_NO_PATHCONV=1 docker run --rm --read-only \
    --env HOME=/tmp/render --env XDG_CACHE_HOME=/tmp/render/cache --env XDG_CONFIG_HOME=/tmp/render/config \
    --entrypoint /opt/darktable/bin/darktable-cli \
    --volume "$root:/input:ro" \
    --volume "$workspace:/output" \
    "$DARKTABLE_IMAGE" \
    /input/IMG_9406.CR2 "/input/$xmp" "/output/$pixels" \
    --width "$size" --height "$size" --library :memory:

  test -s "$workspace/$pixels"
  node "$root/check-pfm.mjs" "$workspace/$pixels" "$size"
  node "$root/check-pfm.mjs" "$root/reference/$pixels" "$size"
  # The size bounds preserve aspect ratio; the approved PFM binds dimensions and pixels.
  if ! cmp --silent "$workspace/$pixels" "$root/reference/$pixels"; then
    printf 'Pixel reference mismatch: %s\n' "$pixels" >&2
    if test -n "${DARKTABLE_FAILURE_DIR:-}"; then
      node - "$workspace/$pixels" "$root/reference/$pixels" "$DARKTABLE_IMAGE" "$DARKTABLE_FAILURE_DIR" <<'NODE'
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const os = require('node:os');
const [candidate, reference, image, directory] = process.argv.slice(2);
const actual = fs.readFileSync(candidate);
const expected = fs.readFileSync(reference);
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
fs.mkdirSync(directory, { recursive: true });
fs.copyFileSync(candidate, path.join(directory, path.basename(candidate)));
fs.writeFileSync(path.join(directory, 'mismatch.json'), JSON.stringify({
  status: 'FAILED exact native comparison; diagnostic only, never qualification',
  image, file: path.basename(candidate), candidateSHA256: hash(actual), referenceSHA256: hash(expected),
  candidateBytes: actual.length, referenceBytes: expected.length,
  firstDifferentByte: actual.findIndex((value, index) => value !== expected[index]),
  host: { architecture: os.arch(), cpuModel: os.cpus()[0]?.model, logicalCPUs: os.cpus().length, availableParallelism: os.availableParallelism() },
}, null, 2) + '\n');
NODE
    fi
    exit 1
  fi
  output_hash="$(sha256sum "$workspace/$output" | awk '{print $1}')"
  pixel_hash="$(sha256sum "$workspace/$pixels" | awk '{print $1}')"
  jpeg_hashes+=("$output_hash")
  pixel_hashes+=("$pixel_hash")
  printf '%s size=%s xmp_sha256=%s output_sha256=%s pixel_sha256=%s bytes=%s\n' "$xmp" "$size" "$xmp_hash" "$output_hash" "$pixel_hash" "$(stat --format=%s "$workspace/$output")"
  done
done

test "$(printf '%s\n' "${jpeg_hashes[@]}" | sort --unique | wc --lines)" -eq 8
test "$(printf '%s\n' "${pixel_hashes[@]}" | sort --unique | wc --lines)" -eq 8
