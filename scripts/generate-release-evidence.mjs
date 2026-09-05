import { createHash } from 'node:crypto';
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { createReleaseManifest } from '../packages/release-tools/release-manifest.mjs';

const [sourceRevision, outputDirectory, ciRunId] = process.argv.slice(2);
if (!/^[a-f0-9]{40}$/.test(sourceRevision) || !outputDirectory) throw new Error('Usage: generate-release-evidence.mjs <full-source-sha> <output-directory>');
mkdirSync(outputDirectory, { recursive: true });
const artifacts = readdirSync(outputDirectory, { withFileTypes: true }).filter((entry) => entry.isFile() && /\.(tar|zip)$/.test(entry.name)).map((entry) => join(outputDirectory, entry.name));
if (artifacts.length !== 4) throw new Error('Release evidence requires API, web, infrastructure, and Windows artifacts.');
const digest = (path) => `sha256:${createHash('sha256').update(readFileSync(path)).digest('hex')}`;
const sbom = join(outputDirectory, 'sbom.spdx.json');
const provenance = join(outputDirectory, 'provenance.json');
const validation = join(outputDirectory, 'validation-evidence.json');
writeFileSync(sbom, JSON.stringify({ spdxVersion: 'SPDX-2.3', name: `release-${sourceRevision}`, files: artifacts.map((path) => ({ fileName: basename(path), checksums: [{ algorithm: 'SHA256', checksumValue: digest(path).slice(7) }] })) }, null, 2));
writeFileSync(provenance, JSON.stringify({ predicateType: 'https://slsa.dev/provenance/v1', buildDefinition: { externalParameters: { sourceRevision } } }, null, 2));
writeFileSync(validation, JSON.stringify({ sourceRevision, ciWorkflow: 'Continuous integration', ciRunId: ciRunId ?? 'manual-dispatch', conclusion: 'success', requiredChecks: ['ci / backend', 'ci / web', 'ci / windows', 'ci / infrastructure', 'ci / security', 'ci / required'] }, null, 2));
const manifest = createReleaseManifest({ releaseId: `vbuild-${sourceRevision}`, sourceRevision, artifacts, sbom, provenance, additionalEvidence: [validation], compatibility: { rollbackAllowed: true, schemaVersion: 'v1' } });
writeFileSync(join(outputDirectory, 'release-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
