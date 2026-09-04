import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const apiDockerfile = readFileSync('apps/api/Dockerfile', 'utf8');
const webDockerfile = readFileSync('apps/web/Dockerfile', 'utf8');
const infrastructureDockerfile = readFileSync('packages/infrastructure/Dockerfile', 'utf8');
const windowsPackage = readFileSync('apps/api/packaging/windows/package.ps1', 'utf8');
const apiLock = JSON.parse(readFileSync('apps/api/packages.lock.json', 'utf8'));

const immutableImage = /FROM [^\s]+@sha256:[a-f0-9]{64}/g;
assert.equal(apiDockerfile.match(immutableImage)?.length, 2, 'API image must pin SDK and runtime bases by digest.');
assert.equal(webDockerfile.match(immutableImage)?.length, 2, 'Web image must pin build and runtime bases by digest.');
assert.equal(infrastructureDockerfile.match(immutableImage)?.length, 1, 'CDK builder must pin its Node base by digest.');
assert.ok(apiDockerfile.includes('dotnet restore apps/api/Platform.Api.csproj --locked-mode'), 'API container must restore with the NuGet lock.');
assert.ok(apiDockerfile.includes('USER $APP_UID'), 'API runtime must not run as root.');
assert.ok(webDockerfile.includes('npm ci --ignore-scripts'), 'Web container must install from its committed lock without lifecycle scripts.');
assert.ok(webDockerfile.includes('npm run build'), 'Web container must produce the production bundle.');
assert.ok(infrastructureDockerfile.includes('npm ci --ignore-scripts'), 'CDK builder must install from its committed lock without lifecycle scripts.');
assert.ok(infrastructureDockerfile.includes('ENTRYPOINT ["npm", "run", "synth", "--"]'), 'CDK builder must synthesize rather than deploy.');
assert.ok(windowsPackage.includes('dotnet restore $project --runtime win-x64 --locked-mode'), 'Windows package must restore the Windows target with the NuGet lock.');
assert.ok(windowsPackage.includes('--runtime win-x64'), 'Windows package must target the documented Windows runtime.');
assert.ok(windowsPackage.includes("throw 'Locked Windows restore failed.'"), 'Windows package must stop when locked restore fails.');
assert.ok(windowsPackage.includes("throw 'Windows package publish failed.'"), 'Windows package must stop when publish fails.');
assert.ok(windowsPackage.includes('Compress-Archive'), 'Windows package must produce a portable archive.');
assert.ok(apiLock.dependencies['net10.0/win-x64'], 'NuGet lock must cover the Windows package runtime target.');

console.log('Locked release package definitions passed.');
