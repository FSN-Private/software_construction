import { App, Environment } from 'aws-cdk-lib';
import { PlatformFoundationStack } from './platform-foundation-stack.js';

const app = new App();
const environmentName = app.node.tryGetContext('environment') ?? 'nonprod';
const region = app.node.tryGetContext('region') ?? 'ap-southeast-1';
const env: Environment = { region };

new PlatformFoundationStack(app, `PlatformFoundation-${environmentName}`, {
  env,
  environmentName,
  monthlyBudgetUsd: Number(app.node.tryGetContext('monthlyBudgetUsd') ?? 250),
  ownerEmail: app.node.tryGetContext('ownerEmail') ?? 'owner@example.invalid',
  alertEmail: app.node.tryGetContext('alertEmail') ?? 'alerts@example.invalid',
  webOrigin: app.node.tryGetContext('webOrigin') ?? 'https://example.invalid',
  cognitoDomainPrefix: app.node.tryGetContext('cognitoDomainPrefix') ?? `platform-${environmentName}-example`,
});
