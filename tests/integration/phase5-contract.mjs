import { readFileSync } from 'node:fs';

const program = readFileSync('apps/api/Program.cs', 'utf8');
const app = readFileSync('apps/web/src/App.tsx', 'utf8');
for (const endpoint of ['/live', '/ready', '/v1/session', '/v1/version']) {
  if (!program.includes(endpoint)) throw new Error(`Missing v1 API endpoint ${endpoint}.`);
}
if (!program.includes('cognito:groups') || !program.includes('unauthenticated') || !program.includes('operationId')) throw new Error('Owner authorization must fail closed with a correlated safe problem.');
if (!app.includes('aria-labelledby') || !app.includes('role="status"')) throw new Error('Status shell must retain accessible labels and announcements.');
if (!app.includes('/oauth2/authorize') || !app.includes('/v1/session') || !app.includes('/ready') || !app.includes('/v1/version')) throw new Error('The owner shell must sign in and call every authenticated status endpoint.');
if (!app.includes('`op_${crypto.randomUUID()}`')) throw new Error('The owner shell must send an API-valid correlated operation ID.');
console.log('Phase 5 local contract checks passed.');
