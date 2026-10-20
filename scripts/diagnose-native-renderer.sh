#!/usr/bin/env bash
set -Eeuo pipefail

: "${SOURCE_REVISION:?Set the verified release source revision.}"
[[ "$SOURCE_REVISION" =~ ^[a-f0-9]{40}$ ]] || { echo 'Expected a full source revision.' >&2; exit 2; }
directory=artifacts/native-renderer-diagnostics
mkdir -p "$directory"
docker load --input artifacts/release/render-worker.tar
export DARKTABLE_IMAGE="$(docker image inspect "platform-render-worker:$SOURCE_REVISION" --format '{{.Id}}')"
node --input-type=module - "$directory/host.json" <<'NODE'
import fs from 'node:fs';
import os from 'node:os';
fs.writeFileSync(process.argv[2], JSON.stringify({ sourceRevision: process.env.SOURCE_REVISION, image: process.env.DARKTABLE_IMAGE,
  cpuModel: os.cpus()[0]?.model, logicalCPUs: os.cpus().length, availableParallelism: os.availableParallelism(),
  cpuVendor: fs.existsSync('/proc/cpuinfo') ? /^vendor_id\s*:\s*(\S+)/m.exec(fs.readFileSync('/proc/cpuinfo', 'utf8'))?.[1] ?? 'unknown' : 'unknown' }, null, 2));
NODE

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
if test "$failed" = 1; then
  NATIVE_VARIANT=baseline bash scripts/collect-native-renderer-candidate.sh > "$directory/candidate-collection.log" 2>&1
fi
# A failed comparison stays failed; these experiments never qualify a deployment.
exit "$failed"
