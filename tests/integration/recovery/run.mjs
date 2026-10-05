import { spawnSync } from 'node:child_process';

const result = spawnSync('dotnet', [
  'run',
  '--project', 'tests/unit/Platform.Api.Foundation.Tests/Platform.Api.Foundation.Tests.csproj',
  '--no-restore',
], {
  stdio: 'inherit',
  env: { ...process.env, INGEST_INTEGRATION: '1' },
});

if (result.error) throw result.error;
process.exit(result.status ?? 1);
