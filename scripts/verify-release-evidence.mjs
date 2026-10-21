import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { verifyReleaseManifest } from '../packages/release-tools/release-manifest.mjs';
import { recoveryVerificationConfiguration, verifyRecoveryEvidence } from './recovery-release-evidence.mjs';

const [manifestPath, expectedSourceRevision, mode, expectedRepository, expectedReleaseRunId] = process.argv.slice(2);
if (!manifestPath || !/^[a-f0-9]{40}$/.test(expectedSourceRevision) || (expectedRepository && !/^[\w.-]+\/[\w.-]+$/.test(expectedRepository)) || (expectedReleaseRunId && !/^\d+$/.test(expectedReleaseRunId))) {
  throw new Error('Usage: verify-release-evidence.mjs <manifest-path> <full-source-sha> [--require-worker|--require-regional|--require-recovery] [owner/repository] [release-run-id]');
}

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
if (manifest.sourceRevision !== expectedSourceRevision) {
  throw new Error('Release manifest source revision does not match the requested revision.');
}
const names = manifest.artifacts.map(({ name }) => name).sort();
const legacy = ['api.tar', 'infrastructure.tar', 'platform-api-win-x64.zip', 'web.tar'];
const current = [...legacy, 'ingest-worker.tar', 'render-worker.tar'].sort();
const recovery = [...current, 'recovery-worker.tar'].sort();
const regional = [...current, 'regional-acceptance-client.zip'].sort();
const regionalRecovery = [...recovery, 'regional-acceptance-client.zip'].sort();
const isLegacyArtifactSet = JSON.stringify(names) === JSON.stringify(legacy);
if (![current, recovery, regional, regionalRecovery].map(set => JSON.stringify(set)).includes(JSON.stringify(names)) && !isLegacyArtifactSet) throw new Error('Release manifest contains an unsupported artifact set.');
if (['--require-worker', '--require-regional'].includes(mode) && (!names.includes('ingest-worker.tar') || !names.includes('render-worker.tar'))) throw new Error('Release manifest does not contain the required ingest and renderer worker artifacts.');
if (mode === '--require-regional' && !names.includes('regional-acceptance-client.zip')) throw new Error('Release manifest does not contain the required regional acceptance client.');
if (mode === '--require-recovery' && (!names.includes('recovery-worker.tar') || manifest.compatibility?.recoveryRequired !== true))
  throw new Error('Release manifest does not contain the required recovery worker artifact and qualification contract.');
verifyReleaseManifest(manifest, dirname(resolve(manifestPath)));
if (mode === '--require-recovery' || manifest.evidence.some(({ kind }) => kind === 'recovery-qualification'))
  verifyRecoveryEvidence(manifest, dirname(resolve(manifestPath)), recoveryVerificationConfiguration());
const provenanceEntry = manifest.evidence.filter(({ kind }) => kind === 'provenance');
if (provenanceEntry.length !== 1) throw new Error('Release manifest must contain one provenance statement.');
const validationEntry = manifest.evidence.filter(({ kind }) => kind === 'validation');
if (validationEntry.length !== 1) throw new Error('Release manifest must contain one CI validation statement.');
const baseDirectory = dirname(resolve(manifestPath));
const provenance = JSON.parse(readFileSync(resolve(baseDirectory, provenanceEntry[0].name), 'utf8'));
const validation = JSON.parse(readFileSync(resolve(baseDirectory, validationEntry[0].name), 'utf8'));
if (isLegacyArtifactSet && provenance._type === undefined) {
  if (provenance.predicateType !== 'https://slsa.dev/provenance/v1' || provenance.buildDefinition?.externalParameters?.sourceRevision !== expectedSourceRevision) throw new Error('Legacy provenance source binding is invalid.');
} else {
  if (!expectedRepository) throw new Error('Current provenance verification requires the expected repository.');
  const parameters = provenance.predicate?.buildDefinition?.externalParameters;
  const dependency = provenance.predicate?.buildDefinition?.resolvedDependencies?.[0];
  const subjects = new Map((provenance.subject ?? []).map(({ name, digest }) => [name, digest?.sha256]));
  const builder = provenance.predicate?.runDetails?.builder?.id?.match(/^https:\/\/github\.com\/([\w.-]+\/[\w.-]+)\/\.github\/workflows\/build-release\.yml@refs\/heads\/master$/);
  const invocation = provenance.predicate?.runDetails?.metadata?.invocationId?.match(/^https:\/\/github\.com\/([\w.-]+\/[\w.-]+)\/actions\/runs\/(\d+)$/);
  if (provenance._type !== 'https://in-toto.io/Statement/v1' || provenance.predicateType !== 'https://slsa.dev/provenance/v1') throw new Error('Unsupported provenance statement.');
  if (provenance.predicate?.buildDefinition?.buildType !== `https://github.com/${expectedRepository}/blob/master/docs/contracts/release-provenance-v1.md`) throw new Error('Provenance build type is invalid.');
  if (parameters?.sourceRevision !== expectedSourceRevision || !/^\d+$/.test(parameters?.ciRunId) || parameters?.repository !== expectedRepository || parameters?.workflow !== '.github/workflows/build-release.yml' || dependency?.digest?.gitCommit !== expectedSourceRevision) throw new Error('Provenance source or CI run binding is invalid.');
  if (!builder || !invocation || builder[1] !== expectedRepository || invocation[1] !== expectedRepository || dependency?.uri !== `git+https://github.com/${expectedRepository}@refs/heads/master`) throw new Error('Provenance builder or repository identity is invalid.');
  if (expectedReleaseRunId && invocation[2] !== expectedReleaseRunId) throw new Error('Provenance release workflow run does not match the requested evidence run.');
  if (validation.sourceRevision !== expectedSourceRevision || String(validation.ciRunId) !== parameters.ciRunId || validation.ciWorkflow !== 'Continuous integration' || validation.conclusion !== 'success') throw new Error('Provenance does not match the CI validation statement.');
  if (subjects.size !== manifest.artifacts.length || manifest.artifacts.some(({ name, digest }) => subjects.get(name) !== digest.slice(7))) throw new Error('Provenance artifact subjects do not match the release manifest.');
}
console.log(`Verified immutable release evidence for ${expectedSourceRevision}.`);
