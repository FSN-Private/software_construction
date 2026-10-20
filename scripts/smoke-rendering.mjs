import { performance } from 'node:perf_hooks';
import { randomUUID } from 'node:crypto';
import { connect } from 'node:http2';

const [baseUrl] = process.argv.slice(2);
const token = process.env.INGEST_SMOKE_TOKEN;
const fixtures = JSON.parse(process.env.RENDERING_ACCEPTANCE_FIXTURES ?? 'null');
if (!baseUrl || !token || !Array.isArray(fixtures) || fixtures.length < 5 || fixtures.some(({ libraryId, assetId, editId } = {}) => !libraryId || !assetId || !editId)) throw new Error('Set RENDERING_ACCEPTANCE_FIXTURES to at least 5 distinct {libraryId,assetId,editId} values.');
if (new Set(fixtures.map(({ libraryId, assetId, editId }) => `${libraryId}/${assetId}/${editId}`)).size !== fixtures.length) throw new Error('Rendering acceptance fixtures must be distinct.');

const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
// Reuse the session established by the actual uncached workload, as a browser does.
const session = connect(new URL(baseUrl).origin);
session.on('error', () => {}); // Each pending stream rejects on connection failure.
const request = (route, options = {}) => new Promise((resolve, reject) => {
  const started = performance.now();
  const url = new URL(route, baseUrl);
  const stream = session.request({ ':method': options.method ?? 'GET', ':path': url.pathname + url.search, ...headers, ...options.headers });
  let status;
  let bytes = 0;
  const chunks = [];
  const timer = setTimeout(() => stream.destroy(new Error('Rendering HTTP/2 request timed out.')), 15000);
  stream.on('response', values => { status = values[':status']; });
  stream.on('data', chunk => {
    bytes += chunk.length;
    if (bytes > 65536) { stream.destroy(new Error('Rendering response exceeded its JSON bound.')); return; }
    chunks.push(chunk);
  });
  stream.on('error', reject);
  stream.on('close', () => {
    clearTimeout(timer);
    if (!stream.readableEnded) reject(new Error('Rendering HTTP/2 response closed before completion.'));
  });
  stream.on('end', () => {
    clearTimeout(timer);
    if (!(status >= 200 && status < 300)) { reject(new Error(`${options.method ?? 'GET'} ${route} failed with HTTP ${status}`)); return; }
    try { resolve({ body: JSON.parse(Buffer.concat(chunks).toString('utf8')), milliseconds: performance.now() - started }); }
    catch { reject(new Error('Rendering response was not valid JSON.')); }
  });
  stream.end(options.body);
});
try {
  const complete = async ({ libraryId, requestId, deadline }) => {
    while (Date.now() < deadline) {
      const status = await request(`/v1/renditions/${requestId}?library=${encodeURIComponent(libraryId)}`, { headers });
      const body = status.body;
      if (body.state === 'completed') {
        if (Date.now() > deadline) throw new Error('Renderer completed after the 30-second acceptance deadline.');
        return;
      }
      if (['failed', 'retryable', 'unavailable', 'cancelled'].includes(body.state)) throw new Error(`Renderer returned ${body.failureCode ?? body.state}.`);
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
    throw new Error('Renderer did not complete within 30 seconds of request creation.');
  };
  const starts = [];
  for (const fixture of fixtures) {
    const body = JSON.stringify({ libraryId: fixture.libraryId, assetId: fixture.assetId, editId: fixture.editId, selectedVersion: 'current', rendition: 'thumbnail-512', profile: 'render-v1' });
    const submittedAt = Date.now();
    const created = await request('/v1/renditions', { method: 'POST', headers: { ...headers, 'Idempotency-Key': randomUUID() }, body });
    const result = created.body;
    if (!result.requestId || result.cacheHit) throw new Error('Each candidate digest requires an uncached acceptance request.');
    starts.push({ libraryId: fixture.libraryId, requestId: result.requestId, milliseconds: created.milliseconds, deadline: submittedAt + 30000 });
  }
  await Promise.all(starts.map(complete));
  const first = starts[0];
  const cacheSamples = await Promise.all(Array.from({ length: 20 }, async () => (await request(`/v1/renditions/${first.requestId}/derivative?library=${encodeURIComponent(first.libraryId)}`, { headers })).milliseconds));
  starts.sort((left, right) => left.milliseconds - right.milliseconds);
  cacheSamples.sort((left, right) => left - right);
  const startP95 = starts[Math.ceil(starts.length * 0.95) - 1].milliseconds;
  const cacheP95 = cacheSamples[Math.ceil(cacheSamples.length * 0.95) - 1];
  const metrics = { transport: 'http2', cacheConcurrency: 20, fixtureCount: fixtures.length, uncachedRequestStartP95Ms: Number(startP95.toFixed(1)), cacheHitP95Ms: Number(cacheP95.toFixed(1)), uncachedStartSamples: starts.map(({ milliseconds }) => Number(milliseconds.toFixed(1))), cacheSamples: cacheSamples.map((value) => Number(value.toFixed(1))) };
  if (startP95 > 2000 || cacheP95 > 500) console.error(JSON.stringify({ phase: 'rendering-latency-failure', ...metrics }));
  if (startP95 > 2000) throw new Error(`Uncached request-start p95 ${startP95.toFixed(1)}ms exceeds 2000ms.`);
  if (cacheP95 > 500) throw new Error(`Cache-hit p95 ${cacheP95.toFixed(1)}ms exceeds 500ms.`);
  console.log(JSON.stringify(metrics));
} finally { session.destroy(); }
