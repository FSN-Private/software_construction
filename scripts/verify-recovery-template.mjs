import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function verifyRecoveryTemplate(bytes, expectedDigest) {
  if (!/^[a-f0-9]{64}$/.test(expectedDigest ?? '') || bytes.length === 0 || bytes.length > 64 * 1024 * 1024)
    throw new Error('Recovery template needs its independently pinned exact artifact digest.');
  const digest = createHash('sha256').update(bytes).digest('hex');
  if (digest !== expectedDigest) throw new Error('Recovery template artifact digest differs from its independent release pin.');
  const template = JSON.parse(bytes.toString('utf8'));
  const parameter = template.Parameters?.RecoveryTemplateDigest;
  if (parameter?.Type !== 'String' || Object.hasOwn(parameter, 'Default') ||
      parameter.AllowedPattern !== '^[a-f0-9]{64}$')
    throw new Error('Recovery template must require a bounded digest parameter without a default.');
  const pins = Object.values(template.Resources ?? {})
    .filter(resource => resource.Type === 'AWS::ECS::TaskDefinition')
    .flatMap(resource => resource.Properties?.ContainerDefinitions ?? [])
    .flatMap(container => container.Environment ?? [])
    .filter(variable => variable.Name === 'Recovery__RecoveryTemplateDigest');
  if (pins.length === 0 || pins.some(pin => JSON.stringify(pin.Value) !== '{"Ref":"RecoveryTemplateDigest"}'))
    throw new Error('Recovery runtime pins must reference the verified template digest parameter.');
  // The value is derived from the verified file, rather than selected separately.
  return [{ ParameterKey: 'RecoveryTemplateDigest', ParameterValue: digest }];
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 4)
    throw new Error('Usage: verify-recovery-template.mjs <exact-template.json> <parameter-output.json>');
  const input = resolve(process.argv[2]);
  const output = resolve(process.argv[3]);
  if (input === output) throw new Error('Parameter output must not overwrite the verified template.');
  const parameters = verifyRecoveryTemplate(readFileSync(input), process.env.RECOVERY_TEMPLATE_DIGEST);
  writeFileSync(output, JSON.stringify(parameters), { mode: 0o600 });
  console.log('Verified exact recovery template bytes and generated their mandatory digest parameter.');
}
