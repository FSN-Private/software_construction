#!/usr/bin/env bash
set -Eeuo pipefail

MISSING=()
for NAME in RELEASE_DIR DEPLOYMENT_RECORD SOURCE_REVISION RELEASE_RUN_ID AWS_REGION ECR_REGISTRY API_REPOSITORY WORKER_REPOSITORY RENDERER_REPOSITORY ECS_CLUSTER ECS_SERVICE ECS_TASK_DEFINITION ECS_API_CONTAINER WORKER_SERVICE WORKER_TASK_DEFINITION ECS_WORKER_CONTAINER RENDERER_SERVICE RENDERER_TASK_DEFINITION ECS_RENDERER_CONTAINER MIGRATION_TASK_DEFINITION ECS_MIGRATION_CONTAINER HEALTH_URL COGNITO_AUTHORITY COGNITO_CLIENT_ID COGNITO_OWNER_USERNAME COGNITO_OWNER_PASSWORD RENDERING_ACCEPTANCE_FIXTURE; do
  test -n "${!NAME:-}" || MISSING+=("$NAME")
done
test "${#MISSING[@]}" -eq 0 || { printf 'Missing required deployment setting: %s\n' "${MISSING[@]}"; exit 1; }

docker load --input "$RELEASE_DIR/api.tar"
docker load --input "$RELEASE_DIR/ingest-worker.tar"
docker load --input "$RELEASE_DIR/render-worker.tar"
aws ecr get-login-password --region "$AWS_REGION" | docker login --username AWS --password-stdin "$ECR_REGISTRY"
publish_immutable() {
  local source="$1"
  local repository="$2"
  local target="$ECR_REGISTRY/$repository:$SOURCE_REVISION"
  local digest
  if digest="$(aws ecr describe-images --repository-name "$repository" --image-ids "imageTag=$SOURCE_REVISION" --query 'imageDetails[0].imageDigest' --output text 2>/dev/null)"; then
    docker pull "$target" >/dev/null
    test "$(docker inspect --format='{{.Id}}' "$source")" = "$(docker inspect --format='{{.Id}}' "$target")" || { echo "Immutable ECR tag contains different image bytes: $target"; return 1; }
  else
    docker tag "$source" "$target"
    docker push "$target" >/dev/null
    digest="$(aws ecr describe-images --repository-name "$repository" --image-ids "imageTag=$SOURCE_REVISION" --query 'imageDetails[0].imageDigest' --output text)"
  fi
  test -n "$digest" && test "$digest" != None
  printf '%s@%s\n' "$ECR_REGISTRY/$repository" "$digest"
}
API_IMAGE="$(publish_immutable "platform-api:$SOURCE_REVISION" "$API_REPOSITORY")"
WORKER_IMAGE="$(publish_immutable "platform-ingest-worker:$SOURCE_REVISION" "$WORKER_REPOSITORY")"
RENDERER_IMAGE="$(publish_immutable "platform-render-worker:$SOURCE_REVISION" "$RENDERER_REPOSITORY")"
test -n "$API_IMAGE" && test -n "$WORKER_IMAGE" && test -n "$RENDERER_IMAGE"
RENDERER_DIGEST="${RENDERER_IMAGE##*@}"

MANIFEST="$RELEASE_DIR/release-manifest.json"
MANIFEST_DIGEST="sha256:$(sha256sum "$MANIFEST" | awk '{print $1}')"
API_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "api.tar") | .digest' "$MANIFEST")"
WORKER_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "ingest-worker.tar") | .digest' "$MANIFEST")"
RENDERER_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "render-worker.tar") | .digest' "$MANIFEST")"
RELEASE_ID="$(jq -er .releaseId "$MANIFEST")"
PROMOTION_EVIDENCE_ID="trusted-linux-${RELEASE_ID}-${RENDERER_DIGEST#sha256:}"
DARKTABLE_IMAGE="$RENDERER_IMAGE" bash tests/rendering/corpus/run-native.sh > "$RELEASE_DIR/rendering-corpus.txt"
API_TASK="$(aws ecs describe-task-definition --task-definition "$ECS_TASK_DEFINITION" --query taskDefinition --output json)"
WORKER_TASK="$(aws ecs describe-task-definition --task-definition "$WORKER_TASK_DEFINITION" --query taskDefinition --output json)"
RENDERER_TASK="$(aws ecs describe-task-definition --task-definition "$RENDERER_TASK_DEFINITION" --query taskDefinition --output json)"
MIGRATION_TASK_BASE="$(aws ecs describe-task-definition --task-definition "$MIGRATION_TASK_DEFINITION" --query taskDefinition --output json)"

for SETTING in Ingest__Database__Host Ingest__Database__Port Ingest__Database__Name Ingest__Database__Username Ingest__Database__Password Ingest__Bucket Ingest__KmsKeyId; do
  printf '%s' "$API_TASK" | jq -e --arg container "$ECS_API_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
  printf '%s' "$WORKER_TASK" | jq -e --arg container "$ECS_WORKER_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
