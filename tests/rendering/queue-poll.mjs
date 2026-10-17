import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { resolve } from 'node:path';

// Exercise the real entry point and AWS deserializer, including an idle queue.
let polls = 0;
let deletes = 0;
let complete;
const received = new Promise(resolve => { complete = resolve; });
const server = createServer(async (request, response) => {
  for await (const _ of request) { /* Consume the SDK request. */ }
  const operation = request.headers['x-amz-target']?.split('.').at(-1);
  let body = {};
  if (operation === 'ReceiveMessage') {
    polls++;
    if (polls === 2) body = { Messages: [] };
    if (polls === 3) body = { Messages: [{ MessageId: 'invalid', ReceiptHandle: 'receipt', Body: 'null', MD5OfBody: createHash('md5').update('null').digest('hex') }] };
    if (polls === 4) {
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
    AWS_ACCESS_KEY_ID: 'test', AWS_SECRET_ACCESS_KEY: 'test', AWS_SESSION_TOKEN: '',
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
  assert.equal(polls, 4);
  assert.equal(deletes, 1, 'Invalid work must still be acknowledged after empty polls.');
  assert.equal(worker.exitCode, null);
  console.log('Renderer survives missing/empty message collections and processes subsequent work.');
} finally {
  clearTimeout(timer);
  worker.kill();
  await exited;
  server.closeAllConnections();
  server.close();
}
