import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import { Policy, PolicyStatement, Role, ServicePrincipal } from 'aws-cdk-lib/aws-iam';
import { Architecture, Code, Function, Runtime, Version } from 'aws-cdk-lib/aws-lambda';
import { LogGroup, RetentionDays } from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

export interface RegionalAcceptanceStackProps extends StackProps {
  readonly environmentName: string;
  readonly origin: string;
  readonly deploymentRoleArn: string;
  readonly bundlePath: string;
}

export class RegionalAcceptanceStack extends Stack {
  public constructor(scope: Construct, id: string, props: RegionalAcceptanceStackProps) {
    super(scope, id, props);
    if (props.environmentName !== 'nonprod' || props.env?.region !== 'ap-southeast-1' || !/^\d{12}$/.test(props.env?.account ?? ''))
      throw new Error('Regional acceptance is restricted to explicit nonprod/Singapore/account.');
    const origin = new URL(props.origin);
    if (origin.origin !== props.origin || origin.protocol !== 'https:' || !/^[a-z0-9]+\.execute-api\.ap-southeast-1\.amazonaws\.com$/.test(origin.hostname))
      throw new Error('Regional acceptance requires the configured public Singapore API origin.');
    if (props.deploymentRoleArn !== `arn:aws:iam::${props.env.account}:role/github-actions-nonprod-deploy`)
      throw new Error('Regional acceptance caller must be the existing same-account nonprod deploy role.');
    const codeSha256 = createHash('sha256').update(readFileSync(props.bundlePath)).digest('base64');
    const log = new LogGroup(this, 'ClientLogs', {
      logGroupName: '/platform/nonprod/regional-acceptance', retention: RetentionDays.ONE_WEEK,
      removalPolicy: RemovalPolicy.DESTROY,
    });
    const role = new Role(this, 'ClientRole', { assumedBy: new ServicePrincipal('lambda.amazonaws.com') });
    role.addToPolicy(new PolicyStatement({ actions: ['logs:CreateLogStream', 'logs:PutLogEvents'], resources: [`${log.logGroupArn}:*`] }));
    const client = new Function(this, 'Client', {
      functionName: 'platform-nonprod-acceptance', runtime: Runtime.NODEJS_24_X, architecture: Architecture.X86_64,
      handler: 'handler.handler', code: Code.fromAsset(props.bundlePath), memorySize: 512,
      timeout: Duration.seconds(300), reservedConcurrentExecutions: 1,
      environment: { TARGET_ORIGIN: props.origin }, role, logGroup: log,
    });
    const version = new Version(this, 'ClientVersion', { lambda: client, codeSha256, removalPolicy: RemovalPolicy.RETAIN });
    const caller = Role.fromRoleArn(this, 'ExistingDeployRole', props.deploymentRoleArn, { mutable: true });
    new Policy(this, 'ClientInvocation', {
      roles: [caller], statements: [new PolicyStatement({
        actions: ['lambda:InvokeFunction', 'lambda:GetFunctionConfiguration'], resources: [`${client.functionArn}:*`],
      })],
    });
    new CfnOutput(this, 'ClientVersionArn', { value: version.functionArn });
    new CfnOutput(this, 'ClientBundleCodeSha256', { value: codeSha256 });
  }
}
