import { strict as assert } from 'node:assert';
import { isTransientAuditFailure, runAudit } from '../../scripts/audit-dependencies.mjs';

assert.ok(isTransientAuditFailure('npm error 503 Service Unavailable'));
assert.ok(!isTransientAuditFailure('npm error invalid package name'));

let calls = 0;
await runAudit('.', [], async () => {
  calls += 1;
  return calls === 1
    ? { code: 1, stdout: '', stderr: 'npm error 503 Service Unavailable', timedOut: false }
    : { code: 0, stdout: '{}', stderr: '', timedOut: false };
}, async () => {});
assert.equal(calls, 2, 'Transient service failures must be retried.');

calls = 0;
await assert.rejects(
  runAudit('.', [], async () => {
    calls += 1;
    return { code: 1, stdout: JSON.stringify({ metadata: { vulnerabilities: { high: 1, critical: 0 } } }), stderr: '', timedOut: false };
  }, async () => {}),
  /high or critical vulnerability/,
);
assert.equal(calls, 1, 'High-severity vulnerabilities must not be retried or suppressed.');

calls = 0;
await assert.rejects(
  runAudit('.', [], async () => {
    calls += 1;
    return {
      code: 1,
      stdout: JSON.stringify({ metadata: { vulnerabilities: { high: 1, critical: 0 } } }),
      stderr: 'npm error 503 Service Unavailable',
      timedOut: false,
    };
  }, async () => {}),
  /high or critical vulnerability/,
);
assert.equal(calls, 1, 'A high vulnerability in stdout must override transient stderr noise.');

console.log('Dependency audit retry tests passed.');
