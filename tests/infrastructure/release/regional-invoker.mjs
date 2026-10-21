import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { awsCli, invokeRegionalAcceptance } from '../../../scripts/invoke-regional-acceptance.mjs';

const directory = mkdtempSync(join(tmpdir(), 'regional-invoker-test-'));
const secret = 'private-owner-token-in-payload-only';
const sourceRevision = 'a'.repeat(40);
const versionArn = 'arn:aws:lambda:ap-southeast-1:111122223333:function:platform-nonprod-acceptance:7';
const origin = 'https://vck1d7xax9.execute-api.ap-southeast-1.amazonaws.com';
const bundle = Buffer.from('fixture bundle bytes');
const digest = createHash('sha256').update(bundle).digest('hex');
const manifestPath = join(directory, 'release-manifest.json');
const trace = join(directory, 'trace.jsonl');
const shim = join(directory, 'aws-fixture.mjs');
const saved = Object.fromEntries(['REGIONAL_CLI_FIXTURE_ROOT', 'REGIONAL_CLI_FIXTURE_MODE', 'INGEST_SMOKE_TOKEN'].map(key => [key, process.env[key]]));
const messages = [], originalError = console.error;
try {
  writeFileSync(join(directory, 'regional-acceptance-client.zip'), bundle);
  writeFileSync(manifestPath, JSON.stringify({ sourceRevision, artifacts: [{ name: 'regional-acceptance-client.zip', digest: `sha256:${digest}`, bytes: bundle.length }] }));
  writeFileSync(shim, `import fs from 'node:fs';import path from 'node:path';
const root=process.env.REGIONAL_CLI_FIXTURE_ROOT,mode=process.env.REGIONAL_CLI_FIXTURE_MODE;
const args=process.argv.slice(2),operation=args[1],trace=path.join(root,'trace.jsonl');
const value=name=>args[args.indexOf(name)+1];
const record={operation,args,attempts:process.env.AWS_MAX_ATTEMPTS,tokenInEnvironment:!!process.env.INGEST_SMOKE_TOKEN};
const prior=fs.existsSync(trace)?fs.readFileSync(trace,'utf8').trim().split('\\n').filter(Boolean).map(JSON.parse):[];
if(operation==='get-function-configuration') {
 fs.appendFileSync(trace,JSON.stringify(record)+'\\n');
 const config={Version:'7',FunctionArn:${JSON.stringify(versionArn)},CodeSha256:${JSON.stringify(Buffer.from(digest, 'hex').toString('base64'))},Runtime:'nodejs24.x',Handler:'handler.handler',MemorySize:512,Timeout:300,PackageType:'Zip',Architectures:['x86_64'],Environment:{Variables:{TARGET_ORIGIN:${JSON.stringify(origin)}}},RuntimeVersionConfig:{RuntimeVersionArn:'runtime-fixed'}};
 if(mode==='wrong-code')config.CodeSha256='wrong';
 if(mode==='wrong-version')config.Version='8';
 if(mode==='wrong-origin')config.Environment.Variables.TARGET_ORIGIN='https://wrong.example';
 if(mode==='injected-environment')config.Environment.Variables.NODE_OPTIONS='private-hook';
 if(mode==='runtime-drift'&&prior.some(item=>item.operation==='invoke'))config.RuntimeVersionConfig.RuntimeVersionArn='runtime-changed';
 console.log(JSON.stringify(config));
} else if(operation==='invoke') {
 const payload=value('--payload').slice(8),output=args.at(-1),request=JSON.parse(fs.readFileSync(payload,'utf8'));
 record.payload=payload;record.payloadMode=fs.statSync(payload).mode&511;record.responseMode=fs.statSync(output).mode&511;
 fs.appendFileSync(trace,JSON.stringify(record)+'\\n');
 if(mode==='transport-error'){console.error(${JSON.stringify(secret)});process.exit(1);}
 const metrics={transport:'http2',cacheConcurrency:20,fixtureCount:5,uncachedRequestStartP95Ms:100,cacheHitP95Ms:50,uncachedStartSamples:Array(5).fill(100),cacheSamples:Array(20).fill(50)};
 const result=Object.fromEntries(['schemaVersion','invocationId','sourceRevision','releaseRunId','manifestSHA256','bundleSHA256','origin'].map(key=>[key,request[key]]));
 Object.assign(result,{region:'ap-southeast-1',functionArn:${JSON.stringify(versionArn)},runtime:'v24.20.0',passed:true,rendering:metrics,ingest:{batchId:request.invocationId,assetId:request.invocationId,manifestId:request.invocationId,sha256:'d'.repeat(64)}});
 if(mode==='wrong-source')result.sourceRevision='f'.repeat(40);
 if(mode==='wrong-invocation')result.invocationId='wrong';
 if(mode==='wrong-result-region')result.region='eu-north-1';
 if(mode==='wrong-count')result.rendering.cacheSamples.pop();
 if(mode==='nonfinite')result.rendering.cacheSamples[0]=NaN;
 if(mode==='wrong-percentile')result.rendering.cacheHitP95Ms=49;
 if(mode==='over-limit'){result.rendering.cacheHitP95Ms=501;result.rendering.cacheSamples.fill(501);}
 if(mode==='secret-result')result.token=${JSON.stringify(secret)};
 if(mode==='workload-failure'){result.passed=false;result.stage='rendering';result.metrics={...metrics,cacheHitP95Ms:501,cacheSamples:Array(20).fill(501)};result.rawError=${JSON.stringify(secret)};}
 fs.writeFileSync(output,mode==='oversize'?'x'.repeat(70000):mode==='malformed'?${JSON.stringify(secret)}:JSON.stringify(result));
 console.log(JSON.stringify({StatusCode:200,ExecutedVersion:mode==='wrong-executed-version'?'8':'7',...(mode==='function-error'?{FunctionError:'Unhandled'}:{})}));
} else process.exit(31);`);
  process.env.REGIONAL_CLI_FIXTURE_ROOT = directory;
  process.env.INGEST_SMOKE_TOKEN = secret;
  console.error = (...args) => messages.push(args.join(' '));
  const options = { manifestPath, sourceRevision, releaseRunId: '12345', origin, versionArn, account: '111122223333', token: secret,
    fixtures: Array.from({ length: 5 }, () => ({ libraryId: 'main', assetId: randomUUID(), editId: randomUUID() })) };
  const aws = args => awsCli(args, process.execPath, [shim]);
  const reset = mode => {
    process.env.REGIONAL_CLI_FIXTURE_MODE = mode;
    rmSync(trace, { force: true });
    for (const name of ['rendering-smoke.json', 'ingest-smoke.json', 'regional-acceptance.json']) rmSync(join(directory, name), { force: true });
  };
  const calls = () => existsSync(trace) ? readFileSync(trace, 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse) : [];
  const verifyCalls = () => {
    for (const item of calls()) {
      assert.equal(item.attempts, '1');
      assert.equal(item.tokenInEnvironment, false);
      assert.equal(JSON.stringify(item.args).includes(secret), false);
      if (item.operation === 'invoke') {
        assert.ok(item.args.includes('RequestResponse') && item.args.includes('None') && item.args.includes('330'));
        assert.equal(existsSync(dirname(item.payload)), false, 'Restricted invoke files are deleted in finally.');
        if (process.platform !== 'win32') { assert.equal(item.payloadMode, 0o600); assert.equal(item.responseMode, 0o600); }
      }
    }
    assert.ok(calls().filter(item => item.operation === 'invoke').length <= 1, 'No automatic reinvocation.');
  };
  reset('success');
  const proof = invokeRegionalAcceptance(options, aws);
  assert.equal(proof.region, 'ap-southeast-1');
  assert.equal(proof.bundleSHA256, digest);
  assert.equal(JSON.stringify(proof).includes(secret), false);
  assert.equal(calls().filter(item => item.operation === 'invoke').length, 1);
  for (const name of ['rendering-smoke.json', 'ingest-smoke.json', 'regional-acceptance.json']) assert.equal(readFileSync(join(directory, name), 'utf8').includes(secret), false);
  verifyCalls();
  for (const mode of ['wrong-code', 'wrong-version', 'wrong-origin', 'injected-environment', 'runtime-drift', 'transport-error', 'function-error', 'wrong-executed-version', 'wrong-source', 'wrong-invocation', 'wrong-result-region', 'wrong-count', 'nonfinite', 'wrong-percentile', 'over-limit', 'secret-result', 'workload-failure', 'oversize', 'malformed']) {
    reset(mode);
    assert.throws(() => invokeRegionalAcceptance(options, aws), undefined, mode);
    assert.equal(existsSync(join(directory, 'regional-acceptance.json')), false, 'Failure never writes healthy proof.');
    verifyCalls();
  }
  for (const invalid of [{ ...options, versionArn: versionArn.replace(':7', ':$LATEST') }, { ...options, versionArn: versionArn.replace('ap-southeast-1', 'eu-north-1') }, { ...options, account: '999988887777' }, { ...options, sourceRevision: 'f'.repeat(40) }]) {
    reset('success'); assert.throws(() => invokeRegionalAcceptance(invalid, aws)); assert.equal(calls().length, 0);
  }
  writeFileSync(join(directory, 'regional-acceptance-client.zip'), 'tampered');
  reset('success'); assert.throws(() => invokeRegionalAcceptance(options, aws)); assert.equal(calls().length, 0);
  assert.equal(messages.join('\n').includes(secret), false, 'Raw CLI/function errors cannot disclose tokens.');
  assert.ok(messages.some(message => message.includes('501')), 'Safe over-limit timing remains observable.');
  console.log('Regional source/code/result binding, real CLI failures, no retries and token cleanup passed.');
} finally {
  console.error = originalError;
  for (const [key, value] of Object.entries(saved)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
  assert.equal(dirname(directory), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('regional-invoker-test-'));
  rmSync(directory, { recursive: true, force: true });
}
