import { ArnFormat, CfnOutput, CfnParameter, Duration, RemovalPolicy, Stack, StackProps, aws_iam } from 'aws-cdk-lib';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { AccountRecovery, CfnUserPoolGroup, Mfa, UserPool } from 'aws-cdk-lib/aws-cognito';
import { GatewayVpcEndpointAwsService, InstanceType, InterfaceVpcEndpointAwsService, Port, SecurityGroup, SubnetType, Vpc } from 'aws-cdk-lib/aws-ec2';
import { Cluster, ContainerImage, FargateTaskDefinition, LogDrivers, Secret as EcsSecret } from 'aws-cdk-lib/aws-ecs';
import { EcsTask } from 'aws-cdk-lib/aws-events-targets';
import { Rule, Schedule } from 'aws-cdk-lib/aws-events';
import { Repository, TagMutability } from 'aws-cdk-lib/aws-ecr';
import { Key, KeySpec, KeyUsage } from 'aws-cdk-lib/aws-kms';
import { Alarm, ComparisonOperator, Metric, TreatMissingData } from 'aws-cdk-lib/aws-cloudwatch';
import { SnsAction } from 'aws-cdk-lib/aws-cloudwatch-actions';
import { RetentionDays, LogGroup } from 'aws-cdk-lib/aws-logs';
import { ObjectLockRetention, StorageClass, BlockPublicAccess, Bucket, BucketEncryption } from 'aws-cdk-lib/aws-s3';
import { Credentials, DatabaseInstance, DatabaseInstanceEngine, PostgresEngineVersion } from 'aws-cdk-lib/aws-rds';
import { Table, AttributeType, BillingMode, TableEncryption } from 'aws-cdk-lib/aws-dynamodb';
import { OpenIdConnectProvider } from 'aws-cdk-lib/aws-iam';
import { Topic } from 'aws-cdk-lib/aws-sns';
import { EmailSubscription } from 'aws-cdk-lib/aws-sns-subscriptions';
import { CfnBudget } from 'aws-cdk-lib/aws-budgets';
import { Secret as SecretsManagerSecret } from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';

export interface RecoveryStackProps extends StackProps {
  readonly environmentName: string;
  readonly recoveryLibraryId: string;
  readonly recoveryOwnerId?: string;
  readonly recoveryAccountId: string;
  readonly primaryAccountId: string;
  readonly primaryAccountAlias: string;
  readonly recoveryAccountAlias: string;
  readonly primaryRegion: string;
  readonly primaryCaptureBucketName: string;
  readonly primaryIngestBucketName: string;
  readonly primaryKmsKeyArn: string;
  readonly primaryObservationQueueArn: string;
  readonly primaryObservationQueueUrl: string;
  readonly ownerEmail: string;
  readonly alertEmails: string[];
  readonly monthlyBudgetUsd: number;
  readonly databaseInstanceType: string;
  readonly databaseAllocatedStorageGiB: number;
  readonly databaseMaxStorageGiB: number;
  readonly workerEphemeralStorageGiB: number;
  readonly schedulesEnabled: boolean;
  readonly allowOfflinePlaceholders: boolean;
  readonly githubRepository: string;
  readonly githubOidcSubject: string;
  readonly recoveryImageTag: string;
  readonly recoveryImageDigest: string;
  readonly recoveryApiImageDigest: string;
  readonly recoveryApiBuildDigest: string;
  readonly recoveryIngestWorkerBuildDigest: string;
  readonly recoveryRendererBuildDigest: string;
  readonly recoveryTemplateDigest: string;
  readonly migrationDigest: string;
  readonly configurationDigest: string;
  readonly databaseRootCertificate: string;
  readonly databaseRootCertificateSha256: string;
  readonly auditGenesisVersionId: string;
  readonly auditGenesisSha256: string;
  readonly auditGenesisByteLength: number;
  readonly auditMinimumSequence: number;
  readonly auditMinimumEventSha256: string;
  readonly recoverySigningPublicKeySpkiBase64: string;
  readonly recoverySigningPublicKeySha256: string;
  readonly ownerApprovalRoleArn: string;
  readonly purgeOperatorArn: string;
  readonly ownerApprovalPublicKeySpkiBase64: string;
  readonly ownerApprovalPublicKeySha256: string;
}

const safeName = (value: string) => /^[a-z][a-z0-9-]{0,20}$/.test(value);
const validEmail = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value) && !/(@[^@]*(?:\.invalid|\.example|\.test)$|@example\.com$)/i.test(value);

