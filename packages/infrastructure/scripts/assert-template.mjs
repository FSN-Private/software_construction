import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const infrastructureDirectory = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const template = JSON.parse(readFileSync(resolve(infrastructureDirectory, 'cdk.out/PlatformFoundation-nonprod.template.json'), 'utf8'));
const resources = Object.values(template.Resources);
const requiredTypes = ['AWS::EC2::VPC', 'AWS::KMS::Key', 'AWS::S3::Bucket', 'AWS::Logs::LogGroup', 'AWS::Budgets::Budget', 'AWS::Cognito::UserPool', 'AWS::SNS::Topic', 'AWS::CloudWatch::Alarm', 'AWS::CloudWatch::Dashboard', 'AWS::ECR::Repository', 'AWS::RDS::DBInstance', 'AWS::ECS::Cluster', 'AWS::ECS::TaskDefinition', 'AWS::ECS::Service', 'AWS::ServiceDiscovery::Service', 'AWS::ApiGatewayV2::Api'];
for (const type of requiredTypes) {
  if (!resources.some((resource) => resource.Type === type)) {
    throw new Error(`Synthesized template is missing required resource ${type}.`);
  }
}
if (resources.filter((resource) => resource.Type === 'AWS::S3::Bucket').length < 2) throw new Error('Foundation and ingest require separate private versioned buckets.');
if (resources.filter((resource) => resource.Type === 'AWS::ECR::Repository').length !== 3) throw new Error('API, ingest-worker, and renderer-worker require separate ECR repositories.');
const taskDefinitions = resources.filter((resource) => resource.Type === 'AWS::ECS::TaskDefinition');
if (taskDefinitions.length !== 4 || resources.filter((resource) => resource.Type === 'AWS::ECS::Service').length !== 3) throw new Error('API, ingest worker, renderer worker, and migration tasks plus paired runtime services are required.');
const apiTask = taskDefinitions.find((task) => task.Properties.ContainerDefinitions[0].Name === 'api');
if (Number(apiTask?.Properties?.Memory) < 2048) throw new Error('The API task requires at least 2 GiB memory for reference-scale integrity verification.');
if (resources.some((resource) => resource.Type === 'AWS::EC2::NatGateway' || resource.Type === 'AWS::ElasticLoadBalancingV2::LoadBalancer')) throw new Error('The low-budget non-production runtime must not provision fixed-cost NAT gateways or load balancers.');
if (resources.filter((resource) => resource.Type === 'AWS::ECS::Service').some((resource) => resource.Properties.DesiredCount !== 0)) throw new Error('Bootstrap services must stay at zero tasks until verified images are published.');
const database = resources.find((resource) => resource.Type === 'AWS::RDS::DBInstance');
if (!database?.Properties?.StorageEncrypted || !database.Properties.DeletionProtection || database.DeletionPolicy !== 'Retain') throw new Error('The ingest database must be encrypted, deletion-protected, and retained.');
const requiredSettings = ['Ingest__Database__Host', 'Ingest__Database__Port', 'Ingest__Database__Name', 'Ingest__Database__Username', 'Ingest__Database__Password', 'Ingest__Bucket', 'Ingest__KmsKeyId'];
for (const task of taskDefinitions.filter((task) => task.Properties.ContainerDefinitions[0].Name !== 'renderer')) {
  const container = task.Properties.ContainerDefinitions[0];
  const names = [...(container.Environment ?? []), ...(container.Secrets ?? [])].map(({ Name }) => Name);
  if (requiredSettings.some((name) => !names.includes(name))) throw new Error('Every runtime task must receive the complete ingest database and storage configuration.');
}
const runtimeTasks = taskDefinitions.filter((task) => ['api', 'worker'].includes(task.Properties.ContainerDefinitions[0].Name));
const migrationTask = taskDefinitions.find((task) => task.Properties.ContainerDefinitions[0].Name === 'migration');
const rendererTask = taskDefinitions.find((task) => task.Properties.ContainerDefinitions[0].Name === 'renderer');
if (runtimeTasks.length !== 2 || !migrationTask) throw new Error('Task definitions must keep distinct API, worker, and migration identities.');
for (const task of runtimeTasks) {
  const secrets = task.Properties.ContainerDefinitions[0].Secrets ?? [];
  if (secrets.length !== 2 || JSON.stringify(secrets).includes('IngestDatabaseSecret')) throw new Error('Runtime tasks must receive only restricted runtime database credentials.');
}
const rendererContainer = rendererTask?.Properties.ContainerDefinitions[0];
const rendererSettings = [...(rendererContainer?.Environment ?? []), ...(rendererContainer?.Secrets ?? [])].map(({ Name }) => Name);
if (!rendererTask || rendererContainer?.ReadonlyRootFilesystem !== true || (rendererContainer.Secrets ?? []).length !== 0 || !['Rendering__QueueUrl', 'Rendering__SourceBucket', 'Rendering__DerivativeBucket', 'Rendering__WorkspaceRoot', 'Rendering__AwsRegion', 'Rendering__CallbackBaseUrl'].every((name) => rendererSettings.includes(name))) throw new Error('Renderer tasks must use a read-only root, no database secrets, and only renderer queue/storage settings.');
if (Number(rendererTask.Properties?.Cpu) < 1024 || Number(rendererTask.Properties?.Memory) < 2048 || Number(rendererTask.Properties?.EphemeralStorage?.SizeInGiB) < 30) throw new Error('Renderer tasks must reserve bounded CPU, memory, and ephemeral workspace capacity.');
if (resources.filter((resource) => resource.Type === 'AWS::SQS::Queue').length !== 2) throw new Error('Renderer work requires an encrypted queue and dead-letter queue.');
const migrationSecretNames = (migrationTask.Properties.ContainerDefinitions[0].Secrets ?? []).map(({ Name }) => Name);
if (!['Ingest__Database__Username', 'Ingest__Database__Password', 'Ingest__RuntimeDatabase__Username', 'Ingest__RuntimeDatabase__Password'].every((name) => migrationSecretNames.includes(name))) throw new Error('The migration task must receive admin and runtime credentials for schema migration and runtime-role provisioning.');
for (const output of ['AwsRegion', 'EcrRegistry', 'ApiRepositoryName', 'WorkerRepositoryName', 'RendererRepositoryName', 'RendererQueueUrl', 'RendererDeadLetterQueueUrl', 'DerivativeBucketName', 'RendererServiceName', 'RendererTaskDefinitionFamily', 'EcsClusterName', 'ApiServiceName', 'ApiTaskDefinitionFamily', 'ApiContainerName', 'WorkerServiceName', 'WorkerTaskDefinitionFamily', 'WorkerContainerName', 'MigrationTaskDefinitionFamily', 'MigrationContainerName', 'HealthUrl', 'CognitoAuthority', 'OwnerUserPoolClientId', 'DatabaseInstanceIdentifier', 'VersionControlHeartbeatAlarmName']) {
  if (!template.Outputs?.[output]) throw new Error(`Missing deployment configuration output ${output}.`);
}
const iamPolicies = Object.entries(template.Resources).filter(([, resource]) => resource.Type === 'AWS::IAM::Policy');
const deploymentPolicies = iamPolicies.flatMap(([, resource]) => resource.Properties.PolicyDocument.Statement);
for (const action of ['ecr:PutImage', 'ecr:DescribeImages', 'ecs:RegisterTaskDefinition', 'ecs:UpdateService', 'ecs:RunTask', 'ecs:StopTask', 'iam:PassRole', 'rds:DescribeDBInstances', 'rds:StartDBInstance', 'rds:StopDBInstance', 'cloudwatch:DisableAlarmActions', 'cloudwatch:EnableAlarmActions']) {
  if (!deploymentPolicies.some((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes(action))) throw new Error(`The deployment role is missing ${action}.`);
}
const describeTaskDefinitionPolicy = deploymentPolicies.find((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes('ecs:DescribeTaskDefinition'));
if (describeTaskDefinitionPolicy?.Resource !== '*') throw new Error('ECS DescribeTaskDefinition does not support task-definition resource scoping and must use Resource "*".');
const rdsLifecyclePolicy = deploymentPolicies.find((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes('rds:StopDBInstance'));
if (!JSON.stringify(rdsLifecyclePolicy?.Resource).includes('IngestDatabase')) throw new Error('RDS lifecycle permissions must target the non-production database instance.');
const heartbeatLifecyclePolicy = deploymentPolicies.find((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes('cloudwatch:DisableAlarmActions'));
if (!JSON.stringify(heartbeatLifecyclePolicy?.Resource).includes('VersionControlIntegrityHeartbeatAlarm')) throw new Error('Alarm lifecycle permissions must target only the version-control heartbeat alarm.');
const deleteStatements = deploymentPolicies.filter((statement) => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).some((action) => action === 's3:DeleteObject' || action === 's3:DeleteObjectVersion'));
if (deleteStatements.some((statement) => statement.Effect === 'Allow' && !JSON.stringify(statement.Resource).includes('staging/*') && !JSON.stringify(statement.Resource).includes('quarantine/*'))) throw new Error('Runtime S3 deletion must be limited to staging or quarantine objects.');
if (deleteStatements.filter((statement) => statement.Effect === 'Deny' && JSON.stringify(statement.Resource).includes('sha256/*')).length < 2) throw new Error('Both runtime task roles must explicitly deny deletion of authoritative sha256 objects and versions.');
const apiPolicy = iamPolicies.find(([id]) => id.startsWith('ApiTaskTaskRoleDefaultPolicy'))?.[1];
const workerPolicy = iamPolicies.find(([id]) => id.startsWith('WorkerTaskTaskRoleDefaultPolicy'))?.[1];
const allowedPutResources = (policy) => policy?.Properties.PolicyDocument.Statement
  .filter((statement) => statement.Effect === 'Allow' && (Array.isArray(statement.Action) ? statement.Action : [statement.Action]).includes('s3:PutObject'))
  .map((statement) => JSON.stringify(statement.Resource)) ?? [];
const apiPutResources = allowedPutResources(apiPolicy);
if (apiPutResources.length !== 1 || !apiPutResources[0].includes('/staging/*') || apiPutResources[0].includes('/sha256/*') || apiPutResources[0].includes('/quarantine/*') || apiPutResources[0].includes('"/*"')) throw new Error('The API may write only staging objects.');
const workerPutResources = allowedPutResources(workerPolicy);
if (workerPutResources.length !== 1 || !workerPutResources[0].includes('/sha256/*') || !workerPutResources[0].includes('/quarantine/*') || workerPutResources[0].includes('"/*"')) throw new Error('The worker may write only authoritative and quarantine objects.');
const keyPolicies = resources.filter((resource) => resource.Type === 'AWS::KMS::Key').flatMap((resource) => resource.Properties.KeyPolicy.Statement);
const logsPolicy = keyPolicies.find((statement) => JSON.stringify(statement.Principal).includes('logs.'));
if (!logsPolicy || !JSON.stringify(logsPolicy.Condition).includes(':log-group:*') || JSON.stringify(logsPolicy.Condition).includes(':log-group/*')) throw new Error('CloudWatch Logs KMS access must use the colon-form log-group ARN pattern.');
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').length < 8) throw new Error('Operations require service, ingest, version-control integrity, deployment, authentication, CI, and backup-age alarms.');
if (!resources.some((resource) => resource.Type === 'AWS::Logs::MetricFilter' && resource.Properties?.MetricTransformations?.[0]?.MetricName === 'AuthenticationFailures' && resource.Properties?.FilterPattern?.includes('$.State.AuditEventType'))) throw new Error('Authentication anomaly alarms must derive a metric from the API JSON authorization-denial audit event.');
if (!resources.some((resource) => resource.Type === 'AWS::Cognito::UserPoolClient' && resource.Properties?.AllowedOAuthScopes?.includes('openid'))) throw new Error('The owner web client must permit the OpenID scope requested by the Hosted UI.');
const apiGateway = resources.find((resource) => resource.Type === 'AWS::ApiGatewayV2::Api');
if (!['if-match', 'x-client-id'].every((header) => apiGateway?.Properties?.CorsConfiguration?.AllowHeaders?.includes(header))) throw new Error('API browser preflight must allow version-control precondition headers.');
const iamCallbacks = resources.filter((resource) => resource.Type === 'AWS::ApiGatewayV2::Route' && resource.Properties?.AuthorizationType === 'AWS_IAM');
if (iamCallbacks.length !== 3 || !iamCallbacks.every((route) => route.Properties.RouteKey.startsWith('POST /internal/render-callbacks/'))) throw new Error('Renderer callbacks must be limited to IAM-authorized machine routes.');
const canonicalRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/deployment-recovery.md';
const ingestRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/ingest-storage.md';
const versionControlRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/version-control-integrity.md';
const renderingRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/rendering-operations.md';
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').some((resource) => !resource.Properties?.AlarmDescription?.includes(canonicalRunbook) && !resource.Properties?.AlarmDescription?.includes(ingestRunbook) && !resource.Properties?.AlarmDescription?.includes(versionControlRunbook) && !resource.Properties?.AlarmDescription?.includes(renderingRunbook))) throw new Error('Every operations alarm must identify its canonical recovery runbook.');
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').filter((resource) => resource.Properties?.AlarmDescription?.includes(renderingRunbook)).length < 2) throw new Error('Renderer queue age and dead-letter alarms must link the rendering runbook.');
for (const metric of ['Quarantined', 'Repairs', 'IntegrityDrift']) {
  if (!resources.some((resource) => resource.Type === 'AWS::Logs::MetricFilter' && resource.Properties?.MetricTransformations?.[0]?.MetricName === metric)) throw new Error(`Ingest operations require the ${metric} metric filter.`);
}
if (!resources.some((resource) => resource.Type === 'AWS::CloudWatch::Alarm' && resource.Properties?.AlarmDescription?.includes(ingestRunbook))) throw new Error('Ingest alarms must link their dedicated runbook.');
for (const metric of ['IntegrityFailures', 'IntegrityHeartbeats']) {
  if (!resources.some((resource) => resource.Type === 'AWS::Logs::MetricFilter' && resource.Properties?.MetricTransformations?.[0]?.MetricName === metric)) throw new Error(`Version-control operations require the ${metric} metric filter.`);
}
const versionControlHeartbeatAlarm = resources.find((resource) => resource.Type === 'AWS::CloudWatch::Alarm' && resource.Properties?.AlarmDescription?.includes(versionControlRunbook) && resource.Properties?.TreatMissingData === 'breaching');
if (!versionControlHeartbeatAlarm) throw new Error('Version-control heartbeat loss must alert within fifteen minutes and link its runbook.');
if (versionControlHeartbeatAlarm.Properties.ActionsEnabled !== false) throw new Error('Non-production heartbeat actions must stay disabled until a healthy application deployment enables them.');
const budget = resources.find((resource) => resource.Type === 'AWS::Budgets::Budget');
if (budget?.Properties?.NotificationsWithSubscribers?.length !== 2) throw new Error('Budget must alert at 80% and 100%.');
if (Number(budget?.Properties?.Budget?.BudgetLimit?.Amount) > 25) throw new Error('Non-production monthly budget must not exceed USD 25.');

const bucket = resources.find((resource) => resource.Type === 'AWS::S3::Bucket');
if (bucket?.Properties?.PublicAccessBlockConfiguration?.BlockPublicAcls !== true) {
  throw new Error('Evidence bucket must block public ACLs.');
}
if (bucket.Properties.BucketEncryption.ServerSideEncryptionConfiguration[0].ServerSideEncryptionByDefault.SSEAlgorithm !== 'aws:kms') {
  throw new Error('Evidence bucket must use KMS encryption.');
}

console.log('Infrastructure template assertions passed.');
