import { ArnFormat, CfnOutput, Duration, RemovalPolicy, Stack, StackProps, aws_iam } from 'aws-cdk-lib';
import { CorsHttpMethod, HttpApi, HttpMethod, VpcLink } from 'aws-cdk-lib/aws-apigatewayv2';
import { HttpServiceDiscoveryIntegration } from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import { CfnBudget } from 'aws-cdk-lib/aws-budgets';
import { Key } from 'aws-cdk-lib/aws-kms';
import { FilterPattern, LogGroup, MetricFilter, RetentionDays } from 'aws-cdk-lib/aws-logs';
import { Alarm, ComparisonOperator, Dashboard, GraphWidget, Metric, TreatMissingData } from 'aws-cdk-lib/aws-cloudwatch';
import { SnsAction } from 'aws-cdk-lib/aws-cloudwatch-actions';
import { BlockPublicAccess, Bucket, BucketEncryption, HttpMethods } from 'aws-cdk-lib/aws-s3';
import { InstanceClass, InstanceSize, InstanceType, Port, SecurityGroup, SubnetType, Vpc } from 'aws-cdk-lib/aws-ec2';
import { Cluster, ContainerImage, FargateService, FargateTaskDefinition, LogDrivers, Secret as EcsSecret } from 'aws-cdk-lib/aws-ecs';
import { Repository, TagMutability } from 'aws-cdk-lib/aws-ecr';
import { Credentials, DatabaseInstance, DatabaseInstanceEngine, PostgresEngineVersion } from 'aws-cdk-lib/aws-rds';
import { DnsRecordType } from 'aws-cdk-lib/aws-servicediscovery';
import { Topic } from 'aws-cdk-lib/aws-sns';
import { EmailSubscription } from 'aws-cdk-lib/aws-sns-subscriptions';
import { Secret as SecretsManagerSecret } from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import { createOwnerIdentity } from './identity/owner-identity.js';

export interface PlatformFoundationStackProps extends StackProps {
  readonly environmentName: string;
  readonly monthlyBudgetUsd: number;
  readonly ownerEmail: string;
  readonly alertEmail: string;
  readonly webOrigin: string;
  readonly cognitoDomainPrefix: string;
  readonly deploymentRoleArn?: string;
}

export class PlatformFoundationStack extends Stack {
  public constructor(scope: Construct, id: string, props: PlatformFoundationStackProps) {
    super(scope, id, props);

    if (!Number.isFinite(props.monthlyBudgetUsd) || props.monthlyBudgetUsd <= 0) {
      throw new Error('monthlyBudgetUsd must be a positive number.');
    }

    const vpc = new Vpc(this, 'PlatformVpc', {
      maxAzs: 2,
      natGateways: 0,
      subnetConfiguration: [
        { name: 'edge', subnetType: SubnetType.PUBLIC },
        { name: 'data', subnetType: SubnetType.PRIVATE_ISOLATED },
      ],
    });

    const encryptionKey = new Key(this, 'PlatformEncryptionKey', {
      enableKeyRotation: true,
      removalPolicy: RemovalPolicy.RETAIN,
      pendingWindow: Duration.days(30),
    });

    const evidenceBucket = new Bucket(this, 'EvidenceBucket', {
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      encryption: BucketEncryption.KMS,
      encryptionKey,
      enforceSSL: true,
      versioned: true,
      removalPolicy: RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
    });

    const telemetry = new LogGroup(this, 'TelemetryLogGroup', {
      encryptionKey,
      retention: RetentionDays.ONE_MONTH,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const identity = createOwnerIdentity(this, { ownerEmail: props.ownerEmail, webOrigin: props.webOrigin, domainPrefix: props.cognitoDomainPrefix });
    const ingestBucket = new Bucket(this, 'IngestBucket', {
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      encryption: BucketEncryption.KMS,
      encryptionKey,
      enforceSSL: true,
      versioned: true,
      removalPolicy: RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
      cors: [{ allowedOrigins: [props.webOrigin], allowedMethods: [HttpMethods.GET, HttpMethods.PUT, HttpMethods.HEAD], allowedHeaders: ['*'], exposedHeaders: ['ETag', 'x-amz-checksum-sha256', 'x-amz-version-id'], maxAge: 900 }],
    });
    const apiRepository = new Repository(this, 'ApiRepository', {
      repositoryName: `platform-${props.environmentName}-api`,
      imageTagMutability: TagMutability.IMMUTABLE,
      imageScanOnPush: true,
      removalPolicy: RemovalPolicy.RETAIN,
      emptyOnDelete: false,
    });
    const workerRepository = new Repository(this, 'WorkerRepository', {
      repositoryName: `platform-${props.environmentName}-ingest-worker`,
      imageTagMutability: TagMutability.IMMUTABLE,
      imageScanOnPush: true,
      removalPolicy: RemovalPolicy.RETAIN,
      emptyOnDelete: false,
    });
    const database = new DatabaseInstance(this, 'IngestDatabase', {
      engine: DatabaseInstanceEngine.postgres({ version: PostgresEngineVersion.of('18.4', '18') }),
      credentials: Credentials.fromGeneratedSecret('platform_admin'),
      databaseName: 'platform',
      instanceType: InstanceType.of(InstanceClass.T4G, InstanceSize.MICRO),
      vpc,
      vpcSubnets: { subnetType: SubnetType.PRIVATE_ISOLATED },
      multiAz: false,
      publiclyAccessible: false,
      allocatedStorage: 20,
      maxAllocatedStorage: 100,
      storageEncrypted: true,
      storageEncryptionKey: encryptionKey,
      backupRetention: Duration.days(7),
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
      cloudwatchLogsExports: ['postgresql'],
    });
    if (!database.secret) throw new Error('The ingest database must expose generated credentials.');
    const runtimeDatabaseSecret = new SecretsManagerSecret(this, 'IngestRuntimeDatabaseCredentials', {
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ username: 'platform_runtime' }),
        generateStringKey: 'password',
        excludePunctuation: true,
        passwordLength: 32,
      },
      removalPolicy: RemovalPolicy.RETAIN,
    });