done
for SETTING in Rendering__QueueUrl Rendering__SourceBucket Rendering__DerivativeBucket Rendering__WorkspaceRoot Rendering__AwsRegion Rendering__CallbackBaseUrl; do
  printf '%s' "$RENDERER_TASK" | jq -e --arg container "$ECS_RENDERER_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
done
for SETTING in Ingest__Database__Host Ingest__Database__Port Ingest__Database__Name Ingest__Database__Username Ingest__Database__Password Ingest__RuntimeDatabase__Username Ingest__RuntimeDatabase__Password; do
  printf '%s' "$MIGRATION_TASK_BASE" | jq -e --arg container "$ECS_MIGRATION_CONTAINER" --arg setting "$SETTING" '.containerDefinitions[] | select(.name == $container) | ((.environment // []) + (.secrets // [])) | any(.name == $setting)' >/dev/null
done

API_UPDATED="$(printf '%s' "$API_TASK" | jq --arg image "$API_IMAGE" --arg name "$ECS_API_CONTAINER" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" --arg rendererDigest "$RENDERER_DIGEST" --arg evidence "$PROMOTION_EVIDENCE_ID" --arg operator "$COGNITO_OWNER_USERNAME" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "Authentication__CognitoAuthority" and .name != "Authentication__CognitoClientId" and .name != "Rendering__DeployedArtifactDigest" and .name != "Rendering__PromotionEvidenceId" and .name != "Rendering__OperationsUsername" and .name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"Authentication__CognitoAuthority",value:$authority},{name:"Authentication__CognitoClientId",value:$client},{name:"Rendering__DeployedArtifactDigest",value:$rendererDigest},{name:"Rendering__PromotionEvidenceId",value:$evidence},{name:"Rendering__OperationsUsername",value:$operator},{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
WORKER_UPDATED="$(printf '%s' "$WORKER_TASK" | jq --arg image "$WORKER_IMAGE" --arg name "$ECS_WORKER_CONTAINER" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
RENDERER_UPDATED="$(printf '%s' "$RENDERER_TASK" | jq --arg image "$RENDERER_IMAGE" --arg name "$ECS_RENDERER_CONTAINER" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
MIGRATION_UPDATED="$(printf '%s' "$MIGRATION_TASK_BASE" | jq --arg image "$WORKER_IMAGE" --arg name "$ECS_MIGRATION_CONTAINER" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
API_TASK_ARN="$(aws ecs register-task-definition --cli-input-json "$API_UPDATED" --query taskDefinition.taskDefinitionArn --output text)"
WORKER_TASK_ARN="$(aws ecs register-task-definition --cli-input-json "$WORKER_UPDATED" --query taskDefinition.taskDefinitionArn --output text)"
RENDERER_TASK_ARN="$(aws ecs register-task-definition --cli-input-json "$RENDERER_UPDATED" --query taskDefinition.taskDefinitionArn --output text)"
MIGRATION_TASK_ARN="$(aws ecs register-task-definition --cli-input-json "$MIGRATION_UPDATED" --query taskDefinition.taskDefinitionArn --output text)"
PREVIOUS_API_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --query 'services[0].taskDefinition' --output text)"
PREVIOUS_WORKER_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].taskDefinition' --output text)"
PREVIOUS_RENDERER_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$RENDERER_SERVICE" --query 'services[0].taskDefinition' --output text)"
PREVIOUS_API_COUNT="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --query 'services[0].desiredCount' --output text)"
PREVIOUS_WORKER_COUNT="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].desiredCount' --output text)"
PREVIOUS_RENDERER_COUNT="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$RENDERER_SERVICE" --query 'services[0].desiredCount' --output text)"
NETWORK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].networkConfiguration' --output json)"
MIGRATION_TASK="$(ECS_CLUSTER="$ECS_CLUSTER" MIGRATION_TASK_DEFINITION="$MIGRATION_TASK_ARN" ECS_MIGRATION_CONTAINER="$ECS_MIGRATION_CONTAINER" MIGRATION_NETWORK="$NETWORK" bash scripts/run-ecs-migration.sh)"

