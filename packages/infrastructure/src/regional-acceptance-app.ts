import { App } from 'aws-cdk-lib';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RegionalAcceptanceStack } from './regional-acceptance-stack.js';

// Separate entry point: this client never constructs the app/data stack.
const app = new App();
const offline = app.node.tryGetContext('offline') === 'true';
const context = (name: string, offlineValue: string): string => {
  const value: unknown = app.node.tryGetContext(name);
  if (typeof value === 'string' && value.length > 0) return value;
  if (offline) return offlineValue;
  throw new Error(`Regional acceptance context ${name} is required.`);
};
const environmentName = app.node.tryGetContext('environment') ?? 'nonprod';
const region = app.node.tryGetContext('region') ?? 'ap-southeast-1';
const account = context('accountId', '111122223333');
const bundlePath = resolve(app.node.tryGetContext('acceptanceBundle') ?? resolve(dirname(fileURLToPath(import.meta.url)), '../../..', 'artifacts/release/regional-acceptance-client.zip'));
new RegionalAcceptanceStack(app, 'RegionalAcceptance-nonprod', {
  env: { account, region }, environmentName, bundlePath,
  origin: context('acceptanceOrigin', 'https://fixtureapi123.execute-api.ap-southeast-1.amazonaws.com'),
  deploymentRoleArn: context('deploymentRoleArn', `arn:aws:iam::${account}:role/github-actions-nonprod-deploy`),
});
app.synth();
