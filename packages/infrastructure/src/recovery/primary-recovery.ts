import { ArnFormat, CfnOutput, Duration, RemovalPolicy, Stack, aws_iam } from 'aws-cdk-lib';
import { createHash } from 'node:crypto';
import { Rule, Schedule } from 'aws-cdk-lib/aws-events';
import { EcsTask } from 'aws-cdk-lib/aws-events-targets';
import { GatewayVpcEndpointAwsService, InterfaceVpcEndpointAwsService, SecurityGroup, SubnetType, Vpc } from 'aws-cdk-lib/aws-ec2';
import { ContainerDefinition, ContainerImage, FargateTaskDefinition, LogDrivers } from 'aws-cdk-lib/aws-ecs';
import { Repository, TagMutability } from 'aws-cdk-lib/aws-ecr';
import { Key } from 'aws-cdk-lib/aws-kms';
import { LogGroup } from 'aws-cdk-lib/aws-logs';
import { DatabaseInstance } from 'aws-cdk-lib/aws-rds';
import { BlockPublicAccess, Bucket, BucketEncryption } from 'aws-cdk-lib/aws-s3';
import { Secret } from 'aws-cdk-lib/aws-secretsmanager';
import { Queue, QueueEncryption } from 'aws-cdk-lib/aws-sqs';
import { Alarm, ComparisonOperator, Metric, TreatMissingData } from 'aws-cdk-lib/aws-cloudwatch';
import { SnsAction } from 'aws-cdk-lib/aws-cloudwatch-actions';
import { ITopic } from 'aws-cdk-lib/aws-sns';
import { Construct } from 'constructs';
import { Cluster } from 'aws-cdk-lib/aws-ecs';

export interface PrimaryRecoveryCaptureProps {
  readonly environmentName: string;
  readonly recoveryAccountId: string;
  readonly sourceDatabaseSecretArn: string;
  readonly sourceDatabaseSecretKmsKeyArn?: string;
  readonly captureImageTag: string;
  readonly captureImageDigest: string;
  readonly apiBuildDigest: string;
  readonly ingestWorkerBuildDigest: string;
  readonly rendererBuildDigest: string;
  readonly recoveryTemplateDigest: string;
  readonly migrationDigest: string;
  readonly configurationDigest: string;
  readonly databaseRootCertificate: string;
  readonly databaseRootCertificateSha256: string;
  readonly ownerApprovalTrustKeyId: string;
  readonly ownerApprovalTrustPublicKeySpkiBase64: string;
  readonly ownerApprovalTrustPublicKeySha256: string;
  readonly recoveryLibraryId: string;
  readonly recoveryOwnerId?: string;
  readonly observationSigningKeyId: string;
  readonly observationPublicKeySpkiBase64: string;
  readonly observationPublicKeySha256: string;
  readonly captureEphemeralStorageGiB: number;
  readonly schedulesEnabled: boolean;
  readonly vpc: Vpc;
  readonly cluster: Cluster;
  readonly database: DatabaseInstance;
  readonly ingestBucket: Bucket;
  readonly encryptionKey: Key;
  readonly telemetry: LogGroup;
  readonly alerts: ITopic;
  readonly apiContainer: ContainerDefinition;
  readonly apiTaskRole: aws_iam.IRole;
}

