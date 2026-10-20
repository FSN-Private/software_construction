#!/usr/bin/env bash
set -Eeuo pipefail

: "${DARKTABLE_IMAGE:?Set the verified renderer image.}"
root="$PWD/tests/rendering/corpus"
workspace="$PWD/artifacts/native-renderer-diagnostics/candidate"
mkdir -p "$workspace"
chmod 0777 "$workspace"
(cd "$root" && sha256sum --check IMG_9406.CR2.sha256)
cp "$root/IMG_9406.CR2" "$workspace/original.raw"

# Candidate evidence only. This script does not approve references or qualify a release.
for name in historical-v1 historical-v2 historical-v3 current; do
  for size in 512 2048; do
    suffix=""
    test "$size" = 512 || suffix="-$size"
    MSYS_NO_PATHCONV=1 docker run --rm --read-only --cpus 1 --memory 2g \
      --env OMP_NUM_THREADS=1 \
      --env HOME=/tmp/render --env XDG_CACHE_HOME=/tmp/render/cache --env XDG_CONFIG_HOME=/tmp/render/config \
      --entrypoint /opt/darktable/bin/darktable-cli \
      --volume "$root:/input:ro" --volume "$workspace:/output" "$DARKTABLE_IMAGE" \
      /output/original.raw "/input/$name.xmp" "/output/$name$suffix.pfm" \
      --width "$size" --height "$size" --library :memory:
    node "$root/check-pfm.mjs" "$workspace/$name$suffix.pfm" "$size"
  done
done
rm "$workspace/original.raw"
node --input-type=module - "$workspace" <<'NODE'
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const directory = process.argv[2];
const files = ['historical-v1', 'historical-v2', 'historical-v3', 'current'].flatMap(name =>
  [512, 2048].map(size => {
    const file = `${name}${size === 512 ? '' : '-2048'}.pfm`;
    const data = fs.readFileSync(path.join(directory, file));
    return { file, bytes: data.length, sha256: crypto.createHash('sha256').update(data).digest('hex'),
      xmpSHA256: crypto.createHash('sha256').update(fs.readFileSync(`tests/rendering/corpus/${name}.xmp`)).digest('hex') };
  }));
fs.writeFileSync(path.join(directory, 'candidate-manifest.json'), JSON.stringify({
  status: 'UNAPPROVED candidate pixels; diagnostic only, never qualification',
  host: JSON.parse(fs.readFileSync('artifacts/native-renderer-diagnostics/host.json', 'utf8')), files,
}, null, 2));
NODE
