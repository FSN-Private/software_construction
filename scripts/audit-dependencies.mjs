import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';

const attempts = 3;
const attemptTimeoutMs = 45_000;
const retryDelayMs = 2_000;

function auditArguments(directory, options = []) {
  const prefix = directory === '.' ? [] : ['--prefix', directory];
  return [...prefix, 'audit', '--json', '--audit-level=high', '--fetch-retries=0', '--fetch-timeout=30000', ...options];
}

function hasHighSeverityVulnerability(output) {
  try {
    const report = JSON.parse(output);
    const vulnerabilities = report.metadata?.vulnerabilities;
    return (vulnerabilities?.high ?? 0) > 0 || (vulnerabilities?.critical ?? 0) > 0;
  } catch {
    return false;
  }
}

export function isTransientAuditFailure(output) {
  return /\b(?:502|503|504)\b|EAI_AGAIN|ECONN(?:RESET|REFUSED|ABORTED)|ENOTFOUND|ETIMEDOUT|fetch failed|network timeout|audit endpoint returned an error/i.test(output);
}

function executeNpm(args) {
  return new Promise((resolve) => {
    const configuredNpmCli = process.env.npm_execpath;
    const npmCli = configuredNpmCli?.endsWith('npm-cli.js')
      ? configuredNpmCli
      : join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
    const command = process.platform === 'win32' ? process.execPath : 'npm';
    const commandArgs = process.platform === 'win32' ? [npmCli, ...args] : args;
    const child = spawn(command, commandArgs, { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    const timeout = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, attemptTimeoutMs);

    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', (error) => {
      clearTimeout(timeout);
      resolve({ code: 1, output: error.message, timedOut: false });
    });
    child.on('close', (code) => {
      clearTimeout(timeout);
      resolve({ code, output: `${stdout}${stderr}`, timedOut });
    });
  });
}

export async function runAudit(directory, options = [], execute = executeNpm, wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds))) {
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    const result = await execute(auditArguments(directory, options));
    if (result.code === 0) {
      console.log(`Dependency audit passed for ${directory}.`);
      return;
    }

    if (hasHighSeverityVulnerability(result.output)) {
      throw new Error(`Dependency audit found a high or critical vulnerability in ${directory}.\n${result.output}`);
    }

    const transient = result.timedOut || isTransientAuditFailure(result.output);
    if (!transient || attempt === attempts) {
      const reason = transient ? 'after bounded retries' : 'with a non-transient error';
      throw new Error(`Dependency audit failed for ${directory} ${reason}.\n${result.output}`);
    }

    console.warn(`Transient dependency-audit failure for ${directory}; retrying (${attempt}/${attempts}).`);
    await wait(retryDelayMs * attempt);
  }
}

if (import.meta.main) {
  const inputs = process.argv.slice(2);
  const options = inputs.filter((input) => input.startsWith('--'));
  const directories = inputs.filter((input) => !input.startsWith('--'));
  for (const directory of directories.length === 0 ? ['.', 'apps/web', 'packages/infrastructure'] : directories) {
    await runAudit(directory, options);
  }
}
