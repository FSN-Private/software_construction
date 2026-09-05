import { Duration, RemovalPolicy } from 'aws-cdk-lib';
import { CfnUserPoolGroup, CfnUserPoolUser, CfnUserPoolUserToGroupAttachment, UserPool, UserPoolClient } from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';

export interface OwnerIdentityProps { readonly ownerEmail: string; }

export function createOwnerIdentity(scope: Construct, props: OwnerIdentityProps) {
  const userPool = new UserPool(scope, 'OwnerUserPool', { selfSignUpEnabled: false, signInAliases: { email: true }, passwordPolicy: { minLength: 14, requireDigits: true, requireLowercase: true, requireUppercase: true, requireSymbols: true, tempPasswordValidity: Duration.days(7) }, removalPolicy: RemovalPolicy.RETAIN });
  const client = new UserPoolClient(scope, 'OwnerWebClient', { userPool, authFlows: { userSrp: true }, preventUserExistenceErrors: true });
  const owner = new CfnUserPoolUser(scope, 'ProvisionedOwner', { userPoolId: userPool.userPoolId, username: props.ownerEmail, userAttributes: [{ name: 'email', value: props.ownerEmail }, { name: 'email_verified', value: 'true' }] });
  new CfnUserPoolGroup(scope, 'OwnerGroup', { groupName: 'owner', userPoolId: userPool.userPoolId });
  new CfnUserPoolUserToGroupAttachment(scope, 'ProvisionedOwnerGroupAttachment', { groupName: 'owner', username: props.ownerEmail, userPoolId: userPool.userPoolId }).addDependency(owner);
  return { userPool, client };
}
