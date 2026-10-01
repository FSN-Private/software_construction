import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const provenance = JSON.parse(readFileSync('vendor/cdk-patch-provenance.json', 'utf8'));
const digest = createHash('sha512').update(readFileSync(`vendor/${provenance.archive}`)).digest();
const lock = JSON.parse(readFileSync('package-lock.json', 'utf8'));
const dependency = lock.packages['node_modules/aws-cdk-lib'];
const installed = JSON.parse(readFileSync('node_modules/aws-cdk-lib/node_modules/brace-expansion/package.json', 'utf8'));
if (digest.toString('hex') !== provenance.sha512 ||
    dependency.resolved !== `file:vendor/${provenance.archive}` ||
    dependency.integrity !== `sha512-${digest.toString('base64')}` ||
    dependency.version !== provenance.upstream.version ||
    installed.name !== provenance.replacement.name ||
    installed.version !== provenance.replacement.version ||
    lock.packages['node_modules/aws-cdk-lib/node_modules/brace-expansion'].version !== installed.version) {
  throw new Error('Patched CDK archive, lockfile or installed bundle does not match its approved provenance.');
}
console.log('Patched CDK archive integrity, lockfile and installed version verified.');
