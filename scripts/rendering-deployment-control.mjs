import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { validateOrigin } from './regional-acceptance/contract.mjs';

// Protected workflow controls only; raw Cognito/HTTP output never reaches logs.
export async function renderingDeploymentControl(action, env = process.env, run = spawnSync, http = fetch) {
  if (env.DEPLOYMENT_ENVIRONMENT !== 'nonprod') throw new Error();
  if (!['activate', 'rollback', 'resume'].includes(action)) throw new Error();
  const origin = validateOrigin(env.HEALTH_URL);
  const record = JSON.parse(readFileSync(env.DEPLOYMENT_RECORD, 'utf8'));
  const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
  if (record.sourceRevision !== env.SOURCE_REVISION || !uuid.test(record.renderingOperationId) || !uuid.test(record.renderingRollbackReceiptId)) throw new Error();
  if (action === 'activate') {
    const verified = run(process.execPath, ['scripts/verify-promotion-evidence.mjs', `${env.RELEASE_DIR}/release-manifest.json`, env.DEPLOYMENT_RECORD,
      env.SOURCE_REVISION, env.RELEASE_RUN_ID], { encoding: 'utf8', maxBuffer: 65536, timeout: 30000, windowsHide: true });
    if (verified.status !== 0 || verified.error) throw new Error();
  }
  const auth = run('aws', ['cognito-idp', 'initiate-auth', '--auth-flow', 'USER_PASSWORD_AUTH', '--client-id', env.COGNITO_CLIENT_ID,
    '--auth-parameters', JSON.stringify({ USERNAME: env.COGNITO_OWNER_USERNAME, PASSWORD: env.COGNITO_OWNER_PASSWORD }),
    '--query', 'AuthenticationResult.IdToken', '--output', 'text', '--region', 'ap-southeast-1', '--cli-connect-timeout', '10', '--cli-read-timeout', '15'],
  { encoding: 'utf8', maxBuffer: 65536, timeout: 30000, windowsHide: true });
  const token = auth.stdout?.trim();
  if (auth.status !== 0 || auth.error || !token || token === 'None' || /[\r\n]/.test(token)) throw new Error();
  const route = action === 'rollback' ? `rollback/${record.renderingRollbackReceiptId}` : action;
  const url = new URL(`/v1/rendering/operations/${route}`, origin);
  if (action === 'rollback' && env.PREVIOUS_RENDERER_DIGEST) {
    if (!/^sha256:[a-f0-9]{64}$/.test(env.PREVIOUS_RENDERER_DIGEST)) throw new Error();
    url.searchParams.set('previousDigest', env.PREVIOUS_RENDERER_DIGEST);
  }
  const response = await http(url, { method: 'POST', redirect: 'error', signal: AbortSignal.timeout(15000),
    headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': `${record.renderingOperationId}-${action}` } });
  if (!response.ok) throw new Error();
  const chunks = [];
  let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > 65536) throw new Error();
    chunks.push(chunk);
  }
  const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  if (body.paused !== (action === 'rollback')) throw new Error();
  return `Rendering ${action} confirmed.`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { console.log(await renderingDeploymentControl(process.argv[2])); }
  catch { console.error('Rendering deployment control failed; release remains unqualified.'); process.exitCode = 1; }
}
