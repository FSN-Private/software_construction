import { spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, relative, isAbsolute } from 'node:path';
import { randomUUID } from 'node:crypto';

// Local ephemeral databases only. No published ports or AWS credentials.
const project = 'tests/integration/recovery-native/Platform.Recovery.Native.Tests.csproj';
const output = resolve('artifacts/recovery-native');
const fixture = realpathSync(mkdtempSync(join(tmpdir(), 'recovery-native-')));
const suffix = randomUUID().replaceAll('-', '');
const network = `recovery-native-${suffix}`;
const database = `recovery-db-${suffix}`;
const postgres = 'postgres:18.4-bookworm@sha256:882236b897e39051d2368c5ccc6cda944904723506b2dfc97f2a8f5bc9afa382';
const password = randomUUID();
const run = (command, arguments_, options = {}) => {
  const result = spawnSync(command, arguments_, { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024, ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed (${result.status}): ${result.stderr ?? ''}`);
  return result.stdout;
};
let createdNetwork = false;
let createdDatabase = false;
try {
  run('dotnet', ['restore', project, '--locked-mode'], { stdio: 'inherit' });
  run('dotnet', ['publish', project, '--configuration', 'Release', '--no-restore', '--output', output], { stdio: 'inherit' });
  run('dotnet', [join(output, 'Platform.Recovery.Native.Tests.dll'), '--write-certificates', fixture]);
  // The non-root worker must traverse the bind mount to read public trust roots.
  // Keep the generated server private key readable only by the fixture owner.
  chmodSync(join(fixture, 'server-key.pem'), 0o600);
  chmodSync(fixture, 0o755);
  run('docker', ['network', 'create', '--internal', network]);
  createdNetwork = true;
  run('docker', ['run', '--detach', '--name', database, '--network', network,
    '--network-alias', 'recovery-postgres', '--network-alias', 'recovery-postgres-wrong',
    '--mount', `type=bind,source=${fixture},target=/fixtures,readonly`,
    '--env', `POSTGRES_PASSWORD=${password}`, '--env', 'POSTGRES_DB=recovery_source',
    '--entrypoint', 'bash', postgres, '-ceu',
    'install -o postgres -g postgres -m 600 /fixtures/server-key.pem /tmp/server-key.pem; install -o postgres -g postgres -m 644 /fixtures/server.pem /tmp/server.pem; exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/tmp/server.pem -c ssl_key_file=/tmp/server-key.pem']);
  createdDatabase = true;
  let ready = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    // The temporary initialization server accepts Unix sockets before TCP is ready.
    const result = spawnSync('docker', ['exec', database, 'pg_isready', '--host', '127.0.0.1', '--username', 'postgres', '--dbname', 'recovery_source'], { stdio: 'ignore' });
    if (result.status === 0) { ready = true; break; }
    await new Promise(done => setTimeout(done, 1000));
  }
  if (!ready) throw new Error('Ephemeral native recovery PostgreSQL did not start.');
  run('docker', ['run', '--rm', '--network', network,
    '--mount', `type=bind,source=${output},target=/native,readonly`,
    '--mount', `type=bind,source=${fixture},target=/fixtures,readonly`,
    '--mount', `type=bind,source=${resolve('apps/api/Database/Migrations')},target=/migrations,readonly`,
    '--env', `NATIVE_RECOVERY_PASSWORD=${password}`, '--env', 'Recovery__Environment=native-fixture',
    '--env', `Recovery__RecoveryWorkerBuildDigest=${'a'.repeat(64)}`,
    '--entrypoint', '/usr/share/dotnet/dotnet', 'software-construction-recovery-worker:local',
    '/native/Platform.Recovery.Native.Tests.dll'], { stdio: 'inherit' });
} finally {
  if (createdDatabase) spawnSync('docker', ['rm', '--force', database], { stdio: 'ignore' });
  if (createdNetwork) spawnSync('docker', ['network', 'rm', network], { stdio: 'ignore' });
  const withinTemporaryRoot = relative(realpathSync(tmpdir()), fixture);
  if (!withinTemporaryRoot.startsWith('..') && !isAbsolute(withinTemporaryRoot) && withinTemporaryRoot)
    rmSync(fixture, { recursive: true, force: true });
}