export function addPrimaryRecoveryCapture(scope: Construct, props: PrimaryRecoveryCaptureProps): void {
  if (!/^\d{12}$/.test(props.recoveryAccountId) || props.recoveryAccountId === Stack.of(scope).account) {
    throw new Error('Recovery integration requires a distinct 12-digit recovery account ID.');
  }
  if (!/^arn:[^:]+:secretsmanager:[^:]+:\d{12}:secret:[A-Za-z0-9/_+=.@-]+$/.test(props.sourceDatabaseSecretArn) || props.sourceDatabaseSecretArn.includes('example.invalid')) {
    throw new Error('Recovery integration requires the provisioned read-only source database secret ARN.');
  }
  if (props.sourceDatabaseSecretKmsKeyArn && !new RegExp(`^arn:[^:]+:kms:ap-southeast-1:${stackAccountId(scope)}:key/[0-9a-f-]{36}$`).test(props.sourceDatabaseSecretKmsKeyArn)) {
    throw new Error('The optional capture secret KMS key must be a primary-account key ARN in ap-southeast-1.');
  }
  if (!Number.isInteger(props.captureEphemeralStorageGiB) || props.captureEphemeralStorageGiB < 21 || props.captureEphemeralStorageGiB > 200) {
    throw new Error('captureEphemeralStorageGiB must be a measured value from 21 through 200 GiB.');
  }
  if (!/^[a-f0-9]{40}$/.test(props.captureImageTag)) throw new Error('Recovery capture requires a pinned full source revision image tag.');
  if (!/^[a-f0-9]{64}$/.test(props.captureImageDigest)) throw new Error('Recovery capture requires the published immutable worker image digest.');
  if (!/^[A-Za-z0-9._:-]{1,200}$/.test(props.recoveryLibraryId)) throw new Error('Recovery capture requires the canonical provisioned library ID.');
  for (const [name, digest] of [
    ['API', props.apiBuildDigest], ['ingest worker', props.ingestWorkerBuildDigest],
    ['renderer', props.rendererBuildDigest], ['recovery template', props.recoveryTemplateDigest],
    ['migration', props.migrationDigest], ['configuration', props.configurationDigest],
    ['database root certificate', props.databaseRootCertificateSha256],
  ] as const) {
    if (!/^[a-f0-9]{64}$/.test(digest)) throw new Error(`Recovery capture requires a pinned ${name} SHA-256 digest.`);
  }
  if (createHash('sha256').update(props.databaseRootCertificate, 'utf8').digest('hex') !== props.databaseRootCertificateSha256) {
    throw new Error('Recovery capture database root certificate bytes do not match their deployment fingerprint.');
  }
  if (!/^[A-Za-z0-9._:-]{1,200}$/.test(props.recoveryLibraryId)) throw new Error('Recovery capture requires the canonical provisioned library ID.');
  if (!new RegExp(`^arn:[^:]+:kms:ap-southeast-2:${props.recoveryAccountId}:key/[0-9a-f-]{36}$`).test(props.observationSigningKeyId) || !/^[a-f0-9]{64}$/.test(props.observationPublicKeySha256) || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(props.observationPublicKeySpkiBase64) || props.observationPublicKeySpkiBase64.length < 100) {
    throw new Error('Primary observations require an externally pinned recovery-account KMS signer SPKI and SHA-256 fingerprint.');
  }
  if (!new RegExp(`^arn:[^:]+:kms:ap-southeast-2:${props.recoveryAccountId}:key/[0-9a-f-]{36}$`).test(props.ownerApprovalTrustKeyId) || !/^[a-f0-9]{64}$/.test(props.ownerApprovalTrustPublicKeySha256) || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(props.ownerApprovalTrustPublicKeySpkiBase64) || props.ownerApprovalTrustPublicKeySpkiBase64.length < 100) {
    throw new Error('Primary capture requires the separately pinned recovery owner-approval KMS key, SPKI, and fingerprint.');
  }

  const stack = Stack.of(scope);
  const prefix = `platform-${props.environmentName}`;
  const copyRoleName = `${prefix}-recovery-copy`;
  const copyRoleArn = stack.formatArn({ service: 'iam', region: '', account: props.recoveryAccountId, resource: 'role', resourceName: copyRoleName, arnFormat: ArnFormat.SLASH_RESOURCE_NAME });
  const captureBucket = new Bucket(scope, 'RecoveryCaptureBucket', {
    bucketName: `${prefix}-${stack.account}-recovery-capture`,
    blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
    encryption: BucketEncryption.KMS,
    encryptionKey: props.encryptionKey,
    enforceSSL: true,
    versioned: true,
    removalPolicy: RemovalPolicy.RETAIN,
    autoDeleteObjects: false,
    lifecycleRules: [{ prefix: 'captures/', expiration: Duration.days(30), noncurrentVersionExpiration: Duration.days(30), abortIncompleteMultipartUploadAfter: Duration.days(2) }],
  });
  const sourceRole = new aws_iam.Role(scope, 'RecoverySourceReadRole', {
    roleName: `${prefix}-recovery-source-read`,
    assumedBy: new aws_iam.AccountPrincipal(props.recoveryAccountId).withConditions({ ArnEquals: { 'aws:PrincipalArn': copyRoleArn } }),
    description: 'Read-only recovery copy role. It cannot write to or delete from recovery storage.',
  });
  for (const [bucketArn, objectArn, objectPrefix] of [
    [captureBucket.bucketArn, captureBucket.arnForObjects('captures/*'), 'captures/*'],
    [props.ingestBucket.bucketArn, props.ingestBucket.arnForObjects('sha256/*'), 'sha256/*'],
  ] as const) {
    sourceRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:ListBucket', 's3:ListBucketVersions'],
      resources: [bucketArn],
      conditions: { StringLike: { 's3:prefix': [objectPrefix] } },
    }));
    sourceRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:GetObject', 's3:GetObjectVersion', 's3:GetObjectAttributes'],
      resources: [objectArn],
    }));
  }
  props.encryptionKey.grantDecrypt(sourceRole);

  const observationDlq = new Queue(scope, 'RecoveryObservationDlq', {
    queueName: `${prefix}-recovery-observation-dlq`,
    encryption: QueueEncryption.SQS_MANAGED,
    retentionPeriod: Duration.days(14),
  });
  const observationQueue = new Queue(scope, 'RecoveryObservationQueue', {
    queueName: `${prefix}-recovery-observations`,
    encryption: QueueEncryption.SQS_MANAGED,
    retentionPeriod: Duration.days(4),
    visibilityTimeout: Duration.minutes(5),
    deadLetterQueue: { queue: observationDlq, maxReceiveCount: 5 },
  });
  observationQueue.addToResourcePolicy(new aws_iam.PolicyStatement({
    principals: [new aws_iam.AccountPrincipal(props.recoveryAccountId)],
    actions: ['sqs:SendMessage'],
    resources: [observationQueue.queueArn],
    conditions: { ArnEquals: { 'aws:PrincipalArn': copyRoleArn } },
  }));
  observationQueue.grantConsumeMessages(props.apiTaskRole);
  props.apiContainer.addEnvironment('Recovery__Enabled', 'true');
  props.apiContainer.addEnvironment('Recovery__LibraryId', props.recoveryLibraryId);
  props.apiContainer.addEnvironment('Gallery__LibraryId', props.recoveryLibraryId);
  if (props.recoveryOwnerId) props.apiContainer.addEnvironment('Recovery__OwnerId', props.recoveryOwnerId);
  props.apiContainer.addEnvironment('Recovery__ApiBuildDigest', props.apiBuildDigest);
  props.apiContainer.addEnvironment('Recovery__IngestWorkerBuildDigest', props.ingestWorkerBuildDigest);
  props.apiContainer.addEnvironment('Recovery__RecoveryWorkerBuildDigest', props.captureImageDigest);
  props.apiContainer.addEnvironment('Recovery__RendererBuildDigest', props.rendererBuildDigest);
  props.apiContainer.addEnvironment('Recovery__RecoveryTemplateDigest', props.recoveryTemplateDigest);
  props.apiContainer.addEnvironment('Recovery__MigrationDigest', props.migrationDigest);
  props.apiContainer.addEnvironment('Recovery__ConfigurationDigest', props.configurationDigest);
  props.apiContainer.addEnvironment('Recovery__DatabaseRootCertificateSha256', props.databaseRootCertificateSha256);
  props.apiContainer.addEnvironment('Recovery__ObservationQueueUrl', observationQueue.queueUrl);
  props.apiContainer.addEnvironment('Recovery__ObservationTrust__KeyId', props.observationSigningKeyId);
  props.apiContainer.addEnvironment('Recovery__ObservationTrust__PublicKeySpkiBase64', props.observationPublicKeySpkiBase64);
  props.apiContainer.addEnvironment('Recovery__ObservationTrust__PublicKeySha256', props.observationPublicKeySha256);

  const repository = new Repository(scope, 'RecoveryWorkerRepository', {
    repositoryName: `${prefix}-recovery-worker`,
    imageTagMutability: TagMutability.IMMUTABLE,
    imageScanOnPush: true,
    removalPolicy: RemovalPolicy.RETAIN,
    emptyOnDelete: false,
  });
  const captureExecutionRole = new aws_iam.Role(scope, 'RecoveryCaptureExecutionRole', {
    assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
  });
  repository.grantPull(captureExecutionRole);
  props.telemetry.grantWrite(captureExecutionRole);
  const captureTaskRole = new aws_iam.Role(scope, 'RecoveryCaptureTaskRole', { assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com') });
  const sourceSecret = Secret.fromSecretCompleteArn(scope, 'RecoveryCaptureDatabaseSecret', props.sourceDatabaseSecretArn);
  sourceSecret.grantRead(captureTaskRole);
  if (props.sourceDatabaseSecretKmsKeyArn) Key.fromKeyArn(scope, 'RecoveryCaptureDatabaseSecretKey', props.sourceDatabaseSecretKmsKeyArn).grantDecrypt(captureTaskRole);
  captureBucket.addToResourcePolicy(new aws_iam.PolicyStatement({
    principals: [captureTaskRole],
    actions: ['s3:PutObject', 's3:AbortMultipartUpload', 's3:ListMultipartUploadParts'],
    resources: [captureBucket.arnForObjects('captures/*')],
  }));
  props.encryptionKey.grantEncrypt(captureTaskRole);
  const captureSecurityGroup = new SecurityGroup(scope, 'RecoveryCaptureSecurityGroup', { vpc: props.vpc, allowAllOutbound: true });
  props.database.connections.allowDefaultPortFrom(captureSecurityGroup);
  const captureTask = new FargateTaskDefinition(scope, 'RecoveryCaptureTask', {
    family: `${prefix}-recovery-capture`,
    cpu: 1024,
    memoryLimitMiB: 4096,
    ephemeralStorageGiB: props.captureEphemeralStorageGiB,
    executionRole: captureExecutionRole,
    taskRole: captureTaskRole,
  });
  captureTask.addContainer('capture', {
    containerName: 'recovery-worker',
    image: ContainerImage.fromRegistry(`${repository.repositoryUri}@sha256:${props.captureImageDigest}`),
    command: ['capture'],
    environment: {
      Recovery__Environment: props.environmentName,
      Recovery__WorkerId: 'capture',
      Recovery__SourceRevision: props.captureImageTag,
      Recovery__ApiBuildDigest: props.apiBuildDigest,
      Recovery__IngestWorkerBuildDigest: props.ingestWorkerBuildDigest,
      Recovery__RecoveryWorkerBuildDigest: props.captureImageDigest,
      Recovery__RendererBuildDigest: props.rendererBuildDigest,
      Recovery__RecoveryTemplateDigest: props.recoveryTemplateDigest,
      Recovery__MigrationDigest: props.migrationDigest,
      Recovery__ConfigurationDigest: props.configurationDigest,
      Recovery__DatabaseRootCertificate: props.databaseRootCertificate,
      Recovery__DatabaseRootCertificateSha256: props.databaseRootCertificateSha256,
      Recovery__OwnerApprovalTrust__KeyId: props.ownerApprovalTrustKeyId,
      Recovery__OwnerApprovalTrust__PublicKeySpkiBase64: props.ownerApprovalTrustPublicKeySpkiBase64,
      Recovery__OwnerApprovalTrust__PublicKeySha256: props.ownerApprovalTrustPublicKeySha256,
      ...(props.recoveryOwnerId ? { Recovery__OwnerId: props.recoveryOwnerId } : {}),
      Recovery__LibraryId: props.recoveryLibraryId,
      Recovery__SourceDatabaseSecretArn: props.sourceDatabaseSecretArn,
      Recovery__PrimaryArtifactsBucket: captureBucket.bucketName,
      Recovery__PrimaryArtifactsPrefix: 'captures/',
      Recovery__SourceBucket: props.ingestBucket.bucketName,
      Recovery__SourceRegion: stack.region,
      Recovery__ObservationQueueUrl: observationQueue.queueUrl,
      Recovery__PrimaryReadRoleArn: sourceRole.roleArn,
      Recovery__CaptureIntervalMinutes: '15',
      Recovery__ScratchDirectory: '/tmp/recovery',
      Recovery__Database__Host: props.database.dbInstanceEndpointAddress,
      Recovery__Database__Port: props.database.dbInstanceEndpointPort,
      Recovery__Database__Name: 'platform',
    },
    logging: LogDrivers.awsLogs({ logGroup: props.telemetry, streamPrefix: 'recovery-capture' }),
  });

  if (props.schedulesEnabled) {
    props.vpc.addGatewayEndpoint('RecoveryCaptureS3Endpoint', {
      service: GatewayVpcEndpointAwsService.S3,
      subnets: [{ subnetType: SubnetType.PRIVATE_ISOLATED }],
    });
    for (const [id, service] of [
      ['RecoveryCaptureEcrApiEndpoint', InterfaceVpcEndpointAwsService.ECR],
      ['RecoveryCaptureEcrDockerEndpoint', InterfaceVpcEndpointAwsService.ECR_DOCKER],
      ['RecoveryCaptureLogsEndpoint', InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS],
      ['RecoveryCaptureSecretsEndpoint', InterfaceVpcEndpointAwsService.SECRETS_MANAGER],
    ] as const) {
      props.vpc.addInterfaceEndpoint(id, { service, subnets: { subnetType: SubnetType.PRIVATE_ISOLATED } });
    }
    new Rule(scope, 'RecoveryCaptureSchedule', {
      schedule: Schedule.rate(Duration.minutes(15)),
      targets: [new EcsTask({
        cluster: props.cluster,
        taskDefinition: captureTask,
        securityGroups: [captureSecurityGroup],
        subnetSelection: { subnetType: SubnetType.PRIVATE_ISOLATED },
        assignPublicIp: false,
        taskCount: 1,
        containerOverrides: [{ containerName: 'recovery-worker', command: ['capture'] }],
      })],
    });
  }

  const captureDimensions = { Environment: props.environmentName, WorkerId: 'capture' };
  for (const [id, metricName, threshold, comparisonOperator, missing] of [
    ['RecoveryCaptureFailureAlarm', 'CaptureFailures', 1, ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, TreatMissingData.NOT_BREACHING],
    ['RecoveryCaptureHeartbeatAlarm', 'RecoveryWorkerHeartbeat', 1, ComparisonOperator.LESS_THAN_THRESHOLD, TreatMissingData.BREACHING],
  ] as const) {
    const alarm = new Alarm(scope, id, {
      metric: new Metric({ namespace: 'Platform/Recovery', metricName, statistic: 'Sum', period: Duration.minutes(5), dimensionsMap: captureDimensions }),
      threshold,
      evaluationPeriods: metricName === 'RecoveryWorkerHeartbeat' ? 3 : 1,
      comparisonOperator,
      treatMissingData: missing,
      alarmDescription: `Primary recovery ${metricName} alarm. Capture should run every 15 minutes; deliver notification within 15 minutes. Runbook: docs/runbooks/library-recovery.md`,
    });
    alarm.addAlarmAction(new SnsAction(props.alerts));
  }

  new CfnOutput(scope, 'RecoveryCaptureBucketName', { value: captureBucket.bucketName });
  new CfnOutput(scope, 'RecoverySourceReadRoleArn', { value: sourceRole.roleArn });
  new CfnOutput(scope, 'RecoveryObservationQueueUrl', { value: observationQueue.queueUrl });
  new CfnOutput(scope, 'RecoveryObservationQueueArn', { value: observationQueue.queueArn });
  new CfnOutput(scope, 'RecoveryWorkerRepositoryName', { value: repository.repositoryName });
  new CfnOutput(scope, 'RecoveryCaptureTaskDefinitionFamily', { value: captureTask.family });
}

function stackAccountId(scope: Construct): string {
  return Stack.of(scope).account;
}
