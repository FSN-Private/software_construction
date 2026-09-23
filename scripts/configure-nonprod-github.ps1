param(
    [Parameter(Mandatory = $true)] [string] $OutputsPath,
    [string] $StackName = 'PlatformFoundation-nonprod'
)

$ErrorActionPreference = 'Stop'
$outputs = (Get-Content -Raw -LiteralPath $OutputsPath | ConvertFrom-Json).$StackName
if ($null -eq $outputs) { throw "CDK outputs do not contain stack $StackName." }

$variables = [ordered]@{
    AWS_REGION = $outputs.AwsRegion
    AWS_NONPROD_ECR_REGISTRY = $outputs.EcrRegistry
    AWS_NONPROD_API_REPOSITORY = $outputs.ApiRepositoryName
    AWS_NONPROD_WORKER_REPOSITORY = $outputs.WorkerRepositoryName
    AWS_NONPROD_RENDERER_REPOSITORY = $outputs.RendererRepositoryName
    AWS_NONPROD_ECS_CLUSTER = $outputs.EcsClusterName
    AWS_NONPROD_ECS_SERVICE = $outputs.ApiServiceName
    AWS_NONPROD_ECS_TASK_DEFINITION = $outputs.ApiTaskDefinitionFamily
    AWS_NONPROD_ECS_API_CONTAINER = $outputs.ApiContainerName
    AWS_NONPROD_WORKER_SERVICE = $outputs.WorkerServiceName
    AWS_NONPROD_DATABASE_INSTANCE = $outputs.DatabaseInstanceIdentifier
    AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM = $outputs.VersionControlHeartbeatAlarmName
    AWS_NONPROD_WORKER_TASK_DEFINITION = $outputs.WorkerTaskDefinitionFamily
    AWS_NONPROD_ECS_WORKER_CONTAINER = $outputs.WorkerContainerName
    AWS_NONPROD_MIGRATION_TASK_DEFINITION = $outputs.MigrationTaskDefinitionFamily
    AWS_NONPROD_ECS_MIGRATION_CONTAINER = $outputs.MigrationContainerName
    AWS_NONPROD_HEALTH_URL = $outputs.HealthUrl
    AWS_NONPROD_COGNITO_AUTHORITY = $outputs.CognitoAuthority
    AWS_NONPROD_COGNITO_CLIENT_ID = $outputs.OwnerUserPoolClientId
}

foreach ($entry in $variables.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value)) { throw "Missing CDK output for $($entry.Key)." }
    gh variable set $entry.Key --env nonprod --body $entry.Value
    if ($LASTEXITCODE -ne 0) { throw "Failed to set $($entry.Key)." }
}

foreach ($name in 'AWS_NONPROD_COGNITO_OWNER_USERNAME', 'AWS_NONPROD_COGNITO_OWNER_PASSWORD') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Set $name in the process environment before running this script." }
    $value | gh secret set $name --env nonprod
    if ($LASTEXITCODE -ne 0) { throw "Failed to set $name." }
}