jq -n --arg tag "${RELEASE_TAG:-}" --arg source "$SOURCE_REVISION" --arg releaseRun "$RELEASE_RUN_ID" --arg manifest "$MANIFEST_DIGEST" --arg apiArtifact "$API_TAR_DIGEST" --arg workerArtifact "$WORKER_TAR_DIGEST" --arg rendererArtifact "$RENDERER_TAR_DIGEST" --arg apiImage "$API_IMAGE" --arg workerImage "$WORKER_IMAGE" --arg rendererImage "$RENDERER_IMAGE" --arg apiTask "$API_TASK_ARN" --arg workerTask "$WORKER_TASK_ARN" --arg rendererTask "$RENDERER_TASK_ARN" --arg migrationTaskDefinition "$MIGRATION_TASK_ARN" --arg previousApiTask "$PREVIOUS_API_TASK" --arg previousWorkerTask "$PREVIOUS_WORKER_TASK" --arg previousRendererTask "$PREVIOUS_RENDERER_TASK" --arg previousApiCount "$PREVIOUS_API_COUNT" --arg previousWorkerCount "$PREVIOUS_WORKER_COUNT" --arg previousRendererCount "$PREVIOUS_RENDERER_COUNT" --arg migrationTask "$MIGRATION_TASK" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" '{sourceRevision:$source,releaseRunId:$releaseRun,manifestDigest:$manifest,releaseArtifactDigest:$apiArtifact,workerReleaseArtifactDigest:$workerArtifact,rendererReleaseArtifactDigest:$rendererArtifact,apiImage:$apiImage,workerImage:$workerImage,rendererImage:$rendererImage,taskDefinition:$apiTask,workerTaskDefinition:$workerTask,rendererTaskDefinition:$rendererTask,migrationTaskDefinition:$migrationTaskDefinition,previousTaskDefinition:$previousApiTask,previousWorkerTaskDefinition:$previousWorkerTask,previousRendererTaskDefinition:$previousRendererTask,previousApiDesiredCount:($previousApiCount|tonumber),previousWorkerDesiredCount:($previousWorkerCount|tonumber),previousRendererDesiredCount:($previousRendererCount|tonumber),migrationTask:$migrationTask,cognitoAuthority:$authority,cognitoClientId:$client} + (if $tag == "" then {} else {tag:$tag} end)' > "$DEPLOYMENT_RECORD"

ROLLBACK_REQUIRED=0
rollback() {
  STATUS=$?
  test "$STATUS" -ne 0 || STATUS=1
  trap - ERR INT TERM
  if test "$ROLLBACK_REQUIRED" = 1; then
    set +e
    if test "${ACCEPTANCE_ACTIVE:-0}" = 1 && test -n "${ID_TOKEN:-}"; then
      curl --fail --silent --show-error --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/v1/rendering/operations/abort" >/dev/null || true
    fi
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$PREVIOUS_API_TASK" --desired-count "$PREVIOUS_API_COUNT" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$PREVIOUS_WORKER_TASK" --desired-count "$PREVIOUS_WORKER_COUNT" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$RENDERER_SERVICE" --task-definition "$PREVIOUS_RENDERER_TASK" --desired-count "$PREVIOUS_RENDERER_COUNT" --force-new-deployment >/dev/null
    timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE" "$RENDERER_SERVICE"
  fi
  exit "$STATUS"
}
trap rollback ERR INT TERM

ROLLBACK_REQUIRED=1
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$WORKER_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$RENDERER_SERVICE" --task-definition "$RENDERER_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$API_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE" "$RENDERER_SERVICE"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$ECS_SERVICE" "$API_TASK_ARN"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$WORKER_SERVICE" "$WORKER_TASK_ARN"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$RENDERER_SERVICE" "$RENDERER_TASK_ARN"
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 "$HEALTH_URL/live" >/dev/null
ID_TOKEN="$(aws cognito-idp initiate-auth --auth-flow USER_PASSWORD_AUTH --client-id "$COGNITO_CLIENT_ID" --auth-parameters "USERNAME=$COGNITO_OWNER_USERNAME,PASSWORD=$COGNITO_OWNER_PASSWORD" --query 'AuthenticationResult.IdToken' --output text)"
test -n "$ID_TOKEN" && test "$ID_TOKEN" != None
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/ready" >/dev/null
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/v1/rendering/operations/pause" >/dev/null
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/v1/rendering/operations/admit" >/dev/null
ACCEPTANCE_ACTIVE=1
timeout 180 env INGEST_SMOKE_TOKEN="$ID_TOKEN" RENDERING_ACCEPTANCE_FIXTURE="$RENDERING_ACCEPTANCE_FIXTURE" node scripts/smoke-rendering.mjs "$HEALTH_URL" > "$RELEASE_DIR/rendering-smoke.json"
timeout 120 env INGEST_SMOKE_TOKEN="$ID_TOKEN" node scripts/smoke-ingest.mjs "$HEALTH_URL" > "$RELEASE_DIR/ingest-smoke.json"
jq --slurpfile smoke "$RELEASE_DIR/ingest-smoke.json" --slurpfile rendering "$RELEASE_DIR/rendering-smoke.json" '. + {smoke:$smoke[0],renderingSmoke:$rendering[0]}' "$DEPLOYMENT_RECORD" > "$DEPLOYMENT_RECORD.tmp"
mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/v1/rendering/operations/activate" >/dev/null
ACCEPTANCE_ACTIVE=0
ROLLBACK_REQUIRED=0
trap - ERR INT TERM