export class RecoveryStack extends Stack {
  public constructor(scope: Construct, id: string, props: RecoveryStackProps) {
    super(scope, id, props);
    if (!safeName(props.environmentName)) throw new Error('Recovery environment name must be a short lower-case deployment identifier.');
    if (!/^[A-Za-z0-9._:-]{1,200}$/.test(props.recoveryLibraryId)) throw new Error('Recovery library ID must be an explicitly provisioned bounded identifier.');
    if (props.recoveryOwnerId !== undefined && !/^[A-Za-z0-9._:@-]{1,200}$/.test(props.recoveryOwnerId)) throw new Error('Recovery owner ID must be an explicitly provisioned bounded identifier.');
    if (!props.allowOfflinePlaceholders && !props.recoveryOwnerId) throw new Error('Recovery owner ID must be independently provisioned before deployment.');
    if (!/^\d{12}$/.test(props.recoveryAccountId) || this.account !== props.recoveryAccountId) throw new Error('Recovery stack account must match its explicit 12-digit target account.');
    if (!/^\d{12}$/.test(props.primaryAccountId) || props.primaryAccountId === props.recoveryAccountId) throw new Error('Recovery account must be distinct from the primary 12-digit account.');
    if (!/^[a-z][a-z0-9-]{2,119}$/.test(props.primaryAccountAlias) || !/^[a-z][a-z0-9-]{2,119}$/.test(props.recoveryAccountAlias) || props.primaryAccountAlias === props.recoveryAccountAlias) throw new Error('Distinct non-placeholder primary and recovery account aliases are required.');
    if (props.primaryRegion !== 'ap-southeast-1' || this.region !== 'ap-southeast-2') throw new Error('RecoveryStack must be deployed in ap-southeast-2 for primary ap-southeast-1.');
    if (!/^platform-[a-z][a-z0-9-]{0,20}-\d{12}-recovery-capture$/.test(props.primaryCaptureBucketName)) throw new Error('A real primary capture bucket name is required.');
    if (!/^[a-z0-9.-]{3,63}$/.test(props.primaryIngestBucketName) || props.primaryIngestBucketName.includes('example')) throw new Error('A real primary ingest bucket name is required.');
    if (!new RegExp(`^arn:[^:]+:kms:${props.primaryRegion}:${props.primaryAccountId}:key/[0-9a-f-]{36}$`).test(props.primaryKmsKeyArn)) throw new Error('A real primary KMS key ARN is required.');
    if (!new RegExp(`^arn:[^:]+:sqs:${props.primaryRegion}:${props.primaryAccountId}:platform-${props.environmentName}-recovery-observations$`).test(props.primaryObservationQueueArn)) throw new Error('A real primary recovery observation queue ARN is required.');
    if (!props.primaryObservationQueueUrl.startsWith(`https://sqs.${props.primaryRegion}.amazonaws.com/${props.primaryAccountId}/platform-${props.environmentName}-recovery-observations`)) throw new Error('A real primary recovery observation queue URL is required.');
    if (props.alertEmails.length === 0 || (!props.allowOfflinePlaceholders && (!validEmail(props.ownerEmail) || props.alertEmails.some(email => !validEmail(email))))) throw new Error('Recovery owner and alert emails must be real non-placeholder addresses.');
    if (!Number.isFinite(props.monthlyBudgetUsd) || props.monthlyBudgetUsd <= 0) throw new Error('Recovery monthly budget must be a positive owner-approved amount.');
    if (!/^[a-z0-9]+\.[a-z0-9]+$/.test(props.databaseInstanceType)) throw new Error('Recovery database instance type must be an owner-qualified measured class and size.');
    if (!Number.isInteger(props.databaseAllocatedStorageGiB) || props.databaseAllocatedStorageGiB < 20 || !Number.isInteger(props.databaseMaxStorageGiB) || props.databaseMaxStorageGiB < props.databaseAllocatedStorageGiB) throw new Error('Recovery database storage values must be measured whole GiB values with maximum >= initial >= 20.');
    if (!Number.isInteger(props.workerEphemeralStorageGiB) || props.workerEphemeralStorageGiB < 21 || props.workerEphemeralStorageGiB > 200) throw new Error('Recovery worker ephemeral storage must be a measured value from 21 through 200 GiB.');
    if (!/^[\w.-]+\/[\w.-]+$/.test(props.githubRepository) || !isRecoveryEnvironmentSubject(props.githubOidcSubject, props.githubRepository)) throw new Error('Recovery GitHub OIDC trust must identify this repository and the protected recovery-drill environment exactly.');
    if (!/^[a-f0-9]{40}$/.test(props.recoveryImageTag)) throw new Error('Recovery images must be pinned to the full source revision.');
    if (!/^[a-f0-9]{64}$/.test(props.recoveryImageDigest) || !/^[a-f0-9]{64}$/.test(props.recoveryApiImageDigest)) throw new Error('Recovery runtime images must be pinned to published immutable OCI digests.');
    if (props.recoveryApiImageDigest !== props.recoveryApiBuildDigest) throw new Error('Recovery API build binding must match the immutable API image digest.');
    for (const [name, digest] of [
      ['ingest worker', props.recoveryIngestWorkerBuildDigest], ['renderer', props.recoveryRendererBuildDigest],
      ['recovery template', props.recoveryTemplateDigest], ['migration', props.migrationDigest],
      ['configuration', props.configurationDigest], ['database root certificate', props.databaseRootCertificateSha256],
    ] as const) {
      if (!/^[a-f0-9]{64}$/.test(digest)) throw new Error(`Recovery ${name} requires a pinned SHA-256 digest.`);
    }
    const certificateDigest = createHash('sha256').update(props.databaseRootCertificate, 'utf8').digest('hex');
    if (certificateDigest !== props.databaseRootCertificateSha256) throw new Error('Recovery database root certificate digest does not match its exact UTF-8 bytes.');
    if (!props.allowOfflinePlaceholders && !/-----BEGIN CERTIFICATE-----[\s\S]+-----END CERTIFICATE-----/.test(props.databaseRootCertificate)) throw new Error('A valid PEM database root certificate is required for deployment.');
    if (!props.auditGenesisVersionId.trim() || props.auditGenesisVersionId.length > 1024 || props.auditGenesisVersionId === 'null' ||
      !/^[a-f0-9]{64}$/.test(props.auditGenesisSha256) || !Number.isInteger(props.auditGenesisByteLength) ||
      props.auditGenesisByteLength < 1 || props.auditGenesisByteLength > 64 * 1024) {
      throw new Error('Recovery audit genesis requires an exact S3 version, SHA-256 digest, and bounded byte length.');
    }
    if (!/^[a-f0-9]{64}$/.test(props.recoverySigningPublicKeySha256)) throw new Error('Recovery evidence KMS public key fingerprint must be pinned out of band.');
    if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(props.recoverySigningPublicKeySpkiBase64) || props.recoverySigningPublicKeySpkiBase64.length < 100) throw new Error('Recovery evidence signing SPKI must be pinned out of band.');
    if (!new RegExp(`^arn:[^:]+:iam::${props.recoveryAccountId}:role/[A-Za-z0-9+=,.@_/-]+$`).test(props.ownerApprovalRoleArn) || (!props.allowOfflinePlaceholders && props.ownerApprovalRoleArn.includes('example'))) throw new Error('A separately provisioned recovery-account MFA owner-approval role is required.');
    if (!new RegExp(`^arn:[^:]+:iam::${props.recoveryAccountId}:(?:user|role)/[A-Za-z0-9+=,.@_/-]+$`).test(props.purgeOperatorArn) || (!props.allowOfflinePlaceholders && props.purgeOperatorArn.includes('example'))) throw new Error('A separately provisioned recovery-account MFA purge operator principal is required.');
    if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(props.ownerApprovalPublicKeySpkiBase64) || props.ownerApprovalPublicKeySpkiBase64.length < 100 || !/^[a-f0-9]{64}$/.test(props.ownerApprovalPublicKeySha256)) throw new Error('The owner approval public SPKI and fingerprint must be supplied from the separately provisioned KMS key.');

    const prefix = `platform-${props.environmentName}-recovery`;
    const templateDigest = new CfnParameter(this, 'RecoveryTemplateDigest', {
      type: 'String', allowedPattern: '^[a-f0-9]{64}$', minLength: 64, maxLength: 64,
      description: 'SHA-256 of the independently verified exact synthesized template bytes; no default.',
    }).valueAsString;
    if (!Number.isSafeInteger(props.auditMinimumSequence) || props.auditMinimumSequence < 0 ||
        !/^[a-f0-9]{64}$/.test(props.auditMinimumEventSha256)) throw new Error('An independently pinned audit minimum is required.');
    const primaryReadRoleName = `platform-${props.environmentName}-recovery-source-read`;
    const primaryReadRoleArn = Stack.of(this).formatArn({ service: 'iam', region: '', account: props.primaryAccountId, resource: 'role', resourceName: primaryReadRoleName, arnFormat: ArnFormat.SLASH_RESOURCE_NAME });
    const vpc = new Vpc(this, 'RecoveryVpc', {
      ...(props.allowOfflinePlaceholders
        ? { availabilityZones: ['ap-southeast-2a', 'ap-southeast-2b'] }
        : { maxAzs: 2 }),
      natGateways: 1,
      subnetConfiguration: [
        { name: 'edge', subnetType: SubnetType.PUBLIC },
        { name: 'worker', subnetType: SubnetType.PRIVATE_WITH_EGRESS },
        { name: 'data', subnetType: SubnetType.PRIVATE_ISOLATED },
      ],
    });
    const encryptionKey = new Key(this, 'RecoveryEncryptionKey', {
      enableKeyRotation: true,
      removalPolicy: RemovalPolicy.RETAIN,
      pendingWindow: Duration.days(30),
    });
    const evidenceEncryptionKey = new Key(this, 'RecoveryEvidenceEncryptionKey', {
      enableKeyRotation: true,
      removalPolicy: RemovalPolicy.RETAIN,
      pendingWindow: Duration.days(30),
    });
    const evidenceSigningKey = new Key(this, 'RecoveryEvidenceSigningKey', {
      keySpec: KeySpec.RSA_2048,
      keyUsage: KeyUsage.SIGN_VERIFY,
      enableKeyRotation: false,
      removalPolicy: RemovalPolicy.RETAIN,
      pendingWindow: Duration.days(30),
    });
    const ownerApprovalKey = new Key(this, 'RecoveryOwnerApprovalKey', {
      keySpec: KeySpec.RSA_2048,
      keyUsage: KeyUsage.SIGN_VERIFY,
      enableKeyRotation: false,
      removalPolicy: RemovalPolicy.RETAIN,
      pendingWindow: Duration.days(30),
    });
    const ownerApprovalRole = aws_iam.Role.fromRoleArn(this, 'RecoveryOwnerApprovalRole', props.ownerApprovalRoleArn, { mutable: false });
    ownerApprovalKey.grantSign(ownerApprovalRole);
    const bucket = new Bucket(this, 'RecoveryBucket', {
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      encryption: BucketEncryption.KMS,
      encryptionKey,
      enforceSSL: true,
      versioned: true,
      objectLockEnabled: true,
      objectLockDefaultRetention: ObjectLockRetention.governance(Duration.days(90)),
      bucketKeyEnabled: false,
      removalPolicy: RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
      lifecycleRules: [{
        id: 'payload-to-glacier-instant-retrieval-after-90-days',
        prefix: 'payload/sha256/',
        objectSizeGreaterThan: 0,
        transitions: [{ storageClass: StorageClass.GLACIER_INSTANT_RETRIEVAL, transitionAfter: Duration.days(90) }],
        noncurrentVersionTransitions: [{ storageClass: StorageClass.GLACIER_INSTANT_RETRIEVAL, transitionAfter: Duration.days(90) }],
      }],
    });
    const evidenceBucket = new Bucket(this, 'RecoveryEvidenceBucket', {
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      encryption: BucketEncryption.KMS,
      encryptionKey: evidenceEncryptionKey,
      enforceSSL: true,
      versioned: true,
      objectLockEnabled: true,
      objectLockDefaultRetention: ObjectLockRetention.compliance(Duration.days(90)),
      bucketKeyEnabled: false,
      removalPolicy: RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
    });
    const controlTable = new Table(this, 'RecoveryControlTable', {
      tableName: `${prefix}-control`,
      partitionKey: { name: 'pk', type: AttributeType.STRING },
      sortKey: { name: 'sk', type: AttributeType.STRING },
      billingMode: BillingMode.PAY_PER_REQUEST,
      encryption: TableEncryption.CUSTOMER_MANAGED,
      encryptionKey,
      pointInTimeRecovery: true,
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const verificationTable = new Table(this, 'RecoveryVerificationTable', {
      tableName: `${prefix}-verification`, partitionKey: { name: 'pk', type: AttributeType.STRING },
      sortKey: { name: 'sk', type: AttributeType.STRING }, billingMode: BillingMode.PAY_PER_REQUEST,
      encryption: TableEncryption.CUSTOMER_MANAGED, encryptionKey, pointInTimeRecovery: true,
      deletionProtection: true, removalPolicy: RemovalPolicy.RETAIN,
    });
    const fenceTable = new Table(this, 'RecoveryFenceTable', {
      tableName: `${prefix}-fence`,
      partitionKey: { name: 'scopeId', type: AttributeType.STRING },
      sortKey: { name: 'recordId', type: AttributeType.STRING },
      billingMode: BillingMode.PAY_PER_REQUEST,
      encryption: TableEncryption.CUSTOMER_MANAGED,
      encryptionKey,
      pointInTimeRecovery: true,
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const intentTable = new Table(this, 'RecoveryIntentTable', {
      tableName: `${prefix}-intent`,
      partitionKey: { name: 'scopeId', type: AttributeType.STRING },
      sortKey: { name: 'recordId', type: AttributeType.STRING },
      billingMode: BillingMode.PAY_PER_REQUEST,
      encryption: TableEncryption.CUSTOMER_MANAGED,
      encryptionKey,
      pointInTimeRecovery: true,
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const liveBucket = new Bucket(this, 'RecoveryLiveLibraryBucket', {
      blockPublicAccess: BlockPublicAccess.BLOCK_ALL,
      encryption: BucketEncryption.KMS,
      encryptionKey,
      enforceSSL: true,
      versioned: true,
      removalPolicy: RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
      lifecycleRules: [{ prefix: 'staging/', expiration: Duration.days(30), noncurrentVersionExpiration: Duration.days(7), abortIncompleteMultipartUploadAfter: Duration.days(2) }],
    });
    const auditLogs = new LogGroup(this, 'RecoveryWorkerLogs', {
      logGroupName: `/platform/${props.environmentName}/recovery`,
      encryptionKey,
      retention: RetentionDays.ONE_YEAR,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const alerts = new Topic(this, 'RecoveryAlerts', { displayName: `${prefix} alerts` });
    for (const email of props.alertEmails) alerts.addSubscription(new EmailSubscription(email));
    new CfnBudget(this, 'RecoveryMonthlyBudget', {
      budget: { budgetName: `${prefix}-monthly-cost`, budgetLimit: { amount: props.monthlyBudgetUsd, unit: 'USD' }, timeUnit: 'MONTHLY', budgetType: 'COST' },
      notificationsWithSubscribers: [80, 100].map(threshold => ({
        notification: { comparisonOperator: 'GREATER_THAN', notificationType: 'ACTUAL', threshold, thresholdType: 'PERCENTAGE' },
        subscribers: props.alertEmails.map(address => ({ address, subscriptionType: 'EMAIL' })),
      })),
    });

    const copyRole = new aws_iam.Role(this, 'RecoveryCopyTaskRole', {
      roleName: `platform-${props.environmentName}-recovery-copy`,
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      description: 'Recovery-side copy identity; primary access is a time-bounded, explicit AssumeRole only.',
    });
    copyRole.addToPolicy(new aws_iam.PolicyStatement({ actions: ['sts:AssumeRole'], resources: [primaryReadRoleArn] }));
    copyRole.addToPolicy(new aws_iam.PolicyStatement({
      sid: 'ExtendOnlyExactRecoveryMediaGovernanceRetention',
      actions: ['s3:PutObjectRetention'], resources: [bucket.arnForObjects('payload/sha256/*')],
      conditions: {
        StringEquals: { 's3:object-lock-mode': 'GOVERNANCE' },
        NumericGreaterThanEquals: { 's3:object-lock-remaining-retention-days': '90' },
        NumericLessThanEquals: { 's3:object-lock-remaining-retention-days': '92' },
      },
    }));
    addRecoveryDataPolicy(copyRole, bucket, controlTable, ['payload/sha256/*', 'snapshots/*', 'points/*', 'evidence/*'], ['payload/sha256/*', 'snapshots/*', 'points/*', 'evidence/*'], ['GetItem', 'PutItem', 'UpdateItem', 'Query', 'DescribeTable']);
    copyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:ConditionCheckItem'],
      resources: [fenceTable.tableArn],
    }));
    copyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:Query'],
      resources: [intentTable.tableArn],
    }));
    encryptionKey.grantEncryptDecrypt(copyRole);
    evidenceSigningKey.grantSignVerify(copyRole);
    copyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['kms:GetPublicKey'], resources: [evidenceSigningKey.keyArn] }));
    addRecoveryEvidencePolicy(copyRole, evidenceBucket, ['points/*', 'evidence/*']);
    evidenceEncryptionKey.grantEncryptDecrypt(copyRole);

    const purgeExecutorRole = new aws_iam.Role(this, 'RecoveryPurgeExecutorRole', {
      roleName: `${prefix}-purge-executor`,
      assumedBy: new aws_iam.ArnPrincipal(props.purgeOperatorArn).withConditions({
        Bool: { 'aws:MultiFactorAuthPresent': 'true' },
      }),
      description: 'Independent MFA recovery purge CLI; exact-version payload cleanup only, with no evidence deletion or primary access.',
    });
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem'],
      resources: [controlTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['OWNER#*', 'POINT#*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      sid: 'DecryptOnlyPurgeTablesThroughDynamoDb',
      actions: ['kms:Decrypt'], resources: [encryptionKey.keyArn],
      conditions: { StringEquals: {
        'kms:ViaService': 'dynamodb.ap-southeast-2.amazonaws.com',
        'kms:CallerAccount': props.recoveryAccountId,
        'kms:EncryptionContext:aws:dynamodb:tableName': [controlTable.tableName, intentTable.tableName, fenceTable.tableName],
        'kms:EncryptionContext:aws:dynamodb:subscriberId': props.recoveryAccountId,
      } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:GetObjectVersion', 's3:DeleteObjectVersion'],
      resources: [liveBucket.arnForObjects('sha256/*')],
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:ListBucketVersions'], resources: [liveBucket.bucketArn],
      conditions: { StringLike: { 's3:prefix': ['sha256/*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      sid: 'DecryptOnlyIsolatedPayloadThroughS3',
      actions: ['kms:Decrypt'], resources: [encryptionKey.keyArn],
      conditions: { StringEquals: {
        'kms:ViaService': 's3.ap-southeast-2.amazonaws.com',
        'kms:CallerAccount': props.recoveryAccountId,
      }, StringLike: { 'kms:EncryptionContext:aws:s3:arn': [liveBucket.arnForObjects('sha256/*')] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:Query'],
      resources: [controlTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['OWNER#*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:Query'],
      resources: [intentTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['MEDIA#*', 'SNAPSHOT#*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:ConditionCheckItem'],
      resources: [fenceTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['MEDIA#*', 'SNAPSHOT#*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:DeleteItem'],
      resources: [fenceTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['SNAPSHOT#*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:ListBucketVersions'],
      resources: [bucket.bucketArn],
      conditions: { StringLike: { 's3:prefix': ['payload/sha256/*', 'snapshots/*'] } },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:GetObjectVersion', 's3:DeleteObjectVersion'],
      resources: [bucket.arnForObjects('payload/sha256/*'), bucket.arnForObjects('snapshots/*')],
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:GetObjectVersion'],
      resources: [evidenceBucket.arnForObjects('points/*')],
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['kms:Decrypt'],
      resources: [encryptionKey.keyArn],
      conditions: {
        StringEquals: {
          'kms:ViaService': `s3.${this.region}.amazonaws.com`,
          'kms:CallerAccount': props.recoveryAccountId,
        },
        StringLike: {
          'kms:EncryptionContext:aws:s3:arn': [bucket.arnForObjects('payload/sha256/*'), bucket.arnForObjects('snapshots/*')],
        },
      },
    }));
    purgeExecutorRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['kms:Decrypt'],
      resources: [evidenceEncryptionKey.keyArn],
      conditions: {
        StringEquals: {
          'kms:ViaService': `s3.${this.region}.amazonaws.com`,
          'kms:CallerAccount': props.recoveryAccountId,
        },
        StringLike: { 'kms:EncryptionContext:aws:s3:arn': [evidenceBucket.arnForObjects('points/*')] },
      },
    }));

    const recoveryOnlyRole = new aws_iam.Role(this, 'RecoveryOnlyTaskRole', {
      roleName: `platform-${props.environmentName}-recovery-verify`,
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      description: 'Verify and drill using recovery resources only; deliberately has no primary AssumeRole permission.',
    });
    addRecoveryDataPolicy(recoveryOnlyRole, bucket, controlTable, ['payload/sha256/*', 'snapshots/*', 'points/*', 'evidence/*', 'restore/*'], ['evidence/*', 'restore/*'], ['GetItem', 'PutItem', 'UpdateItem', 'Query', 'Scan', 'DescribeTable']);
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:ConditionCheckItem'],
      resources: [fenceTable.tableArn],
    }));
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:Query'],
      resources: [intentTable.tableArn],
    }));
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:PutItem', 'dynamodb:UpdateItem'],
      resources: [intentTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['MEDIA#*'] } },
    }));
    liveBucket.grantRead(recoveryOnlyRole, 'sha256/*');
    liveBucket.grantPut(recoveryOnlyRole, 'sha256/*');
    encryptionKey.grantEncryptDecrypt(recoveryOnlyRole);
    evidenceSigningKey.grantSignVerify(recoveryOnlyRole);
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({ actions: ['kms:GetPublicKey'], resources: [evidenceSigningKey.keyArn] }));
    addRecoveryEvidencePolicy(recoveryOnlyRole, evidenceBucket, ['points/*', 'evidence/*']);
    evidenceEncryptionKey.grantEncryptDecrypt(recoveryOnlyRole);

    const destinationBucketMetadataActions = [
      's3:GetBucketLocation', 's3:GetEncryptionConfiguration', 's3:GetBucketPublicAccessBlock',
      's3:GetBucketPolicyStatus', 's3:GetBucketOwnershipControls', 's3:GetBucketVersioning',
      's3:GetObjectLockConfiguration',
    ];
    for (const role of [copyRole, recoveryOnlyRole]) {
      role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
        actions: ['s3:GetObjectRetention'],
        resources: [bucket.arnForObjects('payload/sha256/*'), bucket.arnForObjects('snapshots/*'),
          evidenceBucket.arnForObjects('points/*'), evidenceBucket.arnForObjects('evidence/*')],
      }));
      role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
        actions: destinationBucketMetadataActions,
        resources: [bucket.bucketArn, evidenceBucket.bucketArn],
      }));
    }

    const runtimePins = {
      Recovery__SourceRevision: props.recoveryImageTag,
      Recovery__ApiBuildDigest: props.recoveryApiBuildDigest,
      Recovery__IngestWorkerBuildDigest: props.recoveryIngestWorkerBuildDigest,
      Recovery__RecoveryWorkerBuildDigest: props.recoveryImageDigest,
      Recovery__RendererBuildDigest: props.recoveryRendererBuildDigest,
      Recovery__RecoveryTemplateDigest: templateDigest,
      Recovery__MigrationDigest: props.migrationDigest,
      Recovery__MigrationDirectory: '/app/migrations',
      Recovery__ConfigurationDigest: props.configurationDigest,
      Recovery__DatabaseRootCertificateSha256: props.databaseRootCertificateSha256,
      Recovery__AuditGenesisKey: 'evidence/audit/genesis.v1.json',
      Recovery__AuditGenesisVersionId: props.auditGenesisVersionId,
      Recovery__AuditGenesisSha256: props.auditGenesisSha256,
      Recovery__AuditGenesisByteLength: String(props.auditGenesisByteLength),
      Recovery__AuditMinimumSequence: String(props.auditMinimumSequence),
      Recovery__AuditMinimumEventSha256: props.auditMinimumEventSha256,
    };

    const createRepository = (name: string, id: string) => new Repository(this, id, {
      repositoryName: `${prefix}-${name}`,
      imageTagMutability: TagMutability.IMMUTABLE,
      imageScanOnPush: true,
      removalPolicy: RemovalPolicy.RETAIN,
      emptyOnDelete: false,
    });
    const apiRepository = createRepository('api', 'RecoveryApiRepository');
    const ingestWorkerRepository = createRepository('ingest-worker', 'RecoveryIngestWorkerRepository');
    const rendererRepository = createRepository('render-worker', 'RecoveryRendererRepository');
    const recoveryWorkerRepository = createRepository('recovery-worker', 'RecoveryWorkerRepository');
    const cluster = new Cluster(this, 'RecoveryCluster', { vpc, clusterName: prefix });
    const executionRole = new aws_iam.Role(this, 'RecoveryCopyExecutionRole', {
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
    });
    recoveryWorkerRepository.grantPull(executionRole);
    auditLogs.grantWrite(executionRole);
    const copyTask = new FargateTaskDefinition(this, 'RecoveryCopyTask', {
      family: `${prefix}-copy`, cpu: 1024, memoryLimitMiB: 4096, ephemeralStorageGiB: props.workerEphemeralStorageGiB,
      executionRole, taskRole: copyRole,
    });
    const copyContainer = copyTask.addContainer('recovery-worker', {
      image: ContainerImage.fromRegistry(`${recoveryWorkerRepository.repositoryUri}@sha256:${props.recoveryImageDigest}`),
      command: ['copy'],
      environment: {
        ...runtimePins,
        Recovery__Environment: props.environmentName,
        Recovery__WorkerId: 'copy',
        Recovery__LibraryId: props.recoveryLibraryId,
        ...(props.recoveryOwnerId ? { Recovery__OwnerId: props.recoveryOwnerId } : {}),
        Recovery__SourceRegion: props.primaryRegion,
        Recovery__SourceAccountId: props.primaryAccountId,
        Recovery__SourceAccountAlias: props.primaryAccountAlias,
        Recovery__DestinationAccountId: props.recoveryAccountId,
        Recovery__DestinationAccountAlias: props.recoveryAccountAlias,
        Recovery__SourceCaptureBucket: props.primaryCaptureBucketName,
        Recovery__SourceBucket: props.primaryIngestBucketName,
        Recovery__PrimaryReadRoleArn: primaryReadRoleArn,
        Recovery__DestinationBucket: bucket.bucketName,
        Recovery__EvidenceBucket: evidenceBucket.bucketName,
        Recovery__DestinationRegion: this.region,
        Recovery__DestinationKmsKeyId: encryptionKey.keyArn,
        Recovery__EvidenceKmsKeyId: evidenceEncryptionKey.keyArn,
        Recovery__ControlTableName: controlTable.tableName,
        Recovery__FenceTableName: fenceTable.tableName,
        Recovery__IntentTableName: intentTable.tableName,
        Recovery__CaptureIntervalMinutes: '15',
        Recovery__SigningKeyId: evidenceSigningKey.keyArn,
        Recovery__SigningPublicKeySha256: props.recoverySigningPublicKeySha256,
        Recovery__OwnerApprovalKeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__KeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__PublicKeySpkiBase64: props.ownerApprovalPublicKeySpkiBase64,
        Recovery__OwnerApprovalTrust__PublicKeySha256: props.ownerApprovalPublicKeySha256,
      },
      logging: LogDrivers.awsLogs({ logGroup: auditLogs, streamPrefix: 'copy' }),
    });

    const bootstrapRole = new aws_iam.Role(this, 'RecoveryIndexBootstrapTaskRole', {
      roleName: `${prefix}-index-bootstrap`,
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      description: 'One-time recovery owner-point-index bootstrap; no primary, payload, evidence, database, signing, fence, or purge access.',
    });
    bootstrapRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:Scan', 'dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem'],
      resources: [controlTable.tableArn],
    }));
    const bootstrapExecutionRole = new aws_iam.Role(this, 'RecoveryIndexBootstrapExecutionRole', {
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
    });
    recoveryWorkerRepository.grantPull(bootstrapExecutionRole);
    auditLogs.grantWrite(bootstrapExecutionRole);
    const bootstrapTask = new FargateTaskDefinition(this, 'RecoveryIndexBootstrapTask', {
      family: `${prefix}-index-bootstrap`, cpu: 1024, memoryLimitMiB: 2048,
      executionRole: bootstrapExecutionRole, taskRole: bootstrapRole,
    });
    bootstrapTask.addContainer('recovery-worker', {
      containerName: 'recovery-worker',
      image: ContainerImage.fromRegistry(`${recoveryWorkerRepository.repositoryUri}@sha256:${props.recoveryImageDigest}`),
      command: ['bootstrap-index'],
      environment: {
        Recovery__Environment: props.environmentName,
        Recovery__WorkerId: 'bootstrap-index',
        Recovery__RecoveryWorkerBuildDigest: props.recoveryImageDigest,
        Recovery__DestinationRegion: this.region,
        Recovery__DestinationAccountId: props.recoveryAccountId,
        Recovery__ControlTableName: controlTable.tableName,
        ...(props.recoveryOwnerId ? {
          Recovery__OwnerId: props.recoveryOwnerId,
          Recovery__LibraryId: props.recoveryLibraryId,
        } : {}),
      },
      logging: LogDrivers.awsLogs({ logGroup: auditLogs, streamPrefix: 'bootstrap-index' }),
    });

    const recoveryExecutionRole = new aws_iam.Role(this, 'RecoveryDrillExecutionRole', {
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
    });
    const copySecurityGroup = new SecurityGroup(this, 'RecoveryCopySecurityGroup', { vpc, allowAllOutbound: true });
    const recoveryTaskSecurityGroup = new SecurityGroup(this, 'RecoveryDrillSecurityGroup', { vpc, allowAllOutbound: true });
    if (props.schedulesEnabled) {
      new Rule(this, 'RecoveryCopySchedule', {
        schedule: Schedule.rate(Duration.minutes(5)),
        targets: [new EcsTask({ cluster, taskDefinition: copyTask, securityGroups: [copySecurityGroup], subnetSelection: { subnetType: SubnetType.PRIVATE_WITH_EGRESS }, assignPublicIp: false, containerOverrides: [{ containerName: 'recovery-worker', command: ['copy'] }] })],
      });
    }

    const databaseSecurityGroup = new SecurityGroup(this, 'RecoveryDatabaseSecurityGroup', { vpc, allowAllOutbound: false });
    databaseSecurityGroup.addIngressRule(recoveryTaskSecurityGroup, Port.tcp(5432), 'Only isolated restore/drill workers may access recovered PostgreSQL.');
    const database = new DatabaseInstance(this, 'RecoveryDatabase', {
      engine: DatabaseInstanceEngine.postgres({ version: PostgresEngineVersion.of('18.4', '18') }),
      credentials: Credentials.fromGeneratedSecret('recovery_admin', { encryptionKey }),
      databaseName: 'platform_recovery',
      instanceType: new InstanceType(props.databaseInstanceType),
      vpc,
      vpcSubnets: { subnetType: SubnetType.PRIVATE_ISOLATED },
      securityGroups: [databaseSecurityGroup],
      multiAz: false,
      publiclyAccessible: false,
      allocatedStorage: props.databaseAllocatedStorageGiB,
      maxAllocatedStorage: props.databaseMaxStorageGiB,
      storageEncrypted: true,
      storageEncryptionKey: encryptionKey,
      backupRetention: Duration.days(7),
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
      cloudwatchLogsExports: ['postgresql'],
    });
    if (!database.secret) throw new Error('Recovery database must expose a recovery-owned generated secret.');
    database.secret.grantRead(recoveryOnlyRole);
    const runtimeDatabaseSecret = new SecretsManagerSecret(this, 'RecoveryDatabaseRuntimeCredentials', {
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ username: 'platform_runtime' }),
        generateStringKey: 'password',
        excludePunctuation: true,
        passwordLength: 32,
      },
      encryptionKey,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['secretsmanager:GetSecretValue'], resources: [runtimeDatabaseSecret.secretArn],
    }));
    const ownerScope = createHash('sha256').update(`${props.recoveryOwnerId ?? 'offline-owner-not-qualified'}\0${props.recoveryLibraryId}`, 'utf8').digest('hex');
    const verifierRole = new aws_iam.Role(this, 'RecoveryVerifierPublisherRole', {
      roleName: `${prefix}-verify-publisher`,
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com').withConditions({
        StringEquals: { 'aws:SourceAccount': props.recoveryAccountId },
        ArnLike: { 'aws:SourceArn': `arn:aws:ecs:${this.region}:${props.recoveryAccountId}:*` },
      }),
      description: 'Independent read-only point verifier with retained verification evidence and exact-queue publication.',
    });
    const verifierBindings: Record<string,string> = {
      __RECOVERY_DATABASE_SECRET_ARN__: database.secret.secretArn,
      __RECOVERY_DATABASE_SECRET_KMS_KEY_ARN__: encryptionKey.keyArn,
      __RECOVERY_ACCOUNT_ID__: props.recoveryAccountId,
      __RECOVERY_EVIDENCE_SIGNING_KEY_ARN__: evidenceSigningKey.keyArn,
      __RECOVERY_PAYLOAD_BUCKET_ARN__: bucket.bucketArn,
      __RECOVERY_EVIDENCE_BUCKET_ARN__: evidenceBucket.bucketArn,
      __RECOVERY_PAYLOAD_KMS_KEY_ARN__: encryptionKey.keyArn,
      __RECOVERY_EVIDENCE_KMS_KEY_ARN__: evidenceEncryptionKey.keyArn,
      __RECOVERY_CONTROL_TABLE_ARN__: controlTable.tableArn,
      __RECOVERY_VERIFICATION_TABLE_ARN__: verificationTable.tableArn,
      __LOWERCASE_SHA256_OF_UTF8_OWNER_ID_NUL_LIBRARY_ID__: ownerScope,
      __EXACT_PRIMARY_OBSERVATION_QUEUE_ARN__: props.primaryObservationQueueArn,
    };
    const approvedVerifierPolicy = JSON.parse(new TextDecoder().decode(readFileSync(fileURLToPath(
      new URL('../../policy-proposals/recovery-verifier-publisher.v1.json', import.meta.url))))).policyDocument;
    const boundVerifierPolicy = JSON.parse(JSON.stringify(approvedVerifierPolicy).replace(/__[A-Z0-9_]+__/g, placeholder => {
      const value = verifierBindings[placeholder];
      if (!value) throw new Error(`Unbound approved verifier policy: ${placeholder}`);
      return value;
    }));
    for (const statement of boundVerifierPolicy.Statement)
      verifierRole.addToPrincipalPolicy(aws_iam.PolicyStatement.fromJson(statement));
    verifierRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      sid: 'DecryptOnlyRecoveryControlAndVerificationThroughDynamoDb',
      actions: ['kms:Decrypt'], resources: [encryptionKey.keyArn],
      conditions: { StringEquals: {
        'kms:ViaService': 'dynamodb.ap-southeast-2.amazonaws.com',
        'kms:CallerAccount': props.recoveryAccountId,
        'kms:EncryptionContext:aws:dynamodb:tableName': [controlTable.tableName, verificationTable.tableName],
        'kms:EncryptionContext:aws:dynamodb:subscriberId': props.recoveryAccountId,
      } },
    }));
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      sid: 'ReadOnlyRecoveryVerificationProofs', actions: ['dynamodb:GetItem'], resources: [verificationTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['POINT#*', `OWNER#${ownerScope}`] } },
    }));
    recoveryWorkerRepository.grantPull(recoveryExecutionRole);
    auditLogs.grantWrite(recoveryExecutionRole);
    const drillTask = new FargateTaskDefinition(this, 'RecoveryDrillTask', {
      family: `${prefix}-drill`, cpu: 1024, memoryLimitMiB: 4096, ephemeralStorageGiB: 100,
      executionRole: recoveryExecutionRole, taskRole: recoveryOnlyRole,
    });
    const drillContainer = drillTask.addContainer('recovery-worker', {
      image: ContainerImage.fromRegistry(`${recoveryWorkerRepository.repositoryUri}@sha256:${props.recoveryImageDigest}`),
      command: ['drill'],
      environment: {
        ...runtimePins,
        Recovery__Environment: props.environmentName,
        Recovery__WorkerId: 'drill',
        Recovery__LibraryId: props.recoveryLibraryId,
        ...(props.recoveryOwnerId ? { Recovery__OwnerId: props.recoveryOwnerId } : {}),
        Recovery__SourceAccountId: props.primaryAccountId,
        Recovery__SourceAccountAlias: props.primaryAccountAlias,
        Recovery__DestinationAccountId: props.recoveryAccountId,
        Recovery__DestinationAccountAlias: props.recoveryAccountAlias,
        Recovery__DestinationBucket: bucket.bucketName,
        Recovery__EvidenceBucket: evidenceBucket.bucketName,
        Recovery__TargetBucket: liveBucket.bucketName,
        Recovery__TargetKmsKeyId: encryptionKey.keyArn,
        Recovery__ApiImageDigest: props.recoveryApiImageDigest,
        Recovery__SourceRegion: props.primaryRegion,
        Recovery__SourceBucket: props.primaryIngestBucketName,
        Recovery__DestinationRegion: this.region,
        Recovery__DestinationKmsKeyId: encryptionKey.keyArn,
        Recovery__EvidenceKmsKeyId: evidenceEncryptionKey.keyArn,
        Recovery__ControlTableName: controlTable.tableName,
        Recovery__VerificationTableName: verificationTable.tableName,
        Recovery__FenceTableName: fenceTable.tableName,
        Recovery__IntentTableName: intentTable.tableName,
        Recovery__Database__Host: database.dbInstanceEndpointAddress,
        Recovery__Database__Port: database.dbInstanceEndpointPort,
        Recovery__Database__Name: 'platform_recovery',
        Recovery__DatabaseRootCertificate: props.databaseRootCertificate,
        Recovery__DatabaseSecretArn: database.secret.secretArn,
        Recovery__RuntimeDatabaseSecretArn: runtimeDatabaseSecret.secretArn,
        Recovery__RecoveryOnly: 'true',
        Recovery__SigningKeyId: evidenceSigningKey.keyArn,
        Recovery__SigningPublicKeySha256: props.recoverySigningPublicKeySha256,
        Recovery__OwnerApprovalKeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__KeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__PublicKeySpkiBase64: props.ownerApprovalPublicKeySpkiBase64,
        Recovery__OwnerApprovalTrust__PublicKeySha256: props.ownerApprovalPublicKeySha256,
      },
      logging: LogDrivers.awsLogs({ logGroup: auditLogs, streamPrefix: 'drill' }),
    });
    const pool = new UserPool(this, 'RecoveryOwnerUserPool', {
      userPoolName: `${prefix}-owner`,
      signInAliases: { email: true },
      selfSignUpEnabled: false,
      accountRecovery: AccountRecovery.NONE,
      mfa: Mfa.REQUIRED,
      mfaSecondFactor: { otp: true, sms: false, email: false },
      standardAttributes: { email: { required: true, mutable: false } },
      autoVerify: { email: true },
      deletionProtection: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const poolClient = pool.addClient('RecoveryOwnerClient', { userPoolClientName: `${prefix}-owner-client`, generateSecret: false, preventUserExistenceErrors: true });
    const ownerPoolIssuer = `https://cognito-idp.${this.region}.amazonaws.com/${pool.userPoolId}`;
    for (const container of [copyContainer, drillContainer]) {
      container.addEnvironment('Recovery__OwnerUserPoolId', pool.userPoolId);
      container.addEnvironment('Recovery__OwnerUserPoolClientId', poolClient.userPoolClientId);
      container.addEnvironment('Recovery__OwnerUserPoolIssuer', ownerPoolIssuer);
    }
    new CfnUserPoolGroup(this, 'RecoveryOwnerGroup', { userPoolId: pool.userPoolId, groupName: 'owner', description: 'Owner authorization group for the isolated recovered library.' });

    const apiExecutionRole = new aws_iam.Role(this, 'RecoveryApiExecutionRole', {
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      managedPolicies: [aws_iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AmazonECSTaskExecutionRolePolicy')],
    });
    apiRepository.grantPull(apiExecutionRole);
    auditLogs.grantWrite(apiExecutionRole);
    runtimeDatabaseSecret.grantRead(apiExecutionRole);
    const apiTaskRole = new aws_iam.Role(this, 'RecoveryApiTaskRole', {
      assumedBy: new aws_iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
      description: 'Recovery API may use only the isolated restored content bucket and its runtime database identity.',
    });
    apiTaskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:ConditionCheckItem'],
      resources: [fenceTable.tableArn],
    }));
    apiTaskRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:Query'],
      resources: [intentTable.tableArn],
    }));
    liveBucket.grantRead(apiTaskRole, 'sha256/*');
    liveBucket.grantPut(apiTaskRole, 'sha256/*');
    liveBucket.grantReadWrite(apiTaskRole, 'staging/*');
    liveBucket.grantReadWrite(apiTaskRole, 'quarantine/*');
    encryptionKey.grantEncryptDecrypt(apiTaskRole);
    const apiSecurityGroup = new SecurityGroup(this, 'RecoveryApiSecurityGroup', { vpc, allowAllOutbound: true });
    apiSecurityGroup.addIngressRule(recoveryTaskSecurityGroup, Port.tcp(8080), 'Only the recovery runner may access the isolated API task.');
    database.connections.allowDefaultPortFrom(apiSecurityGroup);
    const isolatedEndpointSecurityGroup = new SecurityGroup(this, 'RecoveryIsolatedEndpointSecurityGroup', { vpc, allowAllOutbound: true });
    isolatedEndpointSecurityGroup.addIngressRule(recoveryTaskSecurityGroup, Port.tcp(443), 'Recovery-only workers may use regional AWS endpoints.');
    isolatedEndpointSecurityGroup.addIngressRule(apiSecurityGroup, Port.tcp(443), 'Recovery API may use regional AWS endpoints.');
    vpc.addGatewayEndpoint('RecoveryIsolatedS3Endpoint', {
      service: GatewayVpcEndpointAwsService.S3,
      subnets: [{ subnetType: SubnetType.PRIVATE_ISOLATED }, { subnetType: SubnetType.PRIVATE_WITH_EGRESS }],
    });
    vpc.addGatewayEndpoint('RecoveryDynamoDbEndpoint', {
      service: GatewayVpcEndpointAwsService.DYNAMODB,
      subnets: [{ subnetType: SubnetType.PRIVATE_ISOLATED }, { subnetType: SubnetType.PRIVATE_WITH_EGRESS }],
    });
    for (const [id, service] of [
      ['RecoveryIsolatedEcrApiEndpoint', InterfaceVpcEndpointAwsService.ECR],
      ['RecoveryIsolatedEcrDockerEndpoint', InterfaceVpcEndpointAwsService.ECR_DOCKER],
      ['RecoveryIsolatedLogsEndpoint', InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS],
      ['RecoveryIsolatedMetricsEndpoint', InterfaceVpcEndpointAwsService.CLOUDWATCH_MONITORING],
      ['RecoveryIsolatedSecretsEndpoint', InterfaceVpcEndpointAwsService.SECRETS_MANAGER],
      ['RecoveryIsolatedKmsEndpoint', InterfaceVpcEndpointAwsService.KMS],
      ['RecoveryIsolatedEcsEndpoint', InterfaceVpcEndpointAwsService.ECS],
      ['RecoveryIsolatedEcsAgentEndpoint', InterfaceVpcEndpointAwsService.ECS_AGENT],
      ['RecoveryIsolatedEcsTelemetryEndpoint', InterfaceVpcEndpointAwsService.ECS_TELEMETRY],
      ['RecoveryIsolatedCognitoEndpoint', InterfaceVpcEndpointAwsService.COGNITO_IDP],
      ['RecoveryIsolatedStsEndpoint', InterfaceVpcEndpointAwsService.STS],
    ] as const) {
      vpc.addInterfaceEndpoint(id, {
        service,
        securityGroups: [isolatedEndpointSecurityGroup],
        open: false,
        subnets: { subnetType: SubnetType.PRIVATE_ISOLATED },
      });
    }
    const apiTask = new FargateTaskDefinition(this, 'RecoveryApiTask', {
      family: `${prefix}-api`, cpu: 1024, memoryLimitMiB: 4096,
      executionRole: apiExecutionRole, taskRole: apiTaskRole,
    });
    apiTask.addContainer('api', {
      containerName: 'api',
      image: ContainerImage.fromRegistry(`${apiRepository.repositoryUri}@sha256:${props.recoveryApiImageDigest}`),
      environment: {
        ASPNETCORE_URLS: 'http://+:8080',
        Ingest__Database__Host: database.dbInstanceEndpointAddress,
        Ingest__Database__Port: database.dbInstanceEndpointPort,
        Ingest__Database__Name: 'platform_recovery',
        Ingest__Bucket: liveBucket.bucketName,
        Ingest__KmsKeyId: encryptionKey.keyArn,
        Authentication__CognitoAuthority: ownerPoolIssuer,
        Authentication__CognitoClientId: poolClient.userPoolClientId,
        Recovery__Enabled: 'true',
        Recovery__RecoveryOnly: 'true',
        Recovery__LibraryId: props.recoveryLibraryId,
        ...(props.recoveryOwnerId ? { Recovery__OwnerId: props.recoveryOwnerId } : {}),
        Recovery__ApiBuildDigest: props.recoveryApiBuildDigest,
        Recovery__IngestWorkerBuildDigest: props.recoveryIngestWorkerBuildDigest,
        Recovery__RecoveryWorkerBuildDigest: props.recoveryImageDigest,
        Recovery__RendererBuildDigest: props.recoveryRendererBuildDigest,
        Recovery__RecoveryTemplateDigest: templateDigest,
        Recovery__MigrationDigest: props.migrationDigest,
        Recovery__ConfigurationDigest: props.configurationDigest,
        Recovery__DatabaseRootCertificate: props.databaseRootCertificate,
        Recovery__DatabaseRootCertificateSha256: props.databaseRootCertificateSha256,
        Recovery__OwnerUserPoolId: pool.userPoolId,
        Recovery__OwnerUserPoolClientId: poolClient.userPoolClientId,
        Recovery__OwnerUserPoolIssuer: ownerPoolIssuer,
        Recovery__FenceTableName: fenceTable.tableName,
        Recovery__IntentTableName: intentTable.tableName,
        Recovery__OwnerApprovalKeyId: ownerApprovalKey.keyArn,
        Recovery__ObservationTrust__KeyId: evidenceSigningKey.keyArn,
        Recovery__ObservationTrust__PublicKeySpkiBase64: props.recoverySigningPublicKeySpkiBase64,
        Recovery__ObservationTrust__PublicKeySha256: props.recoverySigningPublicKeySha256,
        Recovery__OwnerApprovalTrust__KeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__PublicKeySpkiBase64: props.ownerApprovalPublicKeySpkiBase64,
        Recovery__OwnerApprovalTrust__PublicKeySha256: props.ownerApprovalPublicKeySha256,
      },
      secrets: {
        Ingest__Database__Username: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'username'),
        Ingest__Database__Password: EcsSecret.fromSecretsManager(runtimeDatabaseSecret, 'password'),
      },
      portMappings: [{ containerPort: 8080 }],
      logging: LogDrivers.awsLogs({ logGroup: auditLogs, streamPrefix: 'api' }),
    });
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['ecs:RunTask'], resources: [apiTask.taskDefinitionArn],
      conditions: {
        ArnEquals: { 'ecs:cluster': cluster.clusterArn },
        'ForAllValues:StringEquals': { 'ecs:subnet': vpc.selectSubnets({ subnetType: SubnetType.PRIVATE_ISOLATED }).subnetIds },
        StringEquals: { 'ecs:auto-assign-public-ip': 'DISABLED', 'ecs:enable-execute-command': 'false' },
      },
    }));
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['ecs:DescribeTasks', 'ecs:StopTask'], resources: ['*'],
      conditions: { ArnEquals: { 'ecs:cluster': cluster.clusterArn } },
    }));
    recoveryOnlyRole.addToPrincipalPolicy(new aws_iam.PolicyStatement({
      actions: ['iam:PassRole'], resources: [apiExecutionRole.roleArn, apiTaskRole.roleArn],
      conditions: { StringEquals: { 'iam:PassedToService': 'ecs-tasks.amazonaws.com' } },
    }));
    drillContainer.addEnvironment('Recovery__ApiTaskDefinitionArn', apiTask.taskDefinitionArn);
    drillContainer.addEnvironment('Recovery__ApiClusterArn', cluster.clusterArn);
    drillContainer.addEnvironment('Recovery__ApiSecurityGroupId', apiSecurityGroup.securityGroupId);
    drillContainer.addEnvironment('Recovery__RunnerSecurityGroupId', recoveryTaskSecurityGroup.securityGroupId);
    drillContainer.addEnvironment('Recovery__RunnerSubnetIds', vpc.selectSubnets({ subnetType: SubnetType.PRIVATE_ISOLATED }).subnetIds.join(','));
    drillContainer.addEnvironment('Recovery__TargetBucket', liveBucket.bucketName);
    drillContainer.addEnvironment('Recovery__OwnerUserPoolId', pool.userPoolId);
    drillContainer.addEnvironment('Recovery__OwnerUserPoolClientId', poolClient.userPoolClientId);
    drillContainer.addEnvironment('Recovery__OwnerUserPoolIssuer', ownerPoolIssuer);

    const verifierSecurityGroup = new SecurityGroup(this, 'RecoveryVerifierSecurityGroup', { vpc, allowAllOutbound: true });
    databaseSecurityGroup.addIngressRule(verifierSecurityGroup, Port.tcp(5432), 'Independent verifier may reconstruct isolated snapshots.');
    isolatedEndpointSecurityGroup.addIngressRule(verifierSecurityGroup, Port.tcp(443), 'Verifier may use pinned regional AWS endpoints.');
    const verifierTask = new FargateTaskDefinition(this, 'RecoveryVerifierTask', {
      family: `${prefix}-verify`, cpu: 1024, memoryLimitMiB: 4096,
      ephemeralStorageGiB: props.workerEphemeralStorageGiB, executionRole: recoveryExecutionRole, taskRole: verifierRole,
    });
    verifierTask.addContainer('recovery-worker', {
      image: ContainerImage.fromRegistry(`${recoveryWorkerRepository.repositoryUri}@sha256:${props.recoveryImageDigest}`),
      command: ['verify'],
      environment: {
        ...runtimePins, Recovery__Environment: props.environmentName, Recovery__WorkerId: 'verify',
        Recovery__OwnerId: props.recoveryOwnerId ?? 'offline-owner-not-qualified', Recovery__LibraryId: props.recoveryLibraryId,
        Recovery__SourceAccountId: props.primaryAccountId, Recovery__SourceRegion: props.primaryRegion,
        Recovery__DestinationAccountId: props.recoveryAccountId, Recovery__DestinationRegion: this.region,
        Recovery__SourceBucket: props.primaryIngestBucketName, Recovery__DestinationBucket: bucket.bucketName,
        Recovery__EvidenceBucket: evidenceBucket.bucketName, Recovery__DestinationKmsKeyId: encryptionKey.keyArn,
        Recovery__EvidenceKmsKeyId: evidenceEncryptionKey.keyArn, Recovery__ControlTableName: controlTable.tableName,
        Recovery__VerificationTableName: verificationTable.tableName,
        Recovery__DatabaseSecretArn: database.secret.secretArn, Recovery__DatabaseRootCertificate: props.databaseRootCertificate,
        Recovery__Database__Host: database.dbInstanceEndpointAddress, Recovery__Database__Port: database.dbInstanceEndpointPort,
        Recovery__Database__Name: 'platform_recovery', Recovery__RecoveryOnly: 'true',
        Recovery__SigningKeyId: evidenceSigningKey.keyArn, Recovery__SigningPublicKeySha256: props.recoverySigningPublicKeySha256,
        Recovery__OwnerApprovalKeyId: ownerApprovalKey.keyArn, Recovery__OwnerApprovalTrust__KeyId: ownerApprovalKey.keyArn,
        Recovery__OwnerApprovalTrust__PublicKeySpkiBase64: props.ownerApprovalPublicKeySpkiBase64,
        Recovery__OwnerApprovalTrust__PublicKeySha256: props.ownerApprovalPublicKeySha256,
        Recovery__OwnerUserPoolId: pool.userPoolId, Recovery__OwnerUserPoolClientId: poolClient.userPoolClientId,
        Recovery__OwnerUserPoolIssuer: ownerPoolIssuer,
        Recovery__ObservationQueueArn: props.primaryObservationQueueArn, Recovery__ObservationQueueUrl: props.primaryObservationQueueUrl,
      },
      logging: LogDrivers.awsLogs({ logGroup: auditLogs, streamPrefix: 'verify' }),
    });
    if (props.schedulesEnabled) {
      new Rule(this, 'RecoveryVerifierSchedule', {
        schedule: Schedule.rate(Duration.minutes(15)),
        targets: [new EcsTask({ cluster, taskDefinition: verifierTask, securityGroups: [verifierSecurityGroup],
          subnetSelection: { subnetType: SubnetType.PRIVATE_WITH_EGRESS }, assignPublicIp: false })],
      });
    }

    const githubProvider = new OpenIdConnectProvider(this, 'RecoveryGitHubOidcProvider', {
      url: 'https://token.actions.githubusercontent.com',
      clientIds: ['sts.amazonaws.com'],
      removalPolicy: RemovalPolicy.RETAIN,
    });
    const drillOidcRole = new aws_iam.Role(this, 'RecoveryDrillOidcRole', {
      roleName: `${prefix}-github-drill`,
      maxSessionDuration: Duration.hours(6),
      assumedBy: new aws_iam.OpenIdConnectPrincipal(githubProvider).withConditions({
        StringEquals: {
          'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
          'token.actions.githubusercontent.com:sub': props.githubOidcSubject,
        },
      }),
      description: 'Protected GitHub recovery drill launcher; no primary or content-store permissions.',
    });
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['ecs:RunTask'],
      resources: [drillTask.taskDefinitionArn],
      conditions: {
        ArnEquals: { 'ecs:cluster': cluster.clusterArn },
        'ForAllValues:StringEquals': { 'ecs:subnet': vpc.selectSubnets({ subnetType: SubnetType.PRIVATE_ISOLATED }).subnetIds },
        StringEquals: { 'ecs:auto-assign-public-ip': 'DISABLED', 'ecs:enable-execute-command': 'false' },
      },
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['iam:PassRole'],
      resources: [recoveryOnlyRole.roleArn, recoveryExecutionRole.roleArn],
      conditions: { StringEquals: { 'iam:PassedToService': 'ecs-tasks.amazonaws.com' } },
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['ecs:DescribeTasks'], resources: ['*'], conditions: { ArnEquals: { 'ecs:cluster': cluster.clusterArn } },
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['ecs:DescribeTaskDefinition'], resources: ['*'],
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['ec2:DescribeSubnets', 'ec2:DescribeRouteTables', 'ec2:DescribeSecurityGroups'],
      resources: ['*'],
      conditions: { StringEquals: { 'aws:RequestedRegion': this.region } },
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['dynamodb:GetItem'],
      resources: [controlTable.tableArn],
      conditions: { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['RUN#*'] } },
    }));
    drillOidcRole.addToPolicy(new aws_iam.PolicyStatement({
      actions: ['s3:GetObject', 's3:GetObjectVersion'],
      resources: [
        evidenceBucket.arnForObjects('evidence/drills/*/recovery-qualification.v1.json'),
        evidenceBucket.arnForObjects('evidence/drills/*/recovery-audit-checkpoint.v1.json'),
      ],
    }));
    evidenceEncryptionKey.grantDecrypt(drillOidcRole);

    const copyDimensions = { Environment: props.environmentName, WorkerId: 'copy' };
    const verifierDimensions = { Environment: props.environmentName, WorkerId: 'verify' };
    const verifiedAge = new Metric({ namespace: 'Platform/Recovery', metricName: 'VerifiedPointAgeSeconds', statistic: 'Maximum', period: Duration.minutes(5), dimensionsMap: verifierDimensions });
    const overdueAlarm = new Alarm(this, 'RecoveryPointOverdueAlarm', {
      metric: verifiedAge,
      threshold: 2700,
      evaluationPeriods: 1,
      comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: TreatMissingData.BREACHING,
      alarmDescription: `No fully verified recovery point within 45 minutes. Notify within 15 minutes. Runbook: docs/runbooks/library-recovery.md`,
    });
    overdueAlarm.addAlarmAction(new SnsAction(alerts));
    const heartbeatAlarm = new Alarm(this, 'RecoveryWorkerHeartbeatAlarm', {
      metric: new Metric({ namespace: 'Platform/Recovery', metricName: 'RecoveryWorkerHeartbeat', statistic: 'Sum', period: Duration.minutes(5), dimensionsMap: copyDimensions }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: ComparisonOperator.LESS_THAN_THRESHOLD,
      treatMissingData: TreatMissingData.BREACHING,
      alarmDescription: 'No successful recovery copy or freshness-check invocation reported in five minutes. Notify within 15 minutes. Runbook: docs/runbooks/library-recovery.md',
    });
    heartbeatAlarm.addAlarmAction(new SnsAction(alerts));
    const verifierHeartbeatAlarm = new Alarm(this, 'RecoveryVerifierHeartbeatAlarm', {
      metric: new Metric({ namespace: 'Platform/Recovery', metricName: 'RecoveryWorkerHeartbeat', statistic: 'Sum', period: Duration.minutes(5), dimensionsMap: verifierDimensions }),
      threshold: 1, evaluationPeriods: 3,
      comparisonOperator: ComparisonOperator.LESS_THAN_THRESHOLD,
      treatMissingData: TreatMissingData.BREACHING,
      alarmDescription: 'No successful independent verifier invocation within fifteen minutes. Runbook: docs/runbooks/library-recovery.md',
    });
    verifierHeartbeatAlarm.addAlarmAction(new SnsAction(alerts));
    for (const [id, metricName] of [['RecoveryCopyFailureAlarm', 'CopyFailures'], ['RecoveryVerificationFailureAlarm', 'VerificationFailures']] as const) {
      const alarm = new Alarm(this, id, {
        metric: new Metric({ namespace: 'Platform/Recovery', metricName, statistic: 'Sum', period: Duration.minutes(5), dimensionsMap: metricName === 'VerificationFailures' ? verifierDimensions : copyDimensions }),
        threshold: 1,
        evaluationPeriods: 1,
        comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: TreatMissingData.NOT_BREACHING,
        alarmDescription: `Recovery ${metricName.toLowerCase()} detected. Runbook: docs/runbooks/library-recovery.md`,
      });
      alarm.addAlarmAction(new SnsAction(alerts));
    }

    new CfnOutput(this, 'RecoveryBucketName', { value: bucket.bucketName });
    new CfnOutput(this, 'RecoveryEvidenceBucketName', { value: evidenceBucket.bucketName });
    new CfnOutput(this, 'RecoveryKmsKeyArn', { value: encryptionKey.keyArn });
    new CfnOutput(this, 'RecoveryEvidenceKmsKeyArn', { value: evidenceEncryptionKey.keyArn });
    new CfnOutput(this, 'RecoveryEvidenceSigningKeyArn', { value: evidenceSigningKey.keyArn });
    new CfnOutput(this, 'RecoveryOwnerApprovalKeyArn', { value: ownerApprovalKey.keyArn });
    new CfnOutput(this, 'RecoveryControlTableName', { value: controlTable.tableName });
    new CfnOutput(this, 'RecoveryFenceTableName', { value: fenceTable.tableName });
    new CfnOutput(this, 'RecoveryIntentTableName', { value: intentTable.tableName });
    new CfnOutput(this, 'RecoveryWorkerRoleArn', { value: copyRole.roleArn });
    new CfnOutput(this, 'RecoveryVerifyRoleArn', { value: verifierRole.roleArn });
    new CfnOutput(this, 'RecoveryDrillTaskRoleArn', { value: recoveryOnlyRole.roleArn });
    new CfnOutput(this, 'RecoveryVerificationTableName', { value: verificationTable.tableName });
    new CfnOutput(this, 'RecoveryVerifierTaskDefinitionArn', { value: verifierTask.taskDefinitionArn });
    new CfnOutput(this, 'RecoveryWorkerRepositoryName', { value: recoveryWorkerRepository.repositoryName });
    new CfnOutput(this, 'RecoveryApiRepositoryName', { value: apiRepository.repositoryName });
    new CfnOutput(this, 'RecoveryIngestWorkerRepositoryName', { value: ingestWorkerRepository.repositoryName });
    new CfnOutput(this, 'RecoveryRendererRepositoryName', { value: rendererRepository.repositoryName });
    new CfnOutput(this, 'RecoveryOwnerUserPoolId', { value: pool.userPoolId });
    new CfnOutput(this, 'RecoveryOwnerUserPoolClientId', { value: poolClient.userPoolClientId });
    new CfnOutput(this, 'RecoveryDatabaseEndpoint', { value: database.dbInstanceEndpointAddress });
    new CfnOutput(this, 'RecoveryDatabaseSecretArn', { value: database.secret.secretArn });
    new CfnOutput(this, 'RecoveryRuntimeDatabaseSecretArn', { value: runtimeDatabaseSecret.secretArn });
    new CfnOutput(this, 'RecoveryDrillRoleArn', { value: drillOidcRole.roleArn });
    new CfnOutput(this, 'RecoveryPurgeExecutorRoleArn', { value: purgeExecutorRole.roleArn });
    new CfnOutput(this, 'RecoveryDrillTaskDefinitionArn', { value: drillTask.taskDefinitionArn });
    new CfnOutput(this, 'RecoveryIndexBootstrapTaskDefinitionArn', { value: bootstrapTask.taskDefinitionArn });
    new CfnOutput(this, 'RecoveryIndexBootstrapTaskRoleArn', { value: bootstrapRole.roleArn });
    new CfnOutput(this, 'RecoveryApiTaskDefinitionArn', { value: apiTask.taskDefinitionArn });
    new CfnOutput(this, 'RecoveryApiSecurityGroupId', { value: apiSecurityGroup.securityGroupId });
    new CfnOutput(this, 'RecoveryTargetBucketName', { value: liveBucket.bucketName });
    new CfnOutput(this, 'RecoveryClusterArn', { value: cluster.clusterArn });
    new CfnOutput(this, 'RecoveryDrillSecurityGroupId', { value: recoveryTaskSecurityGroup.securityGroupId });
    new CfnOutput(this, 'RecoveryCopySecurityGroupId', { value: copySecurityGroup.securityGroupId });
    new CfnOutput(this, 'RecoveryWorkerSubnetIds', { value: vpc.selectSubnets({ subnetType: SubnetType.PRIVATE_WITH_EGRESS }).subnetIds.join(',') });
    new CfnOutput(this, 'RecoveryRunnerSubnetIds', { value: vpc.selectSubnets({ subnetType: SubnetType.PRIVATE_ISOLATED }).subnetIds.join(',') });
    new CfnOutput(this, 'RecoveryWorkerEphemeralStorageGiB', { value: String(props.workerEphemeralStorageGiB) });
    new CfnOutput(this, 'RecoveryRegion', { value: this.region });
  }
}

