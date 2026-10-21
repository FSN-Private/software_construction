import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { functionArn, renderingMetrics, validateRequest, validateResult } from './regional-acceptance/contract.mjs';

const check = condition => { if (!condition) throw new Error('Regional acceptance verification failed.'); };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

// command/prefix allow tests to exercise a real CLI process without AWS access.
export function awsCli(args, command = 'aws', prefix = []) {
  const env = { ...process.env, AWS_MAX_ATTEMPTS: '1' };
  delete env.INGEST_SMOKE_TOKEN;
  delete env.RENDERING_ACCEPTANCE_FIXTURES;
  const result = spawnSync(command, [...prefix, ...args], {
    env, timeout: 340000, maxBuffer: 65536, encoding: 'utf8', windowsHide: true,
  });
  if (result.status !== 0 || result.error) throw new Error('Regional acceptance AWS operation failed.');
  try { return JSON.parse(result.stdout); } catch { throw new Error('Regional acceptance AWS response was invalid.'); }
}

export function invokeRegionalAcceptance(options, aws = awsCli) {
  const { manifestPath, sourceRevision, releaseRunId, origin, versionArn, account, token, fixtures } = options;
  const arn = functionArn.exec(versionArn ?? '');
  check(arn && /^\d{12}$/.test(account ?? '') && arn[1] === account);
  const manifestBytes = readFileSync(manifestPath);
  const manifest = JSON.parse(manifestBytes);
  check(manifest.sourceRevision === sourceRevision);
  const artifacts = manifest.artifacts.filter(item => item.name === 'regional-acceptance-client.zip');
  check(artifacts.length === 1 && /^sha256:[a-f0-9]{64}$/.test(artifacts[0].digest));
  const bundle = readFileSync(join(dirname(resolve(manifestPath)), 'regional-acceptance-client.zip'));
  const bundleSHA256 = hash(bundle);
  check(artifacts[0].digest === `sha256:${bundleSHA256}` && artifacts[0].bytes === bundle.length);
  const request = validateRequest({ schemaVersion: 1, invocationId: randomUUID(), sourceRevision, releaseRunId,
    manifestSHA256: hash(manifestBytes), bundleSHA256, origin, token, fixtures });
  const configuration = () => aws(['lambda', 'get-function-configuration', '--function-name', versionArn, '--region', 'ap-southeast-1', '--cli-connect-timeout', '10', '--cli-read-timeout', '15', '--output', 'json']);
  const verifyConfiguration = config => {
    check(config.Version === arn[2] && config.FunctionArn?.replace(/:\d+$/, '') === versionArn.replace(/:\d+$/, ''));
    check(config.CodeSha256 === Buffer.from(bundleSHA256, 'hex').toString('base64') && config.Runtime === 'nodejs24.x' && config.Handler === 'handler.handler');
    check(config.MemorySize === 512 && config.Timeout === 300 && config.PackageType === 'Zip');
    check(JSON.stringify(config.Architectures) === '["x86_64"]' && !config.Layers?.length && !config.FileSystemConfigs?.length && !config.VpcConfig?.SubnetIds?.length);
    check(config.Environment && !config.Environment.Error && Object.keys(config.Environment.Variables ?? {}).length === 1 && config.Environment.Variables.TARGET_ORIGIN === origin);
    return { CodeSha256: config.CodeSha256, RuntimeVersionArn: config.RuntimeVersionConfig?.RuntimeVersionArn ?? null };
  };
  const before = verifyConfiguration(configuration());
  const directory = mkdtempSync(join(tmpdir(), 'regional-invoke-'));
  try {
    const payload = join(directory, 'request.json');
    const response = join(directory, 'response.json');
    const encoded = JSON.stringify(request);
    check(Buffer.byteLength(encoded) <= 32768);
    writeFileSync(payload, encoded, { mode: 0o600 });
    writeFileSync(response, '', { mode: 0o600 });
    // The token is only in a restricted file, never a command argument or log.
    const metadata = aws(['lambda', 'invoke', '--function-name', versionArn, '--region', 'ap-southeast-1', '--invocation-type', 'RequestResponse',
      '--log-type', 'None', '--cli-binary-format', 'raw-in-base64-out', '--payload', `fileb://${payload}`, '--cli-connect-timeout', '10', '--cli-read-timeout', '330', response]);
    check(metadata.StatusCode === 200 && !metadata.FunctionError && !metadata.LogResult && metadata.ExecutedVersion === arn[2]);
    check(statSync(response).size <= 65536);
    const value = JSON.parse(readFileSync(response, 'utf8'));
    if (value?.passed === false) {
      if (['rendering', 'ingest'].includes(value.stage)) {
        let metrics;
        try { if (value.metrics) metrics = renderingMetrics(value.metrics, true); } catch { /* Never log unvalidated output. */ }
        console.error(JSON.stringify({ phase: 'regional-acceptance-failure', stage: value.stage, ...(metrics ? { metrics } : {}) }));
      }
      throw new Error('Regional acceptance workload failed.');
    }
    const result = validateResult(value, request, versionArn);
    check(JSON.stringify(verifyConfiguration(configuration())) === JSON.stringify(before));
    const proof = { schemaVersion: 1, invocationId: result.invocationId, sourceRevision, releaseRunId,
      manifestSHA256: result.manifestSHA256, bundleSHA256, origin, region: result.region, functionArn: versionArn,
      runtime: result.runtime, runtimeVersionArn: before.RuntimeVersionArn };
    const output = dirname(resolve(manifestPath));
    for (const [name, data] of [['rendering-smoke.json', result.rendering], ['ingest-smoke.json', result.ingest], ['regional-acceptance.json', proof]])
      writeFileSync(join(output, name), `${JSON.stringify(data)}\n`, { mode: 0o600 });
    return proof;
  } finally {
    check(dirname(directory) === resolve(tmpdir()) && basename(directory).startsWith('regional-invoke-'));
    rmSync(directory, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    invokeRegionalAcceptance({ manifestPath: process.argv[2], sourceRevision: process.env.SOURCE_REVISION, releaseRunId: process.env.RELEASE_RUN_ID,
      origin: process.env.HEALTH_URL, versionArn: process.env.REGIONAL_ACCEPTANCE_FUNCTION_VERSION, account: process.env.ECR_REGISTRY?.split('.')[0],
      token: process.env.INGEST_SMOKE_TOKEN, fixtures: JSON.parse(process.env.RENDERING_ACCEPTANCE_FIXTURES ?? 'null') });
  } catch { console.error('Regional acceptance failed; no deployment qualification.'); process.exitCode = 1; }
}
