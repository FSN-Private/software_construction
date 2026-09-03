import { execFileSync } from 'node:child_process';

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
} else if (category === 'all') {
  assertToolchain();
  throw new Error('The complete suite is not registered yet. Run individual commands only after their owning feature task is complete.');
} else if (categories.has(category)) {
  throw new Error(`The ${category} validation category is not registered yet. Its owning task must add a local equivalent before CI uses it.`);
} else {
  throw new Error('Usage: node scripts/validate.mjs <toolchain|all|format|lint|typecheck|test|integration|build|contract|infrastructure|security>.');
}
