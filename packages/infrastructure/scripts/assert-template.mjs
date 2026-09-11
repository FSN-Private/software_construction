import { readFileSync } from 'node:fs';

const template = JSON.parse(readFileSync('cdk.out/PlatformFoundation-nonprod.template.json', 'utf8'));
const resources = Object.values(template.Resources);
const requiredTypes = ['AWS::EC2::VPC', 'AWS::KMS::Key', 'AWS::S3::Bucket', 'AWS::Logs::LogGroup', 'AWS::Budgets::Budget', 'AWS::Cognito::UserPool', 'AWS::SNS::Topic', 'AWS::CloudWatch::Alarm', 'AWS::CloudWatch::Dashboard', 'AWS::ECR::Repository', 'AWS::RDS::DBInstance', 'AWS::ECS::Cluster', 'AWS::ECS::TaskDefinition', 'AWS::ECS::Service', 'AWS::ElasticLoadBalancingV2::LoadBalancer', 'AWS::ApiGatewayV2::Api'];
for (const type of requiredTypes) {
  if (!resources.some((resource) => resource.Type === type)) {
    throw new Error(`Synthesized template is missing required resource ${type}.`);
  }
}
if (resources.filter((resource) => resource.Type === 'AWS::S3::Bucket').length < 2) throw new Error('Foundation and ingest require separate private versioned buckets.');
if (resources.filter((resource) => resource.Type === 'AWS::ECR::Repository').length !== 2) throw new Error('API and ingest worker require separate ECR repositories.');
if (resources.filter((resource) => resource.Type === 'AWS::ECS::TaskDefinition').length !== 2 || resources.filter((resource) => resource.Type === 'AWS::ECS::Service').length !== 2) throw new Error('API and ingest worker require paired ECS tasks and services.');
if (resources.filter((resource) => resource.Type === 'AWS::ECS::Service').some((resource) => resource.Properties.DesiredCount !== 0)) throw new Error('Bootstrap services must stay at zero tasks until verified images are published.');
const database = resources.find((resource) => resource.Type === 'AWS::RDS::DBInstance');
if (!database?.Properties?.StorageEncrypted || !database.Properties.DeletionProtection || database.DeletionPolicy !== 'Retain') throw new Error('The ingest database must be encrypted, deletion-protected, and retained.');
const requiredSettings = ['Ingest__Database__Host', 'Ingest__Database__Port', 'Ingest__Database__Name', 'Ingest__Database__Username', 'Ingest__Database__Password', 'Ingest__Bucket', 'Ingest__KmsKeyId'];
for (const task of resources.filter((resource) => resource.Type === 'AWS::ECS::TaskDefinition')) {
  const container = task.Properties.ContainerDefinitions[0];
  const names = [...(container.Environment ?? []), ...(container.Secrets ?? [])].map(({ Name }) => Name);
  if (requiredSettings.some((name) => !names.includes(name))) throw new Error('Every runtime task must receive the complete ingest database and storage configuration.');
}
for (const output of ['AwsRegion', 'EcrRegistry', 'ApiRepositoryName', 'WorkerRepositoryName', 'EcsClusterName', 'ApiServiceName', 'ApiTaskDefinitionFamily', 'ApiContainerName', 'WorkerServiceName', 'WorkerTaskDefinitionFamily', 'WorkerContainerName', 'HealthUrl', 'CognitoAuthority', 'OwnerUserPoolClientId']) {
  if (!template.Outputs?.[output]) throw new Error(`Missing deployment configuration output ${output}.`);
}
const deploymentPolicies = resources.filter((resource) => resource.Type === 'AWS::IAM::Policy').flatMap((resource) => resource.Properties.PolicyDocument.Statement);
for (const action of ['ecr:PutImage', 'ecr:DescribeImages', 'ecs:RegisterTaskDefinition', 'ecs:UpdateService', 'ecs:RunTask', 'iam:PassRole']) {
  if (!deploymentPolicies.some((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes(action))) throw new Error(`The deployment role is missing ${action}.`);
}
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').length < 6) throw new Error('Operations require service, ingest, deployment, authentication, CI, and backup-age alarms.');
if (!resources.some((resource) => resource.Type === 'AWS::Logs::MetricFilter' && resource.Properties?.MetricTransformations?.[0]?.MetricName === 'AuthenticationFailures' && resource.Properties?.FilterPattern?.includes('$.State.AuditEventType'))) throw new Error('Authentication anomaly alarms must derive a metric from the API JSON authorization-denial audit event.');
if (!resources.some((resource) => resource.Type === 'AWS::Cognito::UserPoolClient' && resource.Properties?.AllowedOAuthScopes?.includes('openid'))) throw new Error('The owner web client must permit the OpenID scope requested by the Hosted UI.');
const canonicalRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/deployment-recovery.md';
const ingestRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/ingest-storage.md';
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').some((resource) => !resource.Properties?.AlarmDescription?.includes(canonicalRunbook) && !resource.Properties?.AlarmDescription?.includes(ingestRunbook))) throw new Error('Every operations alarm must identify its canonical recovery runbook.');
for (const metric of ['Quarantined', 'Repairs', 'IntegrityDrift']) {
  if (!resources.some((resource) => resource.Type === 'AWS::Logs::MetricFilter' && resource.Properties?.MetricTransformations?.[0]?.MetricName === metric)) throw new Error(`Ingest operations require the ${metric} metric filter.`);
}
if (!resources.some((resource) => resource.Type === 'AWS::CloudWatch::Alarm' && resource.Properties?.AlarmDescription?.includes(ingestRunbook))) throw new Error('Ingest alarms must link their dedicated runbook.');
const budget = resources.find((resource) => resource.Type === 'AWS::Budgets::Budget');
if (budget?.Properties?.NotificationsWithSubscribers?.length !== 2) throw new Error('Budget must alert at 80% and 100%.');

const bucket = resources.find((resource) => resource.Type === 'AWS::S3::Bucket');
if (bucket?.Properties?.PublicAccessBlockConfiguration?.BlockPublicAcls !== true) {
  throw new Error('Evidence bucket must block public ACLs.');
}
if (bucket.Properties.BucketEncryption.ServerSideEncryptionConfiguration[0].ServerSideEncryptionByDefault.SSEAlgorithm !== 'aws:kms') {
  throw new Error('Evidence bucket must use KMS encryption.');
}

console.log('Infrastructure template assertions passed.');
