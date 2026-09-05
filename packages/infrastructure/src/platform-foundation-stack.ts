import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps, aws_iam } from 'aws-cdk-lib';
import { CfnBudget } from 'aws-cdk-lib/aws-budgets';
import { Key } from 'aws-cdk-lib/aws-kms';
import { LogGroup, RetentionDays } from 'aws-cdk-lib/aws-logs';
import { Alarm, ComparisonOperator, Dashboard, GraphWidget, Metric, TreatMissingData } from 'aws-cdk-lib/aws-cloudwatch';
import { SnsAction } from 'aws-cdk-lib/aws-cloudwatch-actions';
import { BlockPublicAccess, Bucket, BucketEncryption } from 'aws-cdk-lib/aws-s3';
import { SubnetType, Vpc } from 'aws-cdk-lib/aws-ec2';
import { Topic } from 'aws-cdk-lib/aws-sns';
import { EmailSubscription } from 'aws-cdk-lib/aws-sns-subscriptions';
import { Construct } from 'constructs';
import { createOwnerIdentity } from './identity/owner-identity.js';

export interface PlatformFoundationStackProps extends StackProps {
  readonly environmentName: string;
  readonly monthlyBudgetUsd: number;
  readonly ownerEmail: string;
  readonly alertEmail: string;
}

export class PlatformFoundationStack extends Stack {
  public constructor(scope: Construct, id: string, props: PlatformFoundationStackProps) {
    super(scope, id, props);

    if (!Number.isFinite(props.monthlyBudgetUsd) || props.monthlyBudgetUsd <= 0) {
      throw new Error('monthlyBudgetUsd must be a positive number.');
    }

    const vpc = new Vpc(this, 'PlatformVpc', {
      maxAzs: 2,
      natGateways: 1,
      subnetConfiguration: [
        { name: 'edge', subnetType: SubnetType.PUBLIC },
        { name: 'application', subnetType: SubnetType.PRIVATE_WITH_EGRESS },
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
    const identity = createOwnerIdentity(this, { ownerEmail: props.ownerEmail });
    const alerts = new Topic(this, 'OwnerAlerts', { masterKey: encryptionKey });
    encryptionKey.addToResourcePolicy(new aws_iam.PolicyStatement({
      principals: [new aws_iam.ServicePrincipal('cloudwatch.amazonaws.com'), new aws_iam.ServicePrincipal('sns.amazonaws.com')],
      actions: ['kms:Decrypt', 'kms:GenerateDataKey*'],
      resources: ['*'],
      conditions: { StringEquals: { 'aws:SourceAccount': this.account } },
    }));
    alerts.addSubscription(new EmailSubscription(props.alertEmail));
    const availability = new Metric({ namespace: 'Platform/Service', metricName: 'Availability', statistic: 'Minimum', period: Duration.minutes(5) });
    const availabilityAlarm = new Alarm(this, 'ServiceAvailabilityAlarm', { metric: availability, threshold: 1, evaluationPeriods: 1, comparisonOperator: ComparisonOperator.LESS_THAN_THRESHOLD, treatMissingData: TreatMissingData.BREACHING });
    availabilityAlarm.addAlarmAction(new SnsAction(alerts));
    const authenticationAlarm = new Alarm(this, 'AuthenticationAnomalyAlarm', { metric: new Metric({ namespace: 'Platform/Auth', metricName: 'AuthenticationFailures', statistic: 'Sum', period: Duration.minutes(5) }), threshold: 5, evaluationPeriods: 1 });
    authenticationAlarm.addAlarmAction(new SnsAction(alerts));
    for (const [id, namespace, metricName, threshold] of [
      ['DeploymentFailureAlarm', 'Platform/Delivery', 'DeploymentFailures', 1],
      ['CiFailureAlarm', 'Platform/Delivery', 'CiFailures', 1],
      ['BackupAgePlaceholderAlarm', 'Platform/Data', 'BackupAgeHours', 26],
    ] as const) {
      const alarm = new Alarm(this, id, { metric: new Metric({ namespace, metricName, statistic: 'Maximum', period: Duration.minutes(5) }), threshold, evaluationPeriods: 1, comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, treatMissingData: TreatMissingData.BREACHING });
      alarm.addAlarmAction(new SnsAction(alerts));
    }
    new Dashboard(this, 'PlatformOperationsDashboard', { dashboardName: `platform-${props.environmentName}-operations`, widgets: [
      [new GraphWidget({ title: 'Service availability', left: [availability] })],
      [new GraphWidget({ title: 'Authentication failures', left: [new Metric({ namespace: 'Platform/Auth', metricName: 'AuthenticationFailures' })] })],
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
  }
}
