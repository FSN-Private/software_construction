import { readFileSync } from 'node:fs';

const template = JSON.parse(readFileSync('cdk.out/PlatformFoundation-nonprod.template.json', 'utf8'));
const resources = Object.values(template.Resources);
const requiredTypes = ['AWS::EC2::VPC', 'AWS::KMS::Key', 'AWS::S3::Bucket', 'AWS::Logs::LogGroup', 'AWS::Budgets::Budget', 'AWS::Cognito::UserPool', 'AWS::SNS::Topic', 'AWS::CloudWatch::Alarm', 'AWS::CloudWatch::Dashboard'];
for (const type of requiredTypes) {
  if (!resources.some((resource) => resource.Type === type)) {
    throw new Error(`Synthesized template is missing required resource ${type}.`);
  }
}
if (resources.filter((resource) => resource.Type === 'AWS::CloudWatch::Alarm').length < 5) throw new Error('Operations require service, deployment, authentication, CI, and backup-age alarms.');
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
