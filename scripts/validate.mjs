import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';

const expected = {
  dotnet: '10.0.400',
  node: 'v24.20.0',
};

const category = process.argv[2];
const categories = new Set([
  'format',
  'lint',
  'typecheck',
  'test',
  'integration',
  'build',
  'contract',
  'infrastructure',
  'security',
]);

function installedDotnetVersion() {
  return execFileSync('dotnet', ['--version'], { encoding: 'utf8' }).trim();
}

function assertToolchain() {
  const actualDotnet = installedDotnetVersion();
  if (actualDotnet !== expected.dotnet) {
    throw new Error(`Expected .NET SDK ${expected.dotnet}; found ${actualDotnet}.`);
  }
  if (process.version !== expected.node) {
    throw new Error(`Expected Node ${expected.node}; found ${process.version}.`);
  }
  console.log(`Toolchain verified: .NET ${actualDotnet}, Node ${process.version}.`);
}

if (category === 'toolchain') {
  assertToolchain();
} else if (category === 'recovery') {
  assertToolchain();
  execFileSync('dotnet', ['run', '--project', 'tests/recovery/Platform.Recovery.Tests.csproj'], { stdio: 'inherit' });
  execFileSync('dotnet', ['publish', 'apps/recovery-worker/Platform.Recovery.Worker.csproj', '--configuration', 'Release',
    '--no-restore', '--output', 'artifacts/recovery-worker'], { stdio: 'inherit' });
  execFileSync(process.execPath, ['tests/infrastructure/release/recovery-qualification-cli.mjs'], { stdio: 'inherit' });
  execFileSync(process.execPath, ['tests/integration/recovery/run.mjs'], { stdio: 'inherit' });
  execFileSync(process.execPath, ['tests/infrastructure/release/recovery-release-evidence.mjs'], { stdio: 'inherit' });
  execFileSync(process.execPath, ['tests/infrastructure/release/recovery-drill.mjs'], { stdio: 'inherit' });
  const npmCommand = process.platform === 'win32' ? process.execPath : 'npm';
  const npmArguments = process.platform === 'win32'
    ? [process.env.npm_execpath ?? join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js')]
    : [];
  execFileSync(npmCommand, [...npmArguments, 'run', 'test:recovery-infrastructure'], { stdio: 'inherit' });
} else if (category === 'all') {
  assertToolchain();
  throw new Error('The complete suite is not registered yet. Run individual commands only after their owning feature task is complete.');
} else if (categories.has(category)) {
  throw new Error(`The ${category} validation category is not registered yet. Its owning task must add a local equivalent before CI uses it.`);
} else {
  throw new Error('Usage: node scripts/validate.mjs <toolchain|recovery|all|format|lint|typecheck|test|integration|build|contract|infrastructure|security>.');
}
