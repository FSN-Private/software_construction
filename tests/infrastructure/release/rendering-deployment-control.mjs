import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { renderingDeploymentControl } from '../../../scripts/rendering-deployment-control.mjs';

const directory = mkdtempSync(join(tmpdir(), 'render-control-'));
try {
  const record = { sourceRevision: 'a'.repeat(40), renderingOperationId: '00000000-0000-0000-0000-000000000001', renderingRollbackReceiptId: '00000000-0000-0000-0000-000000000002' };
  const path = join(directory, 'record.json'); writeFileSync(path, JSON.stringify(record));
  const env = { DEPLOYMENT_ENVIRONMENT: 'nonprod', HEALTH_URL: 'https://fixtureapi123.execute-api.ap-southeast-1.amazonaws.com', DEPLOYMENT_RECORD: path,
    SOURCE_REVISION: record.sourceRevision, RELEASE_DIR: directory, RELEASE_RUN_ID: '12345', COGNITO_CLIENT_ID: 'fixture', COGNITO_OWNER_USERNAME: 'owner', COGNITO_OWNER_PASSWORD: 'test-only-password', PREVIOUS_RENDERER_DIGEST: `sha256:${'a'.repeat(64)}` };
  for (const action of ['activate', 'rollback', 'resume']) {
    const calls = [];
    const run = (command, args, options) => {
      calls.push({ command, args }); assert.equal(options.maxBuffer, 65536); assert.equal(options.timeout, 30000);
      return { status: 0, stdout: command === 'aws' ? 'private-token' : 'verified', stderr: '' };
    };
    const http = async (url, options) => {
      assert.equal(url.origin, env.HEALTH_URL); assert.equal(options.method, 'POST'); assert.equal(options.redirect, 'error');
      assert.equal(options.headers.Authorization, 'Bearer private-token'); assert.equal(options.headers['Idempotency-Key'], `${record.renderingOperationId}-${action}`);
      assert.equal(url.pathname, `/v1/rendering/operations/${action === 'rollback' ? `rollback/${record.renderingRollbackReceiptId}` : action}`);
      if (action === 'rollback') assert.equal(url.searchParams.get('previousDigest'), env.PREVIOUS_RENDERER_DIGEST);
      return new Response(JSON.stringify({ paused: action === 'rollback' }));
    };
    assert.equal(await renderingDeploymentControl(action, env, run, http), `Rendering ${action} confirmed.`);
    assert.equal(calls.length, action === 'activate' ? 2 : 1);
    assert.ok(!JSON.stringify(calls).includes('private-token'), 'Owner JWT must never enter AWS command arguments.');
    if (action === 'activate') assert.equal(calls[0].args[0], 'scripts/verify-promotion-evidence.mjs');
  }
  const auth = () => ({ status: 0, stdout: 'private-token' });
  for (const response of [() => new Response('private-error', { status: 500 }), () => new Response(JSON.stringify({ paused: false })), () => new Response('{malformed'), () => new Response('x'.repeat(65537))])
    await assert.rejects(renderingDeploymentControl('rollback', env, auth, async () => response()));
  await assert.rejects(renderingDeploymentControl('activate', env, () => ({ status: 1, stderr: 'secret' }), async () => assert.fail('Failed evidence must prevent HTTP.')));
  for (const changed of [{ ...env, DEPLOYMENT_ENVIRONMENT: 'production' }, { ...env, HEALTH_URL: 'https://different.example' }, { ...env, SOURCE_REVISION: 'b'.repeat(40) }])
    await assert.rejects(renderingDeploymentControl('rollback', changed, () => assert.fail('Reject before credentials.'), async () => assert.fail()));
  await assert.rejects(renderingDeploymentControl('rollback', env, () => ({ status: 1, stdout: 'private-token' }), async () => assert.fail('Failed auth must prevent HTTP.')));
  console.log('Deployment control source/receipt/target/evidence guards, bounded responses and token-safe failures passed.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir())); assert.ok(basename(directory).startsWith('render-control-'));
  rmSync(directory, { recursive: true, force: true });
}
