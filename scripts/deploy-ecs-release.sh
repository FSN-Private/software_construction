#!/usr/bin/env bash
set -Eeuo pipefail

for NAME in RELEASE_DIR DEPLOYMENT_RECORD SOURCE_REVISION RELEASE_RUN_ID AWS_REGION ECR_REGISTRY API_REPOSITORY WORKER_REPOSITORY ECS_CLUSTER ECS_SERVICE ECS_TASK_DEFINITION ECS_API_CONTAINER WORKER_SERVICE WORKER_TASK_DEFINITION ECS_WORKER_CONTAINER HEALTH_URL COGNITO_AUTHORITY COGNITO_CLIENT_ID COGNITO_OWNER_USERNAME COGNITO_OWNER_PASSWORD; do
  test -n "${!NAME:-}" || { echo "Missing required deployment setting: $NAME"; exit 1; }
done

docker load --input "$RELEASE_DIR/api.tar"
docker load --input "$RELEASE_DIR/ingest-worker.tar"
docker tag "platform-api:$SOURCE_REVISION" "$ECR_REGISTRY/$API_REPOSITORY:$SOURCE_REVISION"
docker tag "platform-ingest-worker:$SOURCE_REVISION" "$ECR_REGISTRY/$WORKER_REPOSITORY:$SOURCE_REVISION"
aws ecr get-login-password --region "$AWS_REGION" | docker login --username AWS --password-stdin "$ECR_REGISTRY"
docker push "$ECR_REGISTRY/$API_REPOSITORY:$SOURCE_REVISION"
docker push "$ECR_REGISTRY/$WORKER_REPOSITORY:$SOURCE_REVISION"
API_IMAGE="$(docker inspect --format='{{index .RepoDigests 0}}' "$ECR_REGISTRY/$API_REPOSITORY:$SOURCE_REVISION")"
WORKER_IMAGE="$(docker inspect --format='{{index .RepoDigests 0}}' "$ECR_REGISTRY/$WORKER_REPOSITORY:$SOURCE_REVISION")"
test -n "$API_IMAGE" && test -n "$WORKER_IMAGE"

MANIFEST="$RELEASE_DIR/release-manifest.json"
MANIFEST_DIGEST="sha256:$(sha256sum "$MANIFEST" | awk '{print $1}')"
API_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "api.tar") | .digest' "$MANIFEST")"
WORKER_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "ingest-worker.tar") | .digest' "$MANIFEST")"
RELEASE_ID="$(jq -er .releaseId "$MANIFEST")"
API_TASK="$(aws ecs describe-task-definition --task-definition "$ECS_TASK_DEFINITION" --query taskDefinition --output json)"
WORKER_TASK="$(aws ecs describe-task-definition --task-definition "$WORKER_TASK_DEFINITION" --query taskDefinition --output json)"

for SETTING in ConnectionStrings__Ingest Ingest__Bucket Ingest__KmsKeyId; do
  printf '%s' "$API_TASK" | jq -e --arg container "$ECS_API_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
  printf '%s' "$WORKER_TASK" | jq -e --arg container "$ECS_WORKER_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
done

