import { strict as assert } from 'node:assert';
import { createHash, randomUUID } from 'node:crypto';
import { createServer } from 'node:http';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative, isAbsolute } from 'node:path';
import { once } from 'node:events';
import { bootstrapRenderingFixtures, renderingFixtureInputs } from '../../../scripts/bootstrap-rendering-fixtures.mjs';

const inputs = await renderingFixtureInputs();
for (const [index, exposure] of [1, -1, -0.5, 0.5, 0].entries()) {
  const params = Buffer.from(inputs.xmps[index].toString().match(/darktable:params="([a-f0-9]+)"/)[1], 'hex');
  assert.equal(params.readInt32LE(0), 0, 'Exposure mode must be manual.');
  assert.equal(params.readFloatLE(4), 0, 'Exposure fixtures must retain neutral black correction.');
  assert.equal(params.readFloatLE(8), exposure);
  assert.equal(params.readFloatLE(12), 50);
  assert.equal(params.readFloatLE(16), -4);
}

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const assetId = randomUUID();
const batches = new Map();
const working = [];
let rejectPublication = false;
let wrongAssociation = false;
let parts = 0;
const server = createServer(async (req, res) => {
  try {
    assert.equal(req.headers.authorization, 'Bearer test-owner-token');
    assert.ok(req.headers['idempotency-key']);
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const bytes = Buffer.concat(chunks);
    const path = req.url;
    let body = {};
    if (req.method === 'POST' && path === '/v1/import-batches') {
      const item = JSON.parse(bytes).items[0];
      assert.ok(item.path.endsWith('.dng') || /sample(?:_0[1-4])?\.dng\.xmp$/.test(item.path));
      const id = randomUUID();
      batches.set(id, { ...item, bytes: [], editId: item.path.endsWith('.xmp') ? randomUUID() : null });
      body = { id, partSize: 8 * 1024 * 1024, items: [{ id }] };
    } else if (req.method === 'GET' && path.startsWith('/v1/import-batches/')) {
      const item = batches.get(path.split('/').at(-1));
      assert.equal(digest(Buffer.concat(item.bytes)), item.sha256);
      assert.equal(Buffer.concat(item.bytes).length, item.length);
      body = { items: [{ state: rejectPublication ? 'quarantined' : 'active',
        assetId: wrongAssociation && item.editId ? randomUUID() : assetId, editId: item.editId }] };
    } else if (path.startsWith('/v1/upload-sessions/')) {
      assert.equal(req.headers['if-match'], '"1"');
      const item = batches.get(path.split('/')[3]);
      assert.ok(item);
      if (req.method === 'PUT') {
        assert.equal(digest(bytes), req.headers['x-content-sha256']);
        assert.equal(Number(path.split('/').at(-1)), item.bytes.length + 1);
        assert.ok(bytes.length <= 8 * 1024 * 1024);
        item.bytes.push(bytes); parts++;
      }
    } else if (req.method === 'PUT' && path === '/v1/libraries/main/version-control/working') {
      assert.equal(req.headers['x-client-id'], 'deployment-rendering-fixtures');
      body = JSON.parse(bytes);
      assert.equal(body.assetId, assetId);
      assert.equal(body.expectedVersion, 0);
      assert.equal(body.change, 'added');
      assert.equal(body.contentSha256, [...batches.values()].find(item => item.editId === body.editId).sha256);
      working.push(body);
    } else throw new Error(`Unexpected request ${req.method} ${path}`);
    res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body));
  } catch (error) { res.writeHead(400).end(String(error)); }
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const directory = await mkdtemp(join(tmpdir(), 'rendering-fixture-check-'));
const output = join(directory, 'fixtures.json');
const baseUrl = `http://127.0.0.1:${server.address().port}`;
try {
  await bootstrapRenderingFixtures(baseUrl, 'test-owner-token', output);
  const record = JSON.parse(await readFile(output, 'utf8'));
  assert.equal(record.fixtures.length, 5);
  assert.equal(new Set(record.fixtures.map(item => item.editId)).size, 5);
  assert.equal(new Set(record.xmpSha256).size, 5);
  assert.equal(working.length, 5);
  assert.equal(parts, 8, 'One three-part DNG and five single-part sidecars.');
  rejectPublication = true;
  await assert.rejects(bootstrapRenderingFixtures(baseUrl, 'test-owner-token', output), /quarantined/);
  rejectPublication = false; wrongAssociation = true;
  await assert.rejects(bootstrapRenderingFixtures(baseUrl, 'test-owner-token', output), /association differs/);
  assert.ok(!(await readFile(output, 'utf8')).includes('test-owner-token'));
  console.log('Rendering fixture upload checksums, multipart, association, owner context and failure checks passed.');
} finally {
  server.closeAllConnections(); server.close();
  const child = relative(tmpdir(), directory);
  assert.ok(child && !child.startsWith('..') && !isAbsolute(child));
  await rm(directory, { recursive: true, force: true });
}
