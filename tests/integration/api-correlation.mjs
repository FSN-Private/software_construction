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
      response = await fetch(`${address}/live`, { headers: { 'X-Operation-Id': operationId } });
      break;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
  }

  if (response === undefined) throw new Error('The API did not start within 10 seconds.');
  if (response.status !== 200) throw new Error(`Expected unauthenticated liveness to return 200; received ${response.status}.`);
  if (response.headers.get('X-Operation-Id') !== operationId) throw new Error('The API did not preserve the valid caller operation ID.');
  const live = await response.json();
  if (live.status !== 'live' || live.contractVersion !== 'v1') throw new Error('Liveness must return the versioned safe contract.');
  const protectedResponse = await fetch(`${address}/v1/version`, { headers: { 'X-Operation-Id': operationId } });
  if (protectedResponse.status !== 401 || protectedResponse.headers.get('content-type')?.includes('application/problem+json') !== true) {
    throw new Error('Unauthenticated version access must return the generic v1 problem response.');
  }
  const problem = await protectedResponse.json();
  if (problem.code !== 'unauthenticated' || problem.operationId !== operationId || Object.keys(problem).some((key) => /token|secret|credential/i.test(key))) {
    throw new Error('Unauthenticated responses must retain operation correlation without sensitive detail.');
  }
  console.log('API correlation integration test passed.');
} finally {
  api.kill();
}
