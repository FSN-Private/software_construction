import { performance } from 'node:perf_hooks';

const [baseUrl] = process.argv.slice(2);
const token = process.env.INGEST_SMOKE_TOKEN;
const fixtures = JSON.parse(process.env.RENDERING_ACCEPTANCE_FIXTURES ?? 'null');
if (!baseUrl || !token || !Array.isArray(fixtures) || fixtures.length < 20 || fixtures.some(({ libraryId, assetId, editId } = {}) => !libraryId || !assetId || !editId)) throw new Error('Set RENDERING_ACCEPTANCE_FIXTURES to at least 20 distinct {libraryId,assetId,editId} values.');
if (new Set(fixtures.map(({ libraryId, assetId, editId }) => `${libraryId}/${assetId}/${editId}`)).size !== fixtures.length) throw new Error('Rendering acceptance fixtures must be distinct.');

const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const request = async (route, options = {}) => {
  const started = performance.now();
  const response = await fetch(new URL(route, baseUrl), { signal: AbortSignal.timeout(15000), ...options });
  if (!response.ok) throw new Error(`${options.method ?? 'GET'} ${route} failed with HTTP ${response.status}`);
  return { response, milliseconds: performance.now() - started };
};
const complete = async ({ libraryId, requestId }) => {
  for (let attempt = 0; attempt < 30; attempt++) {
    const status = await request(`/v1/renditions/${requestId}?library=${encodeURIComponent(libraryId)}`, { headers });
    const body = await status.response.json();
    if (body.state === 'completed') return;
    if (['failed', 'retryable', 'unavailable', 'cancelled'].includes(body.state)) throw new Error(`Renderer returned ${body.failureCode ?? body.state}.`);
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error('Timed out waiting for the acceptance rendition.');
};
const starts = [];
for (const fixture of fixtures) {
  const body = JSON.stringify({ libraryId: fixture.libraryId, assetId: fixture.assetId, editId: fixture.editId, selectedVersion: 'current', rendition: 'thumbnail-512', profile: 'render-v1' });
  const created = await request('/v1/renditions', { method: 'POST', headers, body });
  const result = await created.response.json();
  if (!result.requestId || result.cacheHit) throw new Error('Each candidate digest requires an uncached acceptance request.');
  starts.push({ libraryId: fixture.libraryId, requestId: result.requestId, milliseconds: created.milliseconds });
}
for (const item of starts) await complete(item);
const first = starts[0];
const cacheSamples = await Promise.all(Array.from({ length: 20 }, async () => (await request(`/v1/renditions/${first.requestId}/derivative?library=${encodeURIComponent(first.libraryId)}`, { headers })).milliseconds));
starts.sort((left, right) => left.milliseconds - right.milliseconds);
cacheSamples.sort((left, right) => left - right);
const startP95 = starts[Math.ceil(starts.length * 0.95) - 1].milliseconds;
const cacheP95 = cacheSamples[Math.ceil(cacheSamples.length * 0.95) - 1];
if (startP95 > 2000) throw new Error(`Uncached request-start p95 ${startP95.toFixed(1)}ms exceeds 2000ms.`);
if (cacheP95 > 500) throw new Error(`Cache-hit p95 ${cacheP95.toFixed(1)}ms exceeds 500ms.`);
console.log(JSON.stringify({ fixtureCount: fixtures.length, uncachedRequestStartP95Ms: Number(startP95.toFixed(1)), cacheHitP95Ms: Number(cacheP95.toFixed(1)), uncachedStartSamples: starts.map(({ milliseconds }) => Number(milliseconds.toFixed(1))), cacheSamples: cacheSamples.map((value) => Number(value.toFixed(1))) }));
