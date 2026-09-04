import { spawn } from 'node:child_process';

const address = 'http://127.0.0.1:5073';
const operationId = 'op_integration-test_01';
const api = spawn('dotnet', ['run', '--no-build', '--project', 'apps/api/Platform.Api.csproj', '--', '--urls', address], {
  stdio: ['ignore', 'pipe', 'pipe'],
});

try {
  let response;
  for (let attempt = 0; attempt < 40; attempt += 1) {
    try {
      response = await fetch(`${address}/not-a-product-endpoint`, { headers: { 'X-Operation-Id': operationId } });
      break;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
  }

  if (response === undefined) throw new Error('The API did not start within 10 seconds.');
  if (response.status !== 404) throw new Error(`Expected middleware test request to return 404; received ${response.status}.`);
  if (response.headers.get('X-Operation-Id') !== operationId) throw new Error('The API did not preserve the valid caller operation ID.');
  console.log('API correlation integration test passed.');
} finally {
  api.kill();
}
