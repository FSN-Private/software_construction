import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, createHmac } from 'node:crypto';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { resolve } from 'node:path';

// Verify the signature of the actual wire request, independently of the SDK signer.
const hash = value => createHash('sha256').update(value).digest('hex');
const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
function verifyCallback(request, body, sessionToken) {
  const auth = /^AWS4-HMAC-SHA256 Credential=test\/([^,]+), SignedHeaders=([^,]+), Signature=([a-f0-9]{64})$/.exec(request.headers.authorization ?? '');
  assert.ok(auth, 'Callback must use SigV4 with the fixture access key.');
  const [date, region, service, terminator] = auth[1].split('/');
  assert.equal(region, 'us-east-1');
  assert.equal(service, 'execute-api');
  assert.equal(terminator, 'aws4_request');
  const signed = auth[2].split(';');
  assert.ok(signed.includes('idempotency-key'));
  assert.equal(request.headers['x-amz-security-token'] ?? '', sessionToken);
  if (sessionToken) assert.ok(signed.includes('x-amz-security-token'));
  const headers = signed.map(name => `${name}:${request.headers[name].trim().replace(/\s+/g, ' ')}\n`).join('');
  const canonical = [request.method, request.url, '', headers, auth[2], hash(body)].join('\n');
  const stringToSign = ['AWS4-HMAC-SHA256', request.headers['x-amz-date'], auth[1], hash(canonical)].join('\n');
  const signingKey = hmac(hmac(hmac(hmac('AWS4test', date), region), service), terminator);
  assert.equal(auth[3], createHmac('sha256', signingKey).update(stringToSign).digest('hex'), 'Callback signature must match the actual path, headers and body.');
}

// Exercise the real entry point and AWS deserializer with both credential types.
async function checkWorker(sessionToken) {
let polls = 0;
let deletes = 0;
const callbacks = [];
const callbackKeys = new Set();
const fixtureRequest = {
  OwnerId: 'owner-1', LibraryId: 'library-1', AssetId: '11111111-1111-1111-1111-111111111111', EditId: '22222222-2222-2222-2222-222222222222',
  SelectedVersion: 'current', Rendition: 0, Profile: 'render-v1', RendererRelease: '5.6.1', RendererArtifactDigest: 'unapproved',
  CompatibilityMatrix: 'rendering-v1', SourceSha256: 'a'.repeat(64), XmpSha256: 'b'.repeat(64),
};
const fixtureKey = createHash('sha256').update(['rendering-v1', ...Object.values(fixtureRequest).map(value => value === 0 ? 'Thumbnail512' : value)].join('\n')).digest('hex');
const callbackWork = JSON.stringify({
  RequestId: fixtureRequest.AssetId, AttemptId: fixtureRequest.EditId, Request: fixtureRequest,
  Original: { Key: 'original', Version: 'v1', Sha256: 'a'.repeat(64), ByteLength: 1 },
  Xmp: { Key: 'xmp', Version: 'v1', Sha256: 'b'.repeat(64), ByteLength: 1 },
  DerivativeKey: `derivatives/${fixtureKey}.jpg`, Policy: { TimeoutSeconds: 30, MemoryMiB: 1024, EphemeralStorageMiB: 2048, MaxConcurrentRenders: 1 },
});
let complete;
const received = new Promise(resolve => { complete = resolve; });
const server = createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const bodyBytes = Buffer.concat(chunks);
  if (request.url.startsWith('/internal/render-callbacks/')) {
    const key = request.headers['idempotency-key'] ?? '';
    if (!/^[A-Za-z0-9_-]{16,128}$/.test(key)) { response.writeHead(400); response.end(); return; }
    try { verifyCallback(request, bodyBytes, sessionToken); }
    catch (error) { output += error.message; response.writeHead(403); response.end(); return; }
    callbacks.push(request.url.split('/').at(-1));
    callbackKeys.add(key);
    response.writeHead(200); response.end(); return;
  }
  const operation = request.headers['x-amz-target']?.split('.').at(-1);
  let body = {};
  if (operation === 'ReceiveMessage') {
    polls++;
    if (polls === 2) body = { Messages: [] };
    if (polls === 3) body = { Messages: [{ MessageId: 'invalid', ReceiptHandle: 'receipt', Body: 'null', MD5OfBody: createHash('md5').update('null').digest('hex') }] };
    if (polls === 4) body = { Messages: [{ MessageId: 'incompatible', ReceiptHandle: 'receipt-2', Body: callbackWork, MD5OfBody: createHash('md5').update(callbackWork).digest('hex') }] };
    if (polls === 5) {
      complete();
      return; // Keep the worker blocked until the test terminates it.
    }
  } else if (operation === 'DeleteMessage') {
    deletes++;
  }
  response.writeHead(200, { 'Content-Type': 'application/x-amz-json-1.0' });
  response.end(JSON.stringify(body));
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const endpoint = `http://127.0.0.1:${server.address().port}`;
const worker = spawn('dotnet', [resolve('apps/render-worker/bin/Debug/net10.0/Platform.Render.Worker.dll')], {
  env: {
    ...process.env,
    AWS_ACCESS_KEY_ID: 'test', AWS_SECRET_ACCESS_KEY: 'test', AWS_SESSION_TOKEN: sessionToken,
    AWS_REGION: 'us-east-1', AWS_EC2_METADATA_DISABLED: 'true',
    AWS_ENDPOINT_URL_SQS: endpoint,
    Rendering__QueueUrl: `${endpoint}/123456789012/test`,
    Rendering__SourceBucket: 'test-source', Rendering__DerivativeBucket: 'test-derivatives',
    Rendering__WorkspaceRoot: resolve('artifacts/rendering-queue-poll'),
    Rendering__CallbackBaseUrl: endpoint, Rendering__AwsRegion: 'us-east-1',
    Rendering__DeployedArtifactDigest: `sha256:${'a'.repeat(64)}`,
  },
  stdio: ['ignore', 'pipe', 'pipe'],
});
let output = '';
worker.stdout.on('data', data => { output += data; });
worker.stderr.on('data', data => { output += data; });
const exited = once(worker, 'exit');
let timer;
try {
  await Promise.race([
    received,
    exited.then(([code, signal]) => { throw new Error(`Worker exited (${code ?? signal}): ${output}`); }),
    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`Queue poll timed out: ${output}`)), 15000); }),
  ]);
  assert.equal(polls, 5);
  assert.equal(deletes, 2, 'Invalid and incompatible work must be acknowledged after empty polls.');
  assert.deepEqual(callbacks, ['claim', 'fail']);
  assert.equal(callbackKeys.size, 2, 'Callbacks must carry distinct signed mutation keys.');
  assert.equal(worker.exitCode, null);
  console.log(`Renderer continues polling with verified ${sessionToken ? 'session' : 'basic'} credential callbacks.`);
} finally {
  clearTimeout(timer);
  worker.kill();
  await exited;
  server.closeAllConnections();
  server.close();
}
}
for (const sessionToken of ['', 'dummy-session-token']) await checkWorker(sessionToken);
