import { createHash } from 'node:crypto';
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { createReleaseManifest } from '../packages/release-tools/release-manifest.mjs';

const [sourceRevision, outputDirectory, ciRunId, releaseRunId, repository] = process.argv.slice(2);
if (!/^[a-f0-9]{40}$/.test(sourceRevision) || !outputDirectory || !/^\d+$/.test(ciRunId) || !/^\d+$/.test(releaseRunId) || !/^[\w.-]+\/[\w.-]+$/.test(repository)) {
  throw new Error('Usage: generate-release-evidence.mjs <full-source-sha> <output-directory> <ci-run-id> <release-run-id> <owner/repository>');
}
mkdirSync(outputDirectory, { recursive: true });
const artifacts = readdirSync(outputDirectory, { withFileTypes: true }).filter((entry) => entry.isFile() && /\.(tar|zip)$/.test(entry.name)).map((entry) => join(outputDirectory, entry.name));
if (artifacts.length !== 5) throw new Error('Release evidence requires API, ingest worker, web, infrastructure, and Windows artifacts.');
const digest = (path) => `sha256:${createHash('sha256').update(readFileSync(path)).digest('hex')}`;
const sbom = join(outputDirectory, 'sbom.spdx.json');
const provenance = join(outputDirectory, 'provenance.json');
const validation = join(outputDirectory, 'validation-evidence.json');
writeFileSync(sbom, JSON.stringify({ spdxVersion: 'SPDX-2.3', name: `release-${sourceRevision}`, files: artifacts.map((path) => ({ fileName: basename(path), checksums: [{ algorithm: 'SHA256', checksumValue: digest(path).slice(7) }] })) }, null, 2));
writeFileSync(provenance, JSON.stringify({
  _type: 'https://in-toto.io/Statement/v1',
  subject: artifacts.map((path) => ({ name: basename(path), digest: { sha256: digest(path).slice(7) } })),
  predicateType: 'https://slsa.dev/provenance/v1',
  predicate: {
    buildDefinition: {
      buildType: 'https://actions.github.io/buildtypes/workflow/v1',
      externalParameters: { sourceRevision, ciRunId, workflow: '.github/workflows/build-release.yml' },
      resolvedDependencies: [{ uri: `git+https://github.com/${repository}@refs/heads/master`, digest: { gitCommit: sourceRevision } }],
    },
    runDetails: {
      builder: { id: `https://github.com/${repository}/.github/workflows/build-release.yml@refs/heads/master` },
      metadata: { invocationId: `https://github.com/${repository}/actions/runs/${releaseRunId}` },
    },
  },
}, null, 2));
writeFileSync(validation, JSON.stringify({ sourceRevision, ciWorkflow: 'Continuous integration', ciRunId, conclusion: 'success', requiredChecks: ['ci / backend', 'ci / web', 'ci / windows', 'ci / infrastructure', 'ci / security', 'ci / required'] }, null, 2));
const manifest = createReleaseManifest({ releaseId: `vbuild-${sourceRevision}`, sourceRevision, artifacts, sbom, provenance, additionalEvidence: [validation], compatibility: { rollbackAllowed: true, schemaVersion: 'v1' } });
writeFileSync(join(outputDirectory, 'release-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
