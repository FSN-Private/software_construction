import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join } from 'node:path';

const operations = new Map();
const submissions = [];
const rejected = [];
let pauseAttempts = 0;
const server = createServer(async (req, res) => {
  try {
    assert.equal(req.headers.authorization, 'Bearer test-owner');
    if (req.method === 'POST') assert.match(req.headers['idempotency-key'] ?? '', /^[A-Za-z0-9_-]{16,128}$/);
    let body = {};
    if (req.url.startsWith('/v1/rendering/operations/')) {
      const action = req.url.split('/').at(-1);
      const key = req.headers['idempotency-key'];
      if (operations.has(action)) assert.equal(operations.get(action), key, 'Curl retries must reuse the logical operation key.');
      operations.set(action, key);
      if (action === 'pause' && ++pauseAttempts === 1) {
        res.writeHead(500); res.end(); return;
      }
    } else if (req.method === 'POST' && req.url === '/v1/renditions') {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      submissions.push({ key: req.headers['idempotency-key'], request: JSON.parse(Buffer.concat(chunks)) });
      body = { requestId: randomUUID(), cacheHit: false };
    } else if (req.url.includes('/derivative?')) {
      body = { url: 'https://example.invalid/private-test' };
    } else {
      assert.match(req.url, /^\/v1\/renditions\/[^/]+\?library=/);
      body = { state: 'completed' };
    }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(body));
  } catch (error) {
    rejected.push(error.message);
    res.writeHead(400); res.end();
  }
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const baseUrl = `http://127.0.0.1:${server.address().port}`;
async function run(command, args, env) {
  const child = spawn(command, args, { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', data => { output += data; });
  child.stderr.on('data', data => { output += data; });
  const timer = setTimeout(() => child.kill(), 30000);
  try {
    const [code] = await once(child, 'exit');
    assert.equal(code, 0, `${output}\n${rejected.join('\n')}`);
  } finally { clearTimeout(timer); }
}
try {
  const script = await readFile('scripts/deploy-ecs-release.sh', 'utf8');
  const lines = script.split(/\r?\n/);
  const initialize = lines.find(line => line.startsWith('RENDER_OPERATION_ID=')) ?? '';
  const controls = lines.filter(line => line.includes('curl ') && line.includes('--request POST') && line.includes('/v1/rendering/operations/'));
  assert.equal(controls.length, 6);
  const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
  await run(bash, ['-c', `set -e\n${initialize}\n${controls.join('\n')}`], { HEALTH_URL: baseUrl, ID_TOKEN: 'test-owner' });
  assert.deepEqual([...operations.keys()].sort(), ['abort', 'activate', 'admit', 'drain', 'pause', 'resume']);
  assert.equal(new Set(operations.values()).size, 6, 'Separate mutations must use separate keys.');
  assert.equal(pauseAttempts, 2, 'Exercise a real curl retry.');
  const fixtures = Array.from({ length: 5 }, () => ({ libraryId: 'main', assetId: randomUUID(), editId: randomUUID() }));
  await run(process.execPath, ['scripts/smoke-rendering.mjs', baseUrl], { INGEST_SMOKE_TOKEN: 'test-owner', RENDERING_ACCEPTANCE_FIXTURES: JSON.stringify(fixtures) });
  assert.equal(submissions.length, 5);
  assert.equal(new Set(submissions.map(item => item.key)).size, 5);
  assert.equal(rejected.length, 0);
  console.log('Rendering control retries and smoke submissions satisfy the mutation key contract.');
} finally {
  server.closeAllConnections();
  server.close();
}
