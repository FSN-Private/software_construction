import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { verifyRecoveryTemplate } from '../../../scripts/verify-recovery-template.mjs';

const fixture = () => ({ Parameters: {
  RecoveryTemplateDigest: { Type: 'String', AllowedPattern: '^[a-f0-9]{64}$' },
}, Resources: { Worker: { Type: 'AWS::ECS::TaskDefinition', Properties: {
  ContainerDefinitions: [{ Environment: [{ Name: 'Recovery__RecoveryTemplateDigest', Value: { Ref: 'RecoveryTemplateDigest' } }] }],
} } } });
const bytes = value => Buffer.from(JSON.stringify(value));
const digest = value => createHash('sha256').update(value).digest('hex');
const valid = bytes(fixture());
assert.deepEqual(verifyRecoveryTemplate(valid, digest(valid)), [
  { ParameterKey: 'RecoveryTemplateDigest', ParameterValue: digest(valid) },
]);
assert.throws(() => verifyRecoveryTemplate(valid, undefined), /independently pinned/);
assert.throws(() => verifyRecoveryTemplate(valid, '0'.repeat(64)), /differs/);
assert.throws(() => verifyRecoveryTemplate(Buffer.concat([valid, Buffer.from('\n')]), digest(valid)), /differs/);
const defaulted = fixture();
defaulted.Parameters.RecoveryTemplateDigest.Default = '0'.repeat(64);
const defaultBytes = bytes(defaulted);
assert.throws(() => verifyRecoveryTemplate(defaultBytes, digest(defaultBytes)), /without a default/);
const omitted = fixture();
delete omitted.Parameters.RecoveryTemplateDigest;
const omittedBytes = bytes(omitted);
assert.throws(() => verifyRecoveryTemplate(omittedBytes, digest(omittedBytes)), /without a default/);
const arbitraryPin = fixture();
arbitraryPin.Resources.Worker.Properties.ContainerDefinitions[0].Environment[0].Value = '0'.repeat(64);
const arbitraryBytes = bytes(arbitraryPin);
assert.throws(() => verifyRecoveryTemplate(arbitraryBytes, digest(arbitraryBytes)), /must reference/);
const synthesized = readFileSync('packages/infrastructure/cdk.out-recovery/Recovery-nonprod.template.json');
assert.deepEqual(verifyRecoveryTemplate(synthesized, digest(synthesized)), [
  { ParameterKey: 'RecoveryTemplateDigest', ParameterValue: digest(synthesized) },
]);
assert.throws(() => verifyRecoveryTemplate(Buffer.concat([synthesized, Buffer.from('\n')]), digest(synthesized)), /differs/);
console.log('Exact-template gate rejects changed bytes, missing independent pins and defaulted digest parameters. No deployment performed.');
