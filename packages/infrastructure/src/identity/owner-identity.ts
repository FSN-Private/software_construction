import { Duration, RemovalPolicy } from 'aws-cdk-lib';
import { CfnUserPoolGroup, CfnUserPoolUser, CfnUserPoolUserToGroupAttachment, OAuthScope, UserPool, UserPoolClient } from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';

export interface OwnerIdentityProps { readonly ownerEmail: string; readonly webOrigin: string; readonly domainPrefix: string; }

export function createOwnerIdentity(scope: Construct, props: OwnerIdentityProps) {
  const userPool = new UserPool(scope, 'OwnerUserPool', { selfSignUpEnabled: false, signInAliases: { email: true }, passwordPolicy: { minLength: 14, requireDigits: true, requireLowercase: true, requireUppercase: true, requireSymbols: true, tempPasswordValidity: Duration.days(7) }, removalPolicy: RemovalPolicy.RETAIN });
  const client = new UserPoolClient(scope, 'OwnerWebClient', { userPool, authFlows: { userSrp: true, userPassword: true }, oAuth: { flows: { implicitCodeGrant: true }, scopes: [OAuthScope.OPENID], callbackUrls: [`${props.webOrigin}/`], logoutUrls: [`${props.webOrigin}/`] }, preventUserExistenceErrors: true });
  userPool.addDomain('OwnerHostedUi', { cognitoDomain: { domainPrefix: props.domainPrefix } });
  const owner = new CfnUserPoolUser(scope, 'ProvisionedOwner', { userPoolId: userPool.userPoolId, username: props.ownerEmail, userAttributes: [{ name: 'email', value: props.ownerEmail }, { name: 'email_verified', value: 'true' }] });
  const ownerGroup = new CfnUserPoolGroup(scope, 'OwnerGroup', { groupName: 'owner', userPoolId: userPool.userPoolId });
  const attachment = new CfnUserPoolUserToGroupAttachment(scope, 'ProvisionedOwnerGroupAttachment', { groupName: 'owner', username: props.ownerEmail, userPoolId: userPool.userPoolId });
  attachment.addDependency(owner);
  attachment.addDependency(ownerGroup);
  return { userPool, client };
}