API_UPDATED="$(printf '%s' "$API_TASK" | jq --arg image "$API_IMAGE" --arg name "$ECS_API_CONTAINER" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "Authentication__CognitoAuthority" and .name != "Authentication__CognitoClientId" and .name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"Authentication__CognitoAuthority",value:$authority},{name:"Authentication__CognitoClientId",value:$client},{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
WORKER_UPDATED="$(printf '%s' "$WORKER_TASK" | jq --arg image "$WORKER_IMAGE" --arg name "$ECS_WORKER_CONTAINER" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
API_TASK_ARN="$(printf '%s' "$API_UPDATED" | aws ecs register-task-definition --cli-input-json file:///dev/stdin --query taskDefinition.taskDefinitionArn --output text)"
WORKER_TASK_ARN="$(printf '%s' "$WORKER_UPDATED" | aws ecs register-task-definition --cli-input-json file:///dev/stdin --query taskDefinition.taskDefinitionArn --output text)"
PREVIOUS_API_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --query 'services[0].taskDefinition' --output text)"
PREVIOUS_WORKER_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].taskDefinition' --output text)"
NETWORK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].networkConfiguration' --output json)"
OVERRIDES="$(jq -cn --arg name "$ECS_WORKER_CONTAINER" '{containerOverrides:[{name:$name,command:["--migrate"]}]}')"
MIGRATION_TASK="$(aws ecs run-task --cluster "$ECS_CLUSTER" --launch-type FARGATE --task-definition "$WORKER_TASK_ARN" --network-configuration "$NETWORK" --overrides "$OVERRIDES" --query 'tasks[0].taskArn' --output text)"
test -n "$MIGRATION_TASK" && test "$MIGRATION_TASK" != None
timeout 600 aws ecs wait tasks-stopped --cluster "$ECS_CLUSTER" --tasks "$MIGRATION_TASK"
test "$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "$MIGRATION_TASK" --query "tasks[0].containers[?name=='$ECS_WORKER_CONTAINER'].exitCode | [0]" --output text)" = 0

jq -n --arg tag "${RELEASE_TAG:-}" --arg source "$SOURCE_REVISION" --arg releaseRun "$RELEASE_RUN_ID" --arg manifest "$MANIFEST_DIGEST" --arg apiArtifact "$API_TAR_DIGEST" --arg workerArtifact "$WORKER_TAR_DIGEST" --arg apiImage "$API_IMAGE" --arg workerImage "$WORKER_IMAGE" --arg apiTask "$API_TASK_ARN" --arg workerTask "$WORKER_TASK_ARN" --arg previousApiTask "$PREVIOUS_API_TASK" --arg previousWorkerTask "$PREVIOUS_WORKER_TASK" --arg migrationTask "$MIGRATION_TASK" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" '{sourceRevision:$source,releaseRunId:$releaseRun,manifestDigest:$manifest,releaseArtifactDigest:$apiArtifact,workerReleaseArtifactDigest:$workerArtifact,apiImage:$apiImage,workerImage:$workerImage,taskDefinition:$apiTask,workerTaskDefinition:$workerTask,previousTaskDefinition:$previousApiTask,previousWorkerTaskDefinition:$previousWorkerTask,migrationTask:$migrationTask,cognitoAuthority:$authority,cognitoClientId:$client} + (if $tag == "" then {} else {tag:$tag} end)' > "$DEPLOYMENT_RECORD"

ROLLBACK_REQUIRED=0
rollback() {
  STATUS=$?
  trap - ERR TERM
  if test "$ROLLBACK_REQUIRED" = 1; then
    set +e
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$PREVIOUS_API_TASK" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$PREVIOUS_WORKER_TASK" --force-new-deployment >/dev/null
    timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
  fi
  exit "$STATUS"
}
trap rollback ERR INT TERM

ROLLBACK_REQUIRED=1
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$WORKER_TASK_ARN" --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$API_TASK_ARN" --force-new-deployment --query service.taskDefinition --output text
timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 "$HEALTH_URL/live" >/dev/null
ID_TOKEN="$(aws cognito-idp initiate-auth --auth-flow USER_PASSWORD_AUTH --client-id "$COGNITO_CLIENT_ID" --auth-parameters "USERNAME=$COGNITO_OWNER_USERNAME,PASSWORD=$COGNITO_OWNER_PASSWORD" --query 'AuthenticationResult.IdToken' --output text)"
test -n "$ID_TOKEN" && test "$ID_TOKEN" != None
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/ready" >/dev/null
timeout 120 env INGEST_SMOKE_TOKEN="$ID_TOKEN" node scripts/smoke-ingest.mjs "$HEALTH_URL" > "$RELEASE_DIR/ingest-smoke.json"
jq --slurpfile smoke "$RELEASE_DIR/ingest-smoke.json" '. + {smoke:$smoke[0]}' "$DEPLOYMENT_RECORD" > "$DEPLOYMENT_RECORD.tmp"
mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"
ROLLBACK_REQUIRED=0
trap - ERR INT TERM
