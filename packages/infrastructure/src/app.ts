import { App, Environment } from 'aws-cdk-lib';
import { PlatformFoundationStack } from './platform-foundation-stack.js';

const app = new App();
const offline = app.node.tryGetContext('offline') === 'true';
const requiredContext = (name: string, offlineValue: string): string => {
  const value = app.node.tryGetContext(name);
  if (typeof value === 'string' && value.trim()) return value;
  if (offline) return offlineValue;
  throw new Error(`CDK context ${name} is required for deployment.`);
};
const environmentName = app.node.tryGetContext('environment') ?? 'nonprod';
const region = app.node.tryGetContext('region') ?? 'ap-southeast-1';
const env: Environment = { region };

new PlatformFoundationStack(app, `PlatformFoundation-${environmentName}`, {
  env,
  environmentName,
  monthlyBudgetUsd: Number(requiredContext('monthlyBudgetUsd', '250')),
  ownerEmail: requiredContext('ownerEmail', 'owner@example.invalid'),
  alertEmail: requiredContext('alertEmail', 'alerts@example.invalid'),
  webOrigin: requiredContext('webOrigin', 'https://example.invalid'),
  cognitoDomainPrefix: requiredContext('cognitoDomainPrefix', `platform-${environmentName}-example`),
  deploymentRoleArn: requiredContext('deploymentRoleArn', 'arn:aws:iam::111122223333:role/github-actions-nonprod-deploy'),
});