    const cluster = new Cluster(this, 'RuntimeCluster', { vpc, clusterName: `platform-${props.environmentName}` });
    const namespace = cluster.addDefaultCloudMapNamespace({ name: `${props.environmentName}.platform.internal` });
    const apiExecutionRole = new aws_iam.Role(this, 'ApiExecutionRole', { assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'), managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')] });
    const workerExecutionRole = new aws_iam.Role(this, 'WorkerExecutionRole', { assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'), managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')] });
    const migrationExecutionRole = new aws_iam.Role(this, 'MigrationExecutionRole', { assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'), managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')] });
    runtimeDatabaseSecret.grantRead(apiExecutionRole);
    runtimeDatabaseSecret.grantRead(workerExecutionRole);
    runtimeDatabaseSecret.grantRead(migrationExecutionRole);
    database.secret.grantRead(migrationExecutionRole);
    const databaseEnvironment = {
      Ingest__Database__Host: database.dbInstanceEndpointAddress,
      Ingest__Database__Port: database.dbInstanceEndpointPort,
      Ingest__Database__Name: 'platform',
      Ingest__Bucket: ingestBucket.bucketName,
      Ingest__KmsKeyId: encryptionKey.keyArn,
    };
    const databaseSecrets = {
      Ingest__Database__Username: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'username'),
      Ingest__Database__Password: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'password'),
    };
    const apiTask = new FargateTaskDefinition(this, 'ApiTask', { family: `platform-${props.environmentName}-api`, cpu: 256, memoryLimitMiB: 512, executionRole: apiExecutionRole });
    const apiContainer = apiTask.addContainer('api', {
      containerName: 'api',
      image: ContainerImage.fromEcrRepository(apiRepository, 'bootstrap'),
      environment: { ...databaseEnvironment, Authentication__CognitoAuthority: `https://cognito-idp.${this.region}.${this.urlSuffix}/${identity.userPool.userPoolId}`, Authentication__CognitoClientId: identity.client.userPoolClientId },
      secrets: databaseSecrets,
      logging: LogDrivers.awsLogs({ logGroup: telemetry, streamPrefix: 'api' }),
      portMappings: [{ containerPort: 8080 }],
    });
    const workerTask = new FargateTaskDefinition(this, 'WorkerTask', { family: `platform-${props.environmentName}-ingest-worker`, cpu: 256, memoryLimitMiB: 512, executionRole: workerExecutionRole });
    workerTask.addContainer('worker', {
      containerName: 'worker',
      image: ContainerImage.fromEcrRepository(workerRepository, 'bootstrap'),
      environment: databaseEnvironment,
      secrets: databaseSecrets,
      logging: LogDrivers.awsLogs({ logGroup: telemetry, streamPrefix: 'ingest-worker' }),
    });
    const migrationTask = new FargateTaskDefinition(this, 'MigrationTask', { family: `platform-${props.environmentName}-ingest-migration`, cpu: 256, memoryLimitMiB: 512, executionRole: migrationExecutionRole });
    migrationTask.addContainer('migration', {
      containerName: 'migration',
      image: ContainerImage.fromEcrRepository(workerRepository, 'bootstrap'),
      environment: databaseEnvironment,
      secrets: {
        Ingest__Database__Username: EcsSecret.fromSecretsManager(database.secret, 'username'),
        Ingest__Database__Password: EcsSecret.fromSecretsManager(database.secret, 'password'),
        Ingest__RuntimeDatabase__Username: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'username'),
        Ingest__RuntimeDatabase__Password: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'password'),
      },
      logging: LogDrivers.awsLogs({ logGroup: telemetry, streamPrefix: 'ingest-migration' }),
    });
    for (const role of [apiTask.taskRole, workerTask.taskRole]) {
      role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
        actions: ['s3:GetBucketVersioning', 's3:ListBucket', 's3:ListBucketMultipartUploads'],
        resources: [ingestBucket.bucketArn],
      }));
      role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
        actions: ['s3:GetObject', 's3:GetObjectVersion', 's3:GetObjectAttributes'],
        resources: [ingestBucket.arnForObjects('staging/*'), ingestBucket.arnForObjects('sha256/*')],
      }));
      role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
        effect: aws_iam.Effect.DENY,
        actions: ['s3:DeleteObject', 's3:DeleteObjectVersion'],
        resources: [ingestBucket.arnForObjects('sha256/*')],
      }));
    }
    apiTask.taskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:PutObject', 's3:AbortMultipartUpload', 's3:ListMultipartUploadParts'],
      resources: [ingestBucket.arnForObjects('staging/*')],
    }));
    workerTask.taskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:PutObject'],
      resources: [ingestBucket.arnForObjects('sha256/*'), ingestBucket.arnForObjects('quarantine/*')],
    }));
    workerTask.taskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:AbortMultipartUpload', 's3:ListMultipartUploadParts'],
      resources: [ingestBucket.arnForObjects('staging/*')],
    }));
    workerTask.taskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:DeleteObject', 's3:DeleteObjectVersion'],
      resources: [ingestBucket.arnForObjects('staging/*'), ingestBucket.arnForObjects('quarantine/*')],
    }));
    encryptionKey.grantEncryptDecrypt(apiTask.taskRole);
    encryptionKey.grantEncryptDecrypt(workerTask.taskRole);

    const apiSecurityGroup = new SecurityGroup(this, 'ApiSecurityGroup', { vpc, allowAllOutbound: true });
    const workerSecurityGroup = new SecurityGroup(this, 'WorkerSecurityGroup', { vpc, allowAllOutbound: true });
    database.connections.allowDefaultPortFrom(apiSecurityGroup);
    database.connections.allowDefaultPortFrom(workerSecurityGroup);
    const apiGatewaySecurityGroup = new SecurityGroup(this, 'ApiGatewaySecurityGroup', { vpc, allowAllOutbound: true });
    apiSecurityGroup.connections.allowFrom(apiGatewaySecurityGroup, Port.tcp(8080));
    const apiService = new FargateService(this, 'ApiService', { cluster, serviceName: `platform-${props.environmentName}-api`, taskDefinition: apiTask, desiredCount: 0, minHealthyPercent: 100, maxHealthyPercent: 200, assignPublicIp: true, securityGroups: [apiSecurityGroup], vpcSubnets: { subnetType: SubnetType.PUBLIC }, circuitBreaker: { rollback: true }, healthCheckGracePeriod: Duration.seconds(60), cloudMapOptions: { cloudMapNamespace: namespace, name: 'api', dnsRecordType: DnsRecordType.SRV, container: apiContainer, containerPort: 8080 } });
    const workerService = new FargateService(this, 'WorkerService', { cluster, serviceName: `platform-${props.environmentName}-ingest-worker`, taskDefinition: workerTask, desiredCount: 0, minHealthyPercent: 100, maxHealthyPercent: 200, assignPublicIp: true, securityGroups: [workerSecurityGroup], vpcSubnets: { subnetType: SubnetType.PUBLIC }, circuitBreaker: { rollback: true } });
    const vpcLink = new VpcLink(this, 'ApiVpcLink', { vpc, securityGroups: [apiGatewaySecurityGroup], subnets: { subnetType: SubnetType.PRIVATE_ISOLATED } });
    const publicApi = new HttpApi(this, 'PublicApi', {
      createDefaultStage: true,
      corsPreflight: { allowOrigins: [props.webOrigin], allowMethods: [CorsHttpMethod.ANY], allowHeaders: ['authorization', 'content-type', 'idempotency-key', 'x-operation-id'], maxAge: Duration.minutes(15) },
    });
    if (!apiService.cloudMapService) throw new Error('The API service must register with Cloud Map.');
    const integration = new HttpServiceDiscoveryIntegration('ApiIntegration', apiService.cloudMapService, { vpcLink });
    publicApi.addRoutes({ path: '/', methods: [HttpMethod.ANY], integration });
    publicApi.addRoutes({ path: '/{proxy+}', methods: [HttpMethod.ANY], integration });

    if (props.deploymentRoleArn) {
      const deploymentRole = aws_iam.Role.fromRoleArn(this, 'DeploymentRole', props.deploymentRoleArn);
      apiRepository.grantPullPush(deploymentRole);
      workerRepository.grantPullPush(deploymentRole);
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecr:DescribeImages'], resources: [apiRepository.repositoryArn, workerRepository.repositoryArn] }));
      const taskFamilies = [apiTask, workerTask, migrationTask].map((task) => this.formatArn({ service: 'ecs', resource: 'task-definition', resourceName: `${task.family}:*` }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecs:RegisterTaskDefinition'], resources: ['*'] }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecs:DescribeTaskDefinition'], resources: taskFamilies }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecs:RunTask'], resources: taskFamilies, conditions: { ArnEquals: { 'ecs:cluster': cluster.clusterArn } } }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecs:DescribeServices', 'ecs:UpdateService'], resources: [apiService.serviceArn, workerService.serviceArn] }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['ecs:DescribeTasks', 'ecs:StopTask'], resources: ['*'], conditions: { ArnEquals: { 'ecs:cluster': cluster.clusterArn } } }));
      deploymentRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['iam:PassRole'], resources: [apiTask.taskRole.roleArn, workerTask.taskRole.roleArn, migrationTask.taskRole.roleArn, apiExecutionRole.roleArn, workerExecutionRole.roleArn, migrationExecutionRole.roleArn] }));
    }
    const alerts = new Topic(this, 'OwnerAlerts', { masterKey: encryptionKey });
    encryptionKey.addToResourcePolicy(new aws_iam.PolicyStatement({
      principals: [new aws_iam.ServicePrincipal('cloudwatch.amazonaws.com'), new aws_iam.ServicePrincipal('sns.amazonaws.com')],
      actions: ['kms:Decrypt', 'kms:GenerateDataKey*'],
      resources: ['*'],
      conditions: { StringEquals: { 'aws:SourceAccount': this.account } },
    }));
    encryptionKey.addToResourcePolicy(new aws_iam.PolicyStatement({
      principals: [new aws_iam.ServicePrincipal(`logs.${this.region}.${this.urlSuffix}`)],
      actions: ['kms:Encrypt', 'kms:Decrypt', 'kms:ReEncrypt*', 'kms:GenerateDataKey*', 'kms:DescribeKey'],
      resources: ['*'],
      conditions: { ArnLike: { 'kms:EncryptionContext:aws:logs:arn': this.formatArn({ service: 'logs', resource: 'log-group', resourceName: '*', arnFormat: ArnFormat.COLON_RESOURCE_NAME }) } },
    }));
    alerts.addSubscription(new EmailSubscription(props.alertEmail));
    const availability = new Metric({ namespace: 'Platform/Service', metricName: 'Availability', statistic: 'Minimum', period: Duration.minutes(5) });
    const runbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/deployment-recovery.md';
    const ingestRunbook = 'https://github.com/FSN-Private/software_construction/blob/master/docs/runbooks/ingest-storage.md';
    const availabilityAlarm = new Alarm(this, 'ServiceAvailabilityAlarm', { metric: availability, threshold: 1, evaluationPeriods: 1, comparisonOperator: ComparisonOperator.LESS_THAN_THRESHOLD, treatMissingData: TreatMissingData.BREACHING, alarmDescription: `Owner: ${props.alertEmail}. Runbook: ${runbook}` });
    availabilityAlarm.addAlarmAction(new SnsAction(alerts));
    const authenticationFailures = new MetricFilter(this, 'AuthenticationFailureMetric', { logGroup: telemetry, filterPattern: FilterPattern.literal('{ $.State.AuditEventType = "authorization.denied" }'), metricNamespace: 'Platform/Auth', metricName: 'AuthenticationFailures', metricValue: '1' });
    const authenticationMetric = authenticationFailures.metric({ statistic: 'Sum', period: Duration.minutes(5) });
    const authenticationAlarm = new Alarm(this, 'AuthenticationAnomalyAlarm', { metric: authenticationMetric, threshold: 5, evaluationPeriods: 1, alarmDescription: `Owner: ${props.alertEmail}. Runbook: ${runbook}` });
    authenticationAlarm.addAlarmAction(new SnsAction(alerts));
    const ingestStorageFailures = new MetricFilter(this, 'IngestStorageFailureMetric', { logGroup: telemetry, filterPattern: FilterPattern.literal('{ $.State.AuditEventType = "ingest.storage.failure" }'), metricNamespace: 'Platform/Ingest', metricName: 'StorageFailures', metricValue: '1' });
    const ingestStorageMetric = ingestStorageFailures.metric({ statistic: 'Sum', period: Duration.minutes(5) });
    const ingestStorageAlarm = new Alarm(this, 'IngestStorageFailureAlarm', { metric: ingestStorageMetric, threshold: 1, evaluationPeriods: 3, comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, alarmDescription: `Owner: ${props.alertEmail}. Runbook: ${ingestRunbook}` });
    ingestStorageAlarm.addAlarmAction(new SnsAction(alerts));
    const ingestQuarantineMetric = new MetricFilter(this, 'IngestQuarantineMetric', { logGroup: telemetry, filterPattern: FilterPattern.literal('{ $.State.AuditEventType = "ingest.quarantined" }'), metricNamespace: 'Platform/Ingest', metricName: 'Quarantined', metricValue: '1' }).metric({ statistic: 'Sum', period: Duration.minutes(5) });
    const ingestRepairMetric = new MetricFilter(this, 'IngestRepairMetric', { logGroup: telemetry, filterPattern: FilterPattern.literal('{ $.State.AuditEventType = "ingest.repaired" }'), metricNamespace: 'Platform/Ingest', metricName: 'Repairs', metricValue: '1' }).metric({ statistic: 'Sum', period: Duration.minutes(5) });
    const integrityDriftMetric = new MetricFilter(this, 'IntegrityDriftMetric', { logGroup: telemetry, filterPattern: FilterPattern.literal('{ $.State.AuditEventType = "ingest.integrity.drift" }'), metricNamespace: 'Platform/Ingest', metricName: 'IntegrityDrift', metricValue: '1' }).metric({ statistic: 'Sum', period: Duration.minutes(5) });
    const integrityDriftAlarm = new Alarm(this, 'IntegrityDriftAlarm', { metric: integrityDriftMetric, threshold: 1, evaluationPeriods: 1, comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, alarmDescription: `Owner: ${props.alertEmail}. Runbook: ${ingestRunbook}` });
    integrityDriftAlarm.addAlarmAction(new SnsAction(alerts));
    for (const [id, namespace, metricName, threshold] of [
      ['DeploymentFailureAlarm', 'Platform/Delivery', 'DeploymentFailures', 1],
      ['CiFailureAlarm', 'Platform/Delivery', 'CiFailures', 1],
      ['BackupAgePlaceholderAlarm', 'Platform/Data', 'BackupAgeHours', 26],
    ] as const) {
      const alarm = new Alarm(this, id, { metric: new Metric({ namespace, metricName, statistic: 'Maximum', period: Duration.minutes(5) }), threshold, evaluationPeriods: 1, comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, treatMissingData: TreatMissingData.BREACHING, alarmDescription: `Owner: ${props.alertEmail}. Runbook: ${runbook}` });
      alarm.addAlarmAction(new SnsAction(alerts));
    }
    new Dashboard(this, 'PlatformOperationsDashboard', { dashboardName: `platform-${props.environmentName}-operations`, widgets: [
      [new GraphWidget({ title: 'Service availability', left: [availability] })],
      [new GraphWidget({ title: 'Authentication failures', left: [authenticationMetric] })],
      [new GraphWidget({ title: 'Ingest storage failures', left: [ingestStorageMetric] })],
      [new GraphWidget({ title: 'Ingest quarantine, repair, and integrity', left: [ingestQuarantineMetric, ingestRepairMetric, integrityDriftMetric] })],
      [new GraphWidget({ title: 'Delivery and CI failures', left: [new Metric({ namespace: 'Platform/Delivery', metricName: 'DeploymentFailures' }), new Metric({ namespace: 'Platform/Delivery', metricName: 'CiFailures' })] })],
      [new GraphWidget({ title: 'Backup age placeholder', left: [new Metric({ namespace: 'Platform/Data', metricName: 'BackupAgeHours' })] })],
      [new GraphWidget({ title: 'Monthly cost', left: [new Metric({ namespace: 'AWS/Billing', metricName: 'EstimatedCharges', dimensionsMap: { Currency: 'USD' } })] })],
    ] });

    new CfnBudget(this, 'MonthlyCostBudget', {
      budget: {
        budgetName: `platform-${props.environmentName}-monthly-cost`,
        budgetLimit: { amount: props.monthlyBudgetUsd, unit: 'USD' },
        timeUnit: 'MONTHLY',
        budgetType: 'COST',
      },
      notificationsWithSubscribers: [
        { notification: { comparisonOperator: 'GREATER_THAN', notificationType: 'ACTUAL', threshold: 80, thresholdType: 'PERCENTAGE' }, subscribers: [{ address: props.alertEmail, subscriptionType: 'EMAIL' }] },
        { notification: { comparisonOperator: 'GREATER_THAN', notificationType: 'ACTUAL', threshold: 100, thresholdType: 'PERCENTAGE' }, subscribers: [{ address: props.alertEmail, subscriptionType: 'EMAIL' }] },
      ],
    });

    new CfnOutput(this, 'VpcId', { value: vpc.vpcId });
    new CfnOutput(this, 'EvidenceBucketName', { value: evidenceBucket.bucketName });
    new CfnOutput(this, 'TelemetryLogGroupName', { value: telemetry.logGroupName });
    new CfnOutput(this, 'OwnerUserPoolId', { value: identity.userPool.userPoolId });
    new CfnOutput(this, 'OwnerUserPoolClientId', { value: identity.client.userPoolClientId });
    new CfnOutput(this, 'AwsRegion', { value: this.region });
    new CfnOutput(this, 'EcrRegistry', { value: `${this.account}.dkr.ecr.${this.region}.${this.urlSuffix}` });
    new CfnOutput(this, 'ApiRepositoryName', { value: apiRepository.repositoryName });
    new CfnOutput(this, 'WorkerRepositoryName', { value: workerRepository.repositoryName });
    new CfnOutput(this, 'EcsClusterName', { value: cluster.clusterName });
    new CfnOutput(this, 'ApiServiceName', { value: apiService.serviceName });
    new CfnOutput(this, 'ApiTaskDefinitionFamily', { value: apiTask.family });
    new CfnOutput(this, 'ApiContainerName', { value: 'api' });
    new CfnOutput(this, 'WorkerServiceName', { value: workerService.serviceName });
    new CfnOutput(this, 'WorkerTaskDefinitionFamily', { value: workerTask.family });
    new CfnOutput(this, 'WorkerContainerName', { value: 'worker' });
    new CfnOutput(this, 'MigrationTaskDefinitionFamily', { value: migrationTask.family });
    new CfnOutput(this, 'MigrationContainerName', { value: 'migration' });
    new CfnOutput(this, 'HealthUrl', { value: publicApi.apiEndpoint });
    new CfnOutput(this, 'CognitoAuthority', { value: `https://cognito-idp.${this.region}.${this.urlSuffix}/${identity.userPool.userPoolId}` });
    new CfnOutput(this, 'IngestBucketName', { value: ingestBucket.bucketName });
    new CfnOutput(this, 'IngestKmsKeyArn', { value: encryptionKey.keyArn });
    new CfnOutput(this, 'IngestDatabaseSecretArn', { value: database.secret.secretArn });
  }
}