function addRecoveryDataPolicy(
  role: aws_iam.IRole,
  bucket: Bucket,
  table: Table,
  readPrefixes: string[],
  writePrefixes: string[],
  tableActions: string[],
): void {
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:ListBucket', 's3:ListBucketVersions'],
    resources: [bucket.bucketArn],
    conditions: { StringLike: { 's3:prefix': readPrefixes } },
  }));
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:GetObject', 's3:GetObjectVersion', 's3:GetObjectAttributes'],
    resources: readPrefixes.map(prefix => bucket.arnForObjects(prefix)),
  }));
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:PutObject', 's3:AbortMultipartUpload', 's3:ListMultipartUploadParts'],
    resources: writePrefixes.map(prefix => bucket.arnForObjects(prefix)),
  }));
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: tableActions.map(action => `dynamodb:${action}`),
    resources: [table.tableArn],
  }));
}

function addRecoveryEvidencePolicy(role: aws_iam.IRole, bucket: Bucket, prefixes: string[]): void {
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:ListBucket', 's3:ListBucketVersions'],
    resources: [bucket.bucketArn],
    conditions: { StringLike: { 's3:prefix': prefixes } },
  }));
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:GetObject', 's3:GetObjectVersion', 's3:GetObjectAttributes'],
    resources: prefixes.map(prefix => bucket.arnForObjects(prefix)),
  }));
  role.addToPrincipalPolicy(new aws_iam.PolicyStatement({
    actions: ['s3:PutObject', 's3:AbortMultipartUpload', 's3:ListMultipartUploadParts'],
    resources: prefixes.map(prefix => bucket.arnForObjects(prefix)),
  }));
}

function isRecoveryEnvironmentSubject(subject: string, repository: string): boolean {
  const match = /^repo:([^/:@]+)(?:@\d+)?\/([^/:@]+)(?:@\d+)?:environment:recovery-drill$/.exec(subject);
  if (!match) return false;
  return `${match[1]}/${match[2]}`.toLowerCase() === repository.toLowerCase();
}
