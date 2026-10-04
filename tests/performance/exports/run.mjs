import { spawnSync } from 'node:child_process';
const result = spawnSync('dotnet', ['run', '--project', 'tests/exports/Platform.Exports.Tests.csproj', '--no-restore'], { stdio: 'inherit', env: { ...process.env, EXPORT_INTEGRATION: '1', EXPORT_PERFORMANCE: '1' } });
process.exit(result.status ?? 1);
