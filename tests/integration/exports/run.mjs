import { spawnSync } from 'node:child_process';
const result = spawnSync('dotnet', ['run', '--project', 'tests/exports/Platform.Exports.Tests.csproj'], {
  stdio: 'inherit', env: { ...process.env, EXPORT_INTEGRATION: '1' },
});
if (result.error) throw result.error;
process.exit(result.status ?? 1);
