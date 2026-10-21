import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { bindings, functionArn, ingestMetrics, latencyFailure, renderingMetrics, validateRequest } from './contract.mjs';

const root = dirname(fileURLToPath(import.meta.url));
export async function runSmoke(script, args, env, timeout = 120000) {
  const result = await promisify(execFile)(process.execPath, [join(root, script), ...args], {
    cwd: root, env, timeout, maxBuffer: 65536, killSignal: 'SIGKILL', encoding: 'utf8',
  });
  return JSON.parse(result.stdout);
}

export async function handler(event, context) {
  // Invalid input/configuration fails generically before executing any child.
  try {
    validateRequest(event);
    if (process.env.AWS_REGION !== 'ap-southeast-1' || process.env.TARGET_ORIGIN !== event.origin ||
        !functionArn.test(context?.invokedFunctionArn ?? '') || context.functionVersion !== context.invokedFunctionArn.split(':').at(-1)) throw new Error();
  } catch { throw new Error('Invalid regional acceptance request or configuration.'); }
  const proof = { ...bindings(event), region: process.env.AWS_REGION, functionArn: context.invokedFunctionArn, runtime: process.version };
  // The children need HTTP authentication, not Lambda's AWS role credentials.
  const env = { PATH: process.env.PATH, INGEST_SMOKE_TOKEN: event.token, RENDERING_ACCEPTANCE_FIXTURES: JSON.stringify(event.fixtures) };
  let stage = 'rendering';
  try {
    const rendering = renderingMetrics(await runSmoke('smoke-rendering.mjs', [event.origin], env));
    stage = 'ingest';
    const ingest = ingestMetrics(await runSmoke('smoke-ingest.mjs', [event.origin, join(root, 'sample.png')], env));
    return { ...proof, passed: true, rendering, ingest };
  } catch (error) {
    const metrics = stage === 'rendering' ? latencyFailure(error.stderr) : undefined;
    return { ...proof, passed: false, stage, ...(metrics ? { metrics } : {}) };
  }
}
