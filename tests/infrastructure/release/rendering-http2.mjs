import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createSecureServer, constants } from 'node:http2';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';

const directory = await mkdtemp(join(tmpdir(), 'rendering-http2-'));
const key = join(directory, 'fixture-key.pem');
const certificate = join(directory, 'fixture-cert.pem');
const openssl = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'usr', 'bin', 'openssl.exe') : 'openssl';
const generated = spawnSync(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', certificate, '-days', '1', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'], { encoding: 'utf8' });
assert.equal(generated.status, 0, 'Generate task-local TLS fixture certificate.');
let mode;
let sessions;
let cacheCount;
let active;
let peak;
const server = createSecureServer({ key: await readFile(key), cert: await readFile(certificate) });
server.on('session', session => { sessions++; session.on('error', () => {}); });
server.on('stream', async (stream, headers) => {
  stream.on('error', () => {});
  assert.equal(headers.authorization, 'Bearer fixture-owner');
  const route = headers[':path'];
  if (mode === 'timeout') return;
  let body;
  if (headers[':method'] === 'POST') {
    assert.match(headers['idempotency-key'], /^[A-Za-z0-9_-]{16,128}$/);
    const chunks = [];
    for await (const chunk of stream) chunks.push(chunk);
    assert.equal(JSON.parse(Buffer.concat(chunks)).selectedVersion, 'current');
    body = { requestId: randomUUID(), cacheHit: false };
  } else if (route.includes('/derivative?')) {
    cacheCount++;
    active++;
    peak = Math.max(peak, active);
    if (mode === 'reset') { active--; stream.close(constants.NGHTTP2_INTERNAL_ERROR); return; }
    stream.respond({ ':status': mode === 'status' ? 403 : 200, 'content-type': 'application/json' });
    if (mode === 'invalid') { active--; stream.end('not json'); return; }
    if (mode === 'oversize') { active--; stream.end('x'.repeat(65537)); return; }
    // Headers arrive immediately; full-response timing must include this body delay.
    stream.write('{"locator":');
    setTimeout(() => { active--; if (!stream.destroyed) stream.end('"https://example.invalid/private"}'); }, mode === 'latency' ? 650 : 75);
    return;
  } else body = { state: 'completed' };
  stream.respond({ ':status': 200, 'content-type': 'application/json' });
  stream.end(JSON.stringify(body));
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const fixtures = Array.from({ length: 5 }, () => ({ libraryId: 'main', assetId: randomUUID(), editId: randomUUID() }));
async function run(next, trust = true) {
  mode = next; sessions = 0; cacheCount = 0; active = 0; peak = 0;
  const env = { ...process.env, INGEST_SMOKE_TOKEN: 'fixture-owner', RENDERING_ACCEPTANCE_FIXTURES: JSON.stringify(fixtures) };
  if (trust) env.NODE_EXTRA_CA_CERTS = certificate;
  else delete env.NODE_EXTRA_CA_CERTS;
  const child = spawn(process.execPath, ['scripts/smoke-rendering.mjs', `https://localhost:${server.address().port}`], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', bytes => { output += bytes; });
  child.stderr.on('data', bytes => { output += bytes; });
  const timer = setTimeout(() => child.kill(), 25000);
  try { const [code] = await once(child, 'exit'); return { code, output, sessions, cacheCount, peak }; }
  finally { clearTimeout(timer); }
}
try {
  const success = await run('success');
  assert.equal(success.code, 0, success.output);
  const metrics = JSON.parse(success.output);
  assert.equal(metrics.transport, 'http2');
  assert.equal(metrics.fixtureCount, 5);
  assert.equal(metrics.cacheSamples.length, 20);
  assert.equal(success.cacheCount, 20);
  assert.equal(success.peak, 20, 'Keep all twenty cached requests concurrently in flight.');
  assert.equal(success.sessions, 1, 'Reuse the actual workload session without cache-only prewarming.');
  assert.ok(metrics.cacheHitP95Ms >= 60, 'Include the complete response body, not only headers.');
  for (const failure of ['status', 'invalid', 'oversize', 'reset', 'latency', 'timeout']) {
    const result = await run(failure);
    assert.notEqual(result.code, 0, `${failure} must fail closed: ${result.output}`);
    if (failure === 'latency') {
      assert.match(result.output, /Cache-hit p95 .* exceeds 500ms/);
      assert.match(result.output, /rendering-latency-failure/);
    }
    if (failure === 'timeout') assert.match(result.output, /timed out/);
  }
  const untrusted = await run('untrusted', false);
  assert.notEqual(untrusted.code, 0, 'Reject an untrusted TLS certificate.');
  console.log('Rendering HTTP/2 session reuse, twenty-stream concurrency, full-body timing, TLS and failure checks passed.');
} finally {
  server.close();
  assert.equal(dirname(resolve(directory)), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('rendering-http2-'));
  await rm(directory, { recursive: true, force: true });
}
