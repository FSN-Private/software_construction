import { connect } from 'node:http2';
import { performance } from 'node:perf_hooks';
import { cpus } from 'node:os';

// Public read-only diagnostic. HTTP errors are measurements, never acceptance.
const origin = 'https://vck1d7xax9.execute-api.ap-southeast-1.amazonaws.com';
const session = connect(origin);
session.on('error', () => {}); // Pending streams report connection failures.
const deadline = setTimeout(() => session.destroy(new Error('Diagnostic deadline exceeded.')), 45000);
const request = () => new Promise((resolve, reject) => {
  const started = performance.now();
  const stream = session.request({ ':path': '/live' });
  let status, bytes = 0;
  const timeout = setTimeout(() => stream.destroy(new Error('Response timed out.')), 15000);
  stream.on('response', headers => { status = headers[':status']; });
  stream.on('data', chunk => {
    bytes += chunk.length;
    if (bytes > 65536) stream.destroy(new Error('Response exceeded diagnostic bound.'));
  });
  stream.on('error', reject);
  stream.on('close', () => {
    clearTimeout(timeout);
    if (!stream.readableEnded) reject(new Error('Response closed before completion.'));
  });
  stream.on('end', () => resolve({ status, bytes, milliseconds: Number((performance.now() - started).toFixed(1)) }));
  stream.end();
});
try {
  console.log(JSON.stringify({ qualification: false, origin, runner: process.env.RUNNER_NAME ?? 'local', cpu: cpus()[0]?.model, node: process.version }));
  console.log(JSON.stringify({ phase: 'connection-and-first-response', result: await request(), alpn: session.alpnProtocol, remoteSettings: session.remoteSettings }));
  for (let sample = 1; sample <= 3; sample++) {
    const result = await new Promise(resolve => {
      const timeout = setTimeout(() => resolve({ unavailable: 'PING acknowledgement timed out' }), 5000);
      session.ping((error, milliseconds) => {
        clearTimeout(timeout);
        resolve(error ? { unavailable: error.code ?? 'PING failed' } : { milliseconds: Number(milliseconds.toFixed(1)) });
      });
    });
    console.log(JSON.stringify({ phase: 'http2-ping', sample, ...result }));
  }
  const results = await Promise.all(Array.from({ length: 20 }, request));
  console.log(JSON.stringify({ phase: 'twenty-concurrent-public-liveness-responses', qualification: false, results }));
} finally {
  clearTimeout(deadline);
  session.destroy();
}
