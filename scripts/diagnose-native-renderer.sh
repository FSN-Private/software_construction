#!/usr/bin/env bash
set -Eeuo pipefail

: "${SOURCE_REVISION:?Set the verified release source revision.}"
[[ "$SOURCE_REVISION" =~ ^[a-f0-9]{40}$ ]] || { echo 'Expected a full source revision.' >&2; exit 2; }
directory=artifacts/native-renderer-diagnostics
mkdir -p "$directory"
docker load --input artifacts/release/render-worker.tar
export DARKTABLE_IMAGE="$(docker image inspect "platform-render-worker:$SOURCE_REVISION" --format '{{.Id}}')"

# This wrapper changes only the diagnostic container's libc CPU dispatch.
# GCC-generated darktable clones are separate and are not controlled by this tunable.
docker() {
  if test "$1" = run && test "$NATIVE_VARIANT" = libc-baseline; then
    shift
    command docker run --env GLIBC_TUNABLES=glibc.cpu.hwcaps=-AVX512F,-AVX512VL,-AVX2,-AVX,-FMA,-FMA4,-SSE4_1,-SSE4_2,-SSSE3 "$@"
  else
    command docker "$@"
  fi
}
export -f docker

failed=0
for NATIVE_VARIANT in baseline baseline-repeat libc-baseline; do
  export NATIVE_VARIANT
  export DARKTABLE_FAILURE_DIR="$directory/$NATIVE_VARIANT"
  if bash tests/rendering/corpus/run-native.sh > "$directory/$NATIVE_VARIANT.log" 2>&1; then
    printf '%s PASS\n' "$NATIVE_VARIANT" | tee -a "$directory/results.txt"
  else
    failed=1
    printf '%s FAIL\n' "$NATIVE_VARIANT" | tee -a "$directory/results.txt"
  fi
done
# A failed comparison stays failed; these experiments never qualify a deployment.
exit "$failed"
