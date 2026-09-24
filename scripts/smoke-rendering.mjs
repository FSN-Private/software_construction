import { performance } from 'node:perf_hooks';

const [baseUrl] = process.argv.slice(2);
const token = process.env.INGEST_SMOKE_TOKEN;
const fixture = JSON.parse(process.env.RENDERING_ACCEPTANCE_FIXTURE ?? 'null');
if (!baseUrl || !token || !fixture?.libraryId || !fixture?.assetId || !fixture?.editId) throw new Error('Set INGEST_SMOKE_TOKEN and RENDERING_ACCEPTANCE_FIXTURE={"libraryId":"...","assetId":"...","editId":"..."}.');

const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const request = async (route, options = {}) => {
  const started = performance.now();
  const response = await fetch(new URL(route, baseUrl), { signal: AbortSignal.timeout(15000), ...options });
  if (!response.ok) throw new Error(`${options.method ?? 'GET'} ${route} failed with HTTP ${response.status}`);
  return { response, milliseconds: performance.now() - started };
};
const body = JSON.stringify({ libraryId: fixture.libraryId, assetId: fixture.assetId, editId: fixture.editId, selectedVersion: 'current', rendition: 'Thumbnail512', profile: 'render-v1' });
const uncached = await request('/v1/renditions', { method: 'POST', headers, body });
const created = await uncached.response.json();
if (!created.requestId || created.cacheHit) throw new Error('Acceptance fixture must begin with uncached rendering work.');
let completed;
for (let attempt = 0; attempt < 30; attempt++) {
  const status = await request(`/v1/renditions/${created.requestId}?library=${encodeURIComponent(fixture.libraryId)}`, { headers });
  completed = await status.response.json();
  if (completed.state === 'Completed') break;
  if (['Failed', 'Retryable', 'Unavailable', 'Cancelled'].includes(completed.state)) throw new Error(`Renderer returned ${completed.failureCode ?? completed.state}.`);
  await new Promise((resolve) => setTimeout(resolve, 1000));
}
if (completed?.state !== 'Completed') throw new Error('Timed out waiting for the acceptance rendition.');
const cacheSamples = [];
for (let attempt = 0; attempt < 20; attempt++) cacheSamples.push((await request(`/v1/renditions/${created.requestId}/derivative?library=${encodeURIComponent(fixture.libraryId)}`, { headers })).milliseconds);
cacheSamples.sort((left, right) => left - right);
const cacheP95 = cacheSamples[Math.ceil(cacheSamples.length * 0.95) - 1];
if (cacheP95 > 500) throw new Error(`Cache-hit p95 ${cacheP95.toFixed(1)}ms exceeds 500ms.`);
if (uncached.milliseconds > 2000) throw new Error(`Uncached request start ${uncached.milliseconds.toFixed(1)}ms exceeds 2000ms.`);
console.log(JSON.stringify({ requestId: created.requestId, uncachedRequestStartMs: Number(uncached.milliseconds.toFixed(1)), cacheHitP95Ms: Number(cacheP95.toFixed(1)), cacheSamples: cacheSamples.map((value) => Number(value.toFixed(1))) }));
