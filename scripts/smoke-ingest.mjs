import { createHash, randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const [baseUrl, fixture = 'tests/fixtures/ingest/sample.png'] = process.argv.slice(2);
const token = process.env.INGEST_SMOKE_TOKEN;
if (!baseUrl || !token) throw new Error('Usage: INGEST_SMOKE_TOKEN=<token> node scripts/smoke-ingest.mjs <base-url> [fixture]');

const bytes = await readFile(fixture);
const sha256 = createHash('sha256').update(bytes).digest('hex');
const path = `deployment-smoke/${Date.now()}-${randomUUID()}.png`;
const key = () => randomUUID().replaceAll('-', '');
const headers = (extra = {}) => ({ Authorization: `Bearer ${token}`, ...extra });

async function request(route, options = {}) {
  const response = await fetch(new URL(route, baseUrl), { signal: AbortSignal.timeout(15000), ...options });
  if (!response.ok) throw new Error(`${options.method ?? 'GET'} ${route} failed with HTTP ${response.status}`);
  return response;
}

const batch = await (await request('/v1/import-batches', {
  method: 'POST',
  headers: headers({ 'Content-Type': 'application/json', 'Idempotency-Key': key() }),
  body: JSON.stringify({ atomic: false, items: [{ path, sha256, length: bytes.length }] }),
})).json();
const session = batch.items[0];

await request(`/v1/upload-sessions/${session.id}/transfer`, { method: 'POST', headers: headers({ 'Idempotency-Key': key(), 'If-Match': '"1"' }) });
await request(`/v1/upload-sessions/${session.id}/parts/1`, { method: 'PUT', headers: headers({ 'Idempotency-Key': key(), 'If-Match': '"1"', 'X-Content-SHA256': sha256 }), body: bytes });
await request(`/v1/upload-sessions/${session.id}/complete`, { method: 'POST', headers: headers({ 'Idempotency-Key': key(), 'If-Match': '"1"' }) });

let active;
for (let attempt = 0; attempt < 45; attempt++) {
  const status = await (await request(`/v1/import-batches/${batch.id}`, { headers: headers() })).json();
  const item = status.items[0];
  if (item.state === 'active') { active = item; break; }
  if (item.code || ['failed', 'quarantined', 'integrity-error'].includes(item.state)) throw new Error(`Worker returned ${item.code ?? item.state}`);
  await new Promise((resolve) => setTimeout(resolve, 2000));
}
if (!active?.assetId) throw new Error('Timed out waiting for the worker to publish the smoke asset.');

const downloaded = new Uint8Array(await (await request(`/v1/assets/${active.assetId}/content`, { headers: headers() })).arrayBuffer());
if (createHash('sha256').update(downloaded).digest('hex') !== sha256) throw new Error('Downloaded smoke asset checksum differs from the upload.');

const assets = await (await request('/v1/assets', { headers: headers() })).json();
const asset = assets.find(({ id }) => id === active.assetId);
if (!asset) throw new Error('Published smoke asset is absent from the active listing.');
const deleted = await (await request(`/v1/assets/${active.assetId}`, { method: 'DELETE', headers: headers({ 'Idempotency-Key': key(), 'If-Match': `"${asset.lifecycleVersion}"` }) })).json();
const restored = await (await request(`/v1/assets/${active.assetId}/restore`, { method: 'POST', headers: headers({ 'Content-Type': 'application/json', 'Idempotency-Key': key(), 'If-Match': `"${deleted.lifecycleVersion}"` }), body: JSON.stringify({ path: null }) })).json();
if (restored.state !== 'restored') throw new Error('Smoke asset did not return to restored state.');

const manifest = await (await request('/v1/reconstruction-manifests', { method: 'POST', headers: headers({ 'Idempotency-Key': key() }) })).json();
await request(`/v1/reconstruction-manifests/${manifest.id}`, { headers: headers() });
console.log(JSON.stringify({ batchId: batch.id, assetId: active.assetId, manifestId: manifest.id, sha256 }));
