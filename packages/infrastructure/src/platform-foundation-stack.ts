import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import { CfnBudget } from 'aws-cdk-lib/aws-budgets';
import { Key } from 'aws-cdk-lib/aws-kms';
import { LogGroup, RetentionDays } from 'aws-cdk-lib/aws-logs';
import { BlockPublicAccess, Bucket, BucketEncryption } from 'aws-cdk-lib/aws-s3';
import { SubnetType, Vpc } from 'aws-cdk-lib/aws-ec2';
import { Construct } from 'constructs';

export interface PlatformFoundationStackProps extends StackProps {
  readonly environmentName: string;
  readonly monthlyBudgetUsd: number;
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

    new CfnBudget(this, 'MonthlyCostBudget', {
      budget: {
        budgetName: `platform-${props.environmentName}-monthly-cost`,
        budgetLimit: { amount: props.monthlyBudgetUsd, unit: 'USD' },
        timeUnit: 'MONTHLY',
        budgetType: 'COST',
      },
    });

    new CfnOutput(this, 'VpcId', { value: vpc.vpcId });
    new CfnOutput(this, 'EvidenceBucketName', { value: evidenceBucket.bucketName });
    new CfnOutput(this, 'TelemetryLogGroupName', { value: telemetry.logGroupName });
  }
}
