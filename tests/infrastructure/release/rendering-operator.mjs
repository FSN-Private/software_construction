import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { devNull } from 'node:os';
import { join } from 'node:path';

const script = await readFile('scripts/deploy-ecs-release.sh', 'utf8');
const start = script.indexOf('# Resolve the canonical operator');
const end = script.indexOf('# End authenticated operator resolution.');
assert.ok(start > 0 && end > start);
assert.ok(end < script.indexOf('API_UPDATED='));
assert.ok(end < script.indexOf('register-task-definition'));
const authentication = script.slice(start, end);
const update = script.split(/\r?\n/).find(line => line.startsWith('API_UPDATED='));
const login = 'fixture-owner@example.invalid';
const canonical = '11111111-1111-1111-1111-111111111111';
const password = 'fixture-only,p=a"ss\\word';
let scenario = 'valid';
let requests = [];
const errors = [];
const server = createServer(async (req, res) => {
  try {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks));
    const operation = req.headers['x-amz-target']?.split('.').at(-1);
    requests.push(operation);
    let result;
    if (operation === 'InitiateAuth') {
      assert.equal(body.AuthFlow, 'USER_PASSWORD_AUTH');
      assert.deepEqual(body.AuthParameters, { USERNAME: login, PASSWORD: password });
      result = { AuthenticationResult: { IdToken: 'fixture-id-token', AccessToken: 'fixture-access-token' } };
      if (scenario === 'no-id') delete result.AuthenticationResult.IdToken;
      if (scenario === 'no-access') delete result.AuthenticationResult.AccessToken;
    } else {
      assert.equal(operation, 'GetUser');
      assert.equal(body.AccessToken, 'fixture-access-token');
      result = { Username: scenario === 'no-user' ? '' : canonical, UserAttributes: [] };
    }
    res.writeHead(200, { 'Content-Type': 'application/x-amz-json-1.1' });
    res.end(JSON.stringify(result));
  } catch (error) {
    errors.push(error.message); res.writeHead(400); res.end('{}');
  }
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
try {
  for (scenario of ['valid', 'no-id', 'no-access', 'no-user']) {
    requests = [];
    const env = { ...process.env };
    for (const key of Object.keys(env)) if (key.startsWith('AWS_')) delete env[key];
    Object.assign(env, { AWS_ACCESS_KEY_ID: 'fixture', AWS_SECRET_ACCESS_KEY: 'fixture', AWS_SESSION_TOKEN: '',
      AWS_DEFAULT_REGION: 'us-east-1', AWS_REGION: 'us-east-1', AWS_EC2_METADATA_DISABLED: 'true',
      AWS_ENDPOINT_URL: `http://127.0.0.1:${server.address().port}`, AWS_IGNORE_CONFIGURED_ENDPOINT_URLS: 'false',
      AWS_CONFIG_FILE: devNull, AWS_SHARED_CREDENTIALS_FILE: devNull,
      COGNITO_OWNER_USERNAME: login, COGNITO_OWNER_PASSWORD: password, COGNITO_CLIENT_ID: 'fixture-client',
      API_TASK: JSON.stringify({ containerDefinitions: [{ name: 'api', environment: [] }] }), ECS_API_CONTAINER: 'api',
      API_IMAGE: 'fixture-image', COGNITO_AUTHORITY: 'https://example.invalid', RENDERER_DIGEST: 'fixture-digest',
      PROMOTION_EVIDENCE_ID: 'fixture-evidence', RELEASE_ID: 'fixture-release', SOURCE_REVISION: 'fixture-source', MANIFEST_DIGEST: 'fixture-manifest' });
    const child = spawn(bash, ['-c', `set -Eeuo pipefail\n${authentication}\n${update}\nprintf '%s' "$API_UPDATED"`], { env, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = ''; let errorOutput = '';
    child.stdout.on('data', data => { output += data; });
    child.stderr.on('data', data => { errorOutput += data; });
    const timer = setTimeout(() => child.kill(), 30000);
    let code;
    try { [code] = await once(child, 'exit'); } finally { clearTimeout(timer); }
    if (scenario === 'valid') {
      assert.equal(code, 0, errorOutput);
      const settings = JSON.parse(output).containerDefinitions[0].environment;
      assert.equal(settings.find(item => item.name === 'Rendering__OperationsUsername').value, canonical);
      assert.deepEqual(requests, ['InitiateAuth', 'GetUser']);
    } else {
      assert.notEqual(code, 0);
      assert.equal(output, '');
      assert.equal(requests.length, scenario === 'no-user' ? 2 : 1);
    }
    assert.ok(!output.includes('fixture-id-token') && !output.includes('fixture-access-token'));
    assert.ok(!errorOutput.includes('fixture-id-token') && !errorOutput.includes('fixture-access-token'));
    assert.deepEqual(errors, []);
  }
  console.log('Cognito login/canonical operator separation and missing-identity fail-closed checks passed.');
} finally {
  server.closeAllConnections(); server.close();
}
