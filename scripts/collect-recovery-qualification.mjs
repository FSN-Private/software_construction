import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { drillConfiguration } from './run-recovery-drill.mjs';
import { promotionBinding } from './verify-recovery-promotion.mjs';
import { verifyRecoveryQualification } from './recovery-release-evidence.mjs';
import { sha256File } from '../packages/release-tools/release-manifest.mjs';

export function qualificationPointers(item, runId, bucket) {
  const string = (name) => item?.[name]?.S;
  if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(runId ?? '') ||
      string('state') !== 'qualified' || string('runId') !== runId)
    throw new Error('Recovery run has no completed attributable qualification.');
  return [['qualification', 'recovery-qualification.v1.json', 'recovery-qualification.json', 1024 * 1024],
    ['checkpoint', 'recovery-audit-checkpoint.v1.json', 'recovery-checkpoint.json', 64 * 1024 * 1024]]
    .map(([prefix, key, filename, maximum]) => {
      const pointer = { bucket: string(`${prefix}Bucket`), key: string(`${prefix}Key`),
        versionId: string(`${prefix}VersionId`), sha256: string(`${prefix}Sha256`),
        byteLength: Number(item?.[`${prefix}ByteLength`]?.N), filename };
      if (pointer.bucket !== bucket || pointer.key !== `evidence/drills/${runId}/${key}` ||
          !pointer.versionId || pointer.versionId === 'null' || pointer.versionId.length > 1024 ||
          !/^[a-f0-9]{64}$/.test(pointer.sha256 ?? '') || !Number.isSafeInteger(pointer.byteLength) ||
          pointer.byteLength <= 0 || pointer.byteLength > maximum)
        throw new Error('Recovery qualification pointer violates its exact-version evidence boundary.');
      return pointer;
    });
}

export function collectQualification(launchPath, environment = process.env) {
  const configuration = drillConfiguration(environment);
  const launch = JSON.parse(readFileSync(launchPath, 'utf8'));
  if (launch.state !== 'worker_completed' || launch.runId !== environment.RECOVERY_DRILL_RUN_ID ||
      !launch.taskArn?.startsWith(`arn:aws:ecs:${configuration.region}:${configuration.account}:task/`))
    throw new Error('Recovery qualification requires the selected completed launch record.');
  const table = environment.RECOVERY_CONTROL_TABLE_ARN;
  if (!new RegExp(`^arn:aws:dynamodb:${configuration.region}:${configuration.account}:table/[\\w.-]+$`).test(table ?? ''))
    throw new Error('Recovery qualification requires the recovery-owned control table.');
  const bucket = environment.RECOVERY_EVIDENCE_BUCKET;
  if (!/^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$/.test(bucket ?? ''))
    throw new Error('Recovery qualification requires its independently configured evidence bucket.');
  const aws = (...arguments_) => JSON.parse(execFileSync('aws', [...arguments_, '--region', configuration.region, '--output', 'json'],
    { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 }));
  const identity = aws('sts', 'get-caller-identity');
  if (identity.Account !== configuration.account ||
      !identity.Arn?.startsWith(`arn:aws:sts::${configuration.account}:assumed-role/${configuration.role.split('/').at(-1)}/`))
    throw new Error('Qualification export credentials do not match the independent recovery role.');
  const record = aws('dynamodb', 'get-item', '--table-name', table, '--consistent-read', '--key',
    JSON.stringify({ pk: { S: `RUN#${launch.runId}` }, sk: { S: 'QUALIFICATION' } })).Item;
  const pointers = qualificationPointers(record, launch.runId, bucket);
  const directory = resolve('artifacts/recovery-qualification');
  mkdirSync(directory, { recursive: true, mode: 0o700 });
  for (const pointer of pointers) {
    const target = resolve(directory, pointer.filename);
    const metadata = aws('s3api', 'get-object', '--bucket', pointer.bucket, '--key', pointer.key,
      '--version-id', pointer.versionId, '--expected-bucket-owner', configuration.account, target);
    if (metadata.VersionId !== pointer.versionId || metadata.ContentLength !== pointer.byteLength ||
        readFileSync(target).length !== pointer.byteLength || sha256File(target) !== `sha256:${pointer.sha256}`)
      throw new Error('Exported qualification evidence failed its exact-version checksum check.');
  }
  const qualificationPath = resolve(directory, 'recovery-qualification.json');
  const envelope = JSON.parse(readFileSync(qualificationPath, 'utf8'));
  if (envelope.payload?.runId !== launch.runId || envelope.payload?.sourceRevision !== environment.SOURCE_REVISION ||
      envelope.payload?.primary?.accountId !== configuration.primaryAccount || envelope.payload?.primary?.region !== 'ap-southeast-1' ||
      envelope.payload?.recovery?.accountId !== configuration.account || envelope.payload?.recovery?.region !== configuration.region)
    throw new Error('Exported qualification belongs to a different run, release or environment.');
  const bindingPath = resolve(directory, 'recovery-release-binding.json');
  writeFileSync(bindingPath, JSON.stringify(promotionBinding(environment)), { mode: 0o600 });
  verifyRecoveryQualification({ qualification: qualificationPath, binding: bindingPath,
    checkpoint: resolve(directory, 'recovery-checkpoint.json'), trust: environment.RECOVERY_TRUST_PATH,
    anchor: environment.RECOVERY_OFFLINE_ANCHOR_PATH, sourceRevision: environment.SOURCE_REVISION });
  console.log(`Verified payload-free recovery qualification for run ${launch.runId}.`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 3) throw new Error('Usage: collect-recovery-qualification.mjs <completed-launch-record>');
  collectQualification(process.argv[2]);
}
