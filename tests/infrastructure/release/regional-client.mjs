import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';
import { bindings, renderingMetrics, validateRequest } from '../../../scripts/regional-acceptance/contract.mjs';

const directory = mkdtempSync(join(tmpdir(), 'regional-client-'));
const shell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
const ps = args => {
  const result = spawnSync(shell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', ...args], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result;
};
const quote = value => `'${value.replaceAll("'", "''")}'`;
const secret = 'private-owner-token-that-must-never-escape';
const origin = 'https://vck1d7xax9.execute-api.ap-southeast-1.amazonaws.com';
const request = { schemaVersion: 1, invocationId: randomUUID(), sourceRevision: 'a'.repeat(40), releaseRunId: '12345', manifestSHA256: 'b'.repeat(64), bundleSHA256: 'c'.repeat(64), origin, token: secret,
  fixtures: Array.from({ length: 5 }, () => ({ libraryId: 'main', assetId: randomUUID(), editId: randomUUID() })) };
const metrics = { transport: 'http2', cacheConcurrency: 20, fixtureCount: 5, uncachedRequestStartP95Ms: 100, cacheHitP95Ms: 50, uncachedStartSamples: Array(5).fill(100), cacheSamples: Array(20).fill(50) };
const ingest = { batchId: randomUUID(), assetId: randomUUID(), manifestId: randomUUID(), sha256: 'd'.repeat(64) };
const context = { invokedFunctionArn: 'arn:aws:lambda:ap-southeast-1:111122223333:function:platform-nonprod-acceptance:7', functionVersion: '7' };
const savedEnvironment = Object.fromEntries(['AWS_REGION', 'TARGET_ORIGIN', 'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_SESSION_TOKEN', 'REGIONAL_PARENT_SENTINEL'].map(key => [key, process.env[key]]));
try {
  const zip = join(directory, 'client.zip');
  const repeatZip = join(directory, 'repeat.zip');
  for (const target of [zip, repeatZip]) ps(['-File', 'scripts/package-regional-acceptance.ps1', '-OutputPath', target]);
  assert.deepEqual(readFileSync(zip), readFileSync(repeatZip), 'Identical local inputs must produce identical bundle bytes.');
  const expanded = join(directory, 'expanded');
  ps(['-Command', `Expand-Archive -LiteralPath ${quote(zip)} -DestinationPath ${quote(expanded)}`]);
  assert.deepEqual(readdirSync(expanded).sort(), ['contract.mjs', 'handler.mjs', 'sample.png', 'smoke-ingest.mjs', 'smoke-rendering.mjs']);
  for (const name of ['handler.mjs', 'contract.mjs', 'smoke-rendering.mjs', 'smoke-ingest.mjs']) {
    const source = name === 'handler.mjs' || name === 'contract.mjs' ? `scripts/regional-acceptance/${name}` : `scripts/${name}`;
    assert.equal(readFileSync(join(expanded, name), 'utf8'), readFileSync(source, 'utf8').replaceAll('\r\n', '\n'));
  }
  assert.deepEqual(readFileSync(join(expanded, 'sample.png')), readFileSync('tests/fixtures/ingest/sample.png'));

  const fixture = join(directory, 'process-fixture');
  mkdirSync(fixture);
  for (const name of ['handler.mjs', 'contract.mjs']) copyFileSync(join(expanded, name), join(fixture, name));
  writeFileSync(join(fixture, 'sample.png'), 'fixture');
  const checkEnvironment = `if (['AWS_ACCESS_KEY_ID','AWS_SECRET_ACCESS_KEY','AWS_SESSION_TOKEN','REGIONAL_PARENT_SENTINEL'].some(key=>process.env[key])) process.exit(19); if(process.env.INGEST_SMOKE_TOKEN!==${JSON.stringify(secret)}) process.exit(20);`;
  const successfulRender = `import fs from 'node:fs'; ${checkEnvironment} if(JSON.parse(process.env.RENDERING_ACCEPTANCE_FIXTURES).length!==5) process.exit(21); fs.writeFileSync('order.txt','rendering'); console.log(${JSON.stringify(JSON.stringify(metrics))});`;
  writeFileSync(join(fixture, 'smoke-rendering.mjs'), successfulRender);
  writeFileSync(join(fixture, 'smoke-ingest.mjs'), `import fs from 'node:fs'; ${checkEnvironment} if(fs.readFileSync('order.txt','utf8')!=='rendering'||!fs.existsSync(process.argv[3])) process.exit(22); console.log(${JSON.stringify(JSON.stringify(ingest))});`);
  process.env.AWS_REGION = 'ap-southeast-1';
  process.env.TARGET_ORIGIN = origin;
  for (const key of ['AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_SESSION_TOKEN', 'REGIONAL_PARENT_SENTINEL']) process.env[key] = 'private-parent-credential';
  const { handler, runSmoke } = await import(pathToFileURL(join(fixture, 'handler.mjs')));
  const passed = await handler(request, context);
  assert.equal(passed.passed, true, 'Execute actual bounded child processes in rendering/ingest order.');
  assert.deepEqual(passed.rendering, metrics);
  assert.deepEqual(passed.ingest, ingest);
  assert.equal(JSON.stringify(passed).includes(secret), false);
  assert.equal(JSON.stringify(bindings(request)).includes(secret), false);

  for (const invalid of [{ ...request, token: '' }, { ...request, token: 'x\r\ny' }, { ...request, sourceRevision: 'bad' }, { ...request, origin: 'https://other.example' }, { ...request, fixtures: Array(5).fill(request.fixtures[0]) }]) {
    assert.throws(() => validateRequest(invalid));
    await assert.rejects(handler(invalid, context), /Invalid regional acceptance/);
  }
  for (const invalid of [{ ...metrics, cacheSamples: Array(19).fill(50) }, { ...metrics, cacheSamples: Array(20).fill(NaN) }, { ...metrics, cacheHitP95Ms: 49 }, { ...metrics, cacheHitP95Ms: 500, cacheSamples: Array(20).fill(500.05) }, { ...metrics, token: secret }]) assert.throws(() => renderingMetrics(invalid));
  await assert.rejects(handler(request, { ...context, functionVersion: '$LATEST' }));
  process.env.AWS_REGION = 'eu-north-1';
  await assert.rejects(handler(request, context));
  process.env.AWS_REGION = 'ap-southeast-1';
  process.env.TARGET_ORIGIN = 'https://different.execute-api.ap-southeast-1.amazonaws.com';
  await assert.rejects(handler(request, context));
  process.env.TARGET_ORIGIN = origin;

  writeFileSync(join(fixture, 'smoke-rendering.mjs'), `console.error(${JSON.stringify(secret)}); process.exit(1);`);
  const failed = await handler(request, context);
  assert.equal(failed.passed, false);
  assert.equal(failed.stage, 'rendering');
  assert.equal(JSON.stringify(failed).includes(secret), false, 'Raw child stderr must not escape.');
  const slow = { ...metrics, cacheHitP95Ms: 501, cacheSamples: Array(20).fill(501) };
  writeFileSync(join(fixture, 'smoke-rendering.mjs'), `console.error(${JSON.stringify(JSON.stringify({ phase: 'rendering-latency-failure', ...slow }))}); console.error(${JSON.stringify(secret)}); process.exit(1);`);
  assert.deepEqual((await handler(request, context)).metrics, slow, 'Retain only validated duration/count failure metrics.');
  writeFileSync(join(fixture, 'smoke-rendering.mjs'), `console.log('x'.repeat(100000));`);
  assert.equal((await handler(request, context)).passed, false, 'Excess child output fails closed.');
  writeFileSync(join(fixture, 'smoke-rendering.mjs'), successfulRender);
  writeFileSync(join(fixture, 'smoke-ingest.mjs'), `throw new Error(${JSON.stringify(secret)});`);
  const ingestFailure = await handler(request, context);
  assert.equal(ingestFailure.passed, false);
  assert.equal(ingestFailure.stage, 'ingest');
  assert.equal(JSON.stringify(ingestFailure).includes(secret), false);
  writeFileSync(join(fixture, 'hang.mjs'), 'setInterval(()=>{},1000);');
  await assert.rejects(runSmoke('hang.mjs', [], {}, 100), 'Actual hanging child must be terminated.');
  assert.ok(existsSync(join(fixture, 'order.txt')));
  console.log('Regional client package, real child execution, bounds and credential privacy passed.');
} finally {
  for (const [key, value] of Object.entries(savedEnvironment)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  assert.equal(dirname(directory), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('regional-client-'));
  rmSync(directory, { recursive: true, force: true });
}
