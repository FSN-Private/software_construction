import { createHash } from 'node:crypto';
import { readFileSync, statSync } from 'node:fs';
import { basename, resolve } from 'node:path';

export const manifestVersion = 'v1';

export function sha256File(path) {
  return `sha256:${createHash('sha256').update(readFileSync(path)).digest('hex')}`;
}

export function createReleaseManifest({ releaseId, sourceRevision, artifacts, sbom, provenance, compatibility }) {
  if (!/^v[0-9A-Za-z._-]+$/.test(releaseId)) {
    throw new Error('releaseId must be a versioned v-prefixed identifier.');
  }
  if (!/^[a-f0-9]{40,64}$/.test(sourceRevision)) {
    throw new Error('sourceRevision must be a full lowercase Git SHA.');
  }
  if (!Array.isArray(artifacts) || artifacts.length === 0) {
    throw new Error('At least one immutable artifact is required.');
  }
  if (!sbom || !provenance || !compatibility) {
    throw new Error('SBOM, provenance, and compatibility evidence are required.');
  }

  const evidence = [
    ['sbom', sbom],
    ['provenance', provenance],
  ].map(([kind, path]) => ({ kind, name: basename(path), digest: sha256File(path) }));

  return {
    manifestVersion,
    releaseId,
    sourceRevision,
    artifacts: artifacts.map((path) => ({ name: basename(path), digest: sha256File(path), bytes: statSync(path).size })),
    evidence,
    compatibility,
    promotionRule: 'same-digest-with-separate-audited-configuration',
  };
}

export function verifyReleaseManifest(manifest, baseDirectory) {
  if (manifest.manifestVersion !== manifestVersion || manifest.promotionRule !== 'same-digest-with-separate-audited-configuration') {
    throw new Error('Unsupported or mutable release manifest.');
  }
  if (manifest.compatibility?.rollbackAllowed !== true) {
    throw new Error('Rollback is refused unless compatibility evidence explicitly allows it.');
  }
  for (const entry of [...manifest.artifacts, ...manifest.evidence]) {
    const actualDigest = sha256File(resolve(baseDirectory, entry.name));
    if (actualDigest !== entry.digest) {
      throw new Error(`Digest verification failed for ${entry.name}.`);
    }
  }
  return true;
}
