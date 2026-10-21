#!/usr/bin/env bash
set -Eeuo pipefail

if test "${BOOTSTRAP_RENDERING_FIXTURES:-false}" = true; then
  test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod || { echo 'Fixture bootstrap is restricted to nonprod.'; exit 1; }
elif test "${BOOTSTRAP_RENDERING_FIXTURES:-false}" != false; then
  echo 'BOOTSTRAP_RENDERING_FIXTURES must be true or false.'; exit 1
fi
MISSING=()
for NAME in RELEASE_DIR DEPLOYMENT_RECORD SOURCE_REVISION RELEASE_RUN_ID AWS_REGION ECR_REGISTRY API_REPOSITORY WORKER_REPOSITORY RENDERER_REPOSITORY ECS_CLUSTER ECS_SERVICE ECS_TASK_DEFINITION ECS_API_CONTAINER WORKER_SERVICE WORKER_TASK_DEFINITION ECS_WORKER_CONTAINER RENDERER_SERVICE RENDERER_TASK_DEFINITION ECS_RENDERER_CONTAINER MIGRATION_TASK_DEFINITION ECS_MIGRATION_CONTAINER HEALTH_URL COGNITO_AUTHORITY COGNITO_CLIENT_ID COGNITO_OWNER_USERNAME COGNITO_OWNER_PASSWORD RENDERING_ACCEPTANCE_FIXTURES; do
  if test "$NAME" = RENDERING_ACCEPTANCE_FIXTURES && test "${BOOTSTRAP_RENDERING_FIXTURES:-false}" = true; then continue; fi
  test -n "${!NAME:-}" || MISSING+=("$NAME")
done
test "${#MISSING[@]}" -eq 0 || { printf 'Missing required deployment setting: %s\n' "${MISSING[@]}"; exit 1; }
if test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod; then
  test "$AWS_REGION" = ap-southeast-1 && test -n "${REGIONAL_ACCEPTANCE_FUNCTION_VERSION:-}" || { echo 'Set the approved Singapore regional acceptance function version.'; exit 1; }
  node scripts/invoke-regional-acceptance.mjs --verify-client "$RELEASE_DIR/release-manifest.json"
fi
RENDER_OPERATION_ID="$(node -p 'require("node:crypto").randomUUID()')"

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

# Production must pass this gate before task registration or persistent migration.
# Non-production candidates can still run to generate qualification evidence.
if test "${RECOVERY_REQUIRED:-false}" = true; then
  API_IMAGE="$API_IMAGE" WORKER_IMAGE="$WORKER_IMAGE" RENDERER_IMAGE="$RENDERER_IMAGE" node scripts/verify-recovery-promotion.mjs
elif test -n "${RELEASE_TAG:-}"; then
  echo 'A tagged production release requires independently verified recovery qualification.'
  exit 1
fi

MANIFEST="$RELEASE_DIR/release-manifest.json"
MANIFEST_DIGEST="sha256:$(sha256sum "$MANIFEST" | awk '{print $1}')"
API_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "api.tar") | .digest' "$MANIFEST")"
WORKER_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "ingest-worker.tar") | .digest' "$MANIFEST")"
RENDERER_TAR_DIGEST="$(jq -er '.artifacts[] | select(.name == "render-worker.tar") | .digest' "$MANIFEST")"
RELEASE_ID="$(jq -er .releaseId "$MANIFEST")"
PROMOTION_EVIDENCE_ID="trusted-linux-${RELEASE_ID}-${RENDERER_DIGEST#sha256:}"
DARKTABLE_FAILURE_DIR="$RELEASE_DIR/rendering-native-failure" DARKTABLE_IMAGE="$RENDERER_IMAGE" bash tests/rendering/corpus/run-native.sh > "$RELEASE_DIR/rendering-corpus.txt"
# Resolve the canonical operator through Cognito while preserving the sign-in identifier.
AUTH_PARAMETERS="$(jq -cn --arg username "$COGNITO_OWNER_USERNAME" --arg password "$COGNITO_OWNER_PASSWORD" '{USERNAME:$username,PASSWORD:$password}')"
AUTHENTICATION="$(aws cognito-idp initiate-auth --auth-flow USER_PASSWORD_AUTH --client-id "$COGNITO_CLIENT_ID" --auth-parameters "$AUTH_PARAMETERS" --query AuthenticationResult --output json)"
ID_TOKEN="$(printf '%s' "$AUTHENTICATION" | jq -er '.IdToken | select(type == "string" and length > 0)')"
ACCESS_TOKEN="$(printf '%s' "$AUTHENTICATION" | jq -er '.AccessToken | select(type == "string" and length > 0)')"
OPERATOR_USERNAME="$(aws cognito-idp get-user --access-token "$ACCESS_TOKEN" --query Username --output text)"
test -n "$OPERATOR_USERNAME" && test "$OPERATOR_USERNAME" != None || { echo 'Authenticated operator identity is missing.' >&2; exit 1; }
unset AUTH_PARAMETERS AUTHENTICATION ACCESS_TOKEN
# End authenticated operator resolution.
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

API_UPDATED="$(printf '%s' "$API_TASK" | jq --arg image "$API_IMAGE" --arg name "$ECS_API_CONTAINER" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" --arg rendererDigest "$RENDERER_DIGEST" --arg evidence "$PROMOTION_EVIDENCE_ID" --arg operator "$OPERATOR_USERNAME" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "Authentication__CognitoAuthority" and .name != "Authentication__CognitoClientId" and .name != "Rendering__DeployedArtifactDigest" and .name != "Rendering__PromotionEvidenceId" and .name != "Rendering__OperationsUsername" and .name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"Authentication__CognitoAuthority",value:$authority},{name:"Authentication__CognitoClientId",value:$client},{name:"Rendering__DeployedArtifactDigest",value:$rendererDigest},{name:"Rendering__PromotionEvidenceId",value:$evidence},{name:"Rendering__OperationsUsername",value:$operator},{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
WORKER_UPDATED="$(printf '%s' "$WORKER_TASK" | jq --arg image "$WORKER_IMAGE" --arg name "$ECS_WORKER_CONTAINER" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
RENDERER_UPDATED="$(printf '%s' "$RENDERER_TASK" | jq --arg image "$RENDERER_IMAGE" --arg name "$ECS_RENDERER_CONTAINER" --arg rendererDigest "$RENDERER_DIGEST" --arg release "$RELEASE_ID" --arg source "$SOURCE_REVISION" --arg manifest "$MANIFEST_DIGEST" '.containerDefinitions |= map(if .name == $name then .image = $image | .environment = ((.environment // []) | map(select(.name != "Rendering__DeployedArtifactDigest" and .name != "PLATFORM_RELEASE_ID" and .name != "PLATFORM_SOURCE_REVISION" and .name != "PLATFORM_MANIFEST_DIGEST")) + [{name:"Rendering__DeployedArtifactDigest",value:$rendererDigest},{name:"PLATFORM_RELEASE_ID",value:$release},{name:"PLATFORM_SOURCE_REVISION",value:$source},{name:"PLATFORM_MANIFEST_DIGEST",value:$manifest}]) else . end) | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities, .registeredAt, .registeredBy)')"
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
ROLLBACK_REQUIRED=0
rollback() {
  STATUS=$?
  test "$STATUS" -ne 0 || STATUS=1
  trap - ERR INT TERM
  set +e
  if test "${ACCEPTANCE_ACTIVE:-0}" = 1 && test -n "${ID_TOKEN:-}"; then
    curl --fail --silent --show-error --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-abort" "$HEALTH_URL/v1/rendering/operations/abort" >/dev/null || true
  fi
  if test "$ROLLBACK_REQUIRED" = 1; then
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$PREVIOUS_API_TASK" --desired-count "$PREVIOUS_API_COUNT" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$PREVIOUS_WORKER_TASK" --desired-count "$PREVIOUS_WORKER_COUNT" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$RENDERER_SERVICE" --task-definition "$PREVIOUS_RENDERER_TASK" --desired-count "$PREVIOUS_RENDERER_COUNT" --force-new-deployment >/dev/null
    timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE" "$RENDERER_SERVICE"
  fi
  if test "${INTAKE_PAUSED:-0}" = 1 && test -n "${ID_TOKEN:-}"; then
    curl --fail --silent --show-error --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-resume" "$HEALTH_URL/v1/rendering/operations/resume" >/dev/null || true
  fi
  exit "$STATUS"
}
trap rollback ERR INT TERM
# A scheduled shutdown or first deployment has no previous API to probe.
# Keep the old-release readiness/drain gate whenever either service was enabled.
if test "$PREVIOUS_API_COUNT" != 0 || test "$PREVIOUS_RENDERER_COUNT" != 0; then
  curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/ready" >/dev/null
fi
if test "$PREVIOUS_RENDERER_COUNT" != 0; then
  INTAKE_PAUSED=1
  curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-drain" "$HEALTH_URL/v1/rendering/operations/drain" >/dev/null
  for _ in $(seq 1 60); do
    DRAIN_STATUS="$(curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/v1/rendering/operations")"
    test "$(printf '%s' "$DRAIN_STATUS" | jq -er .pending)" = 0 && break
    sleep 5
  done
  test "$(printf '%s' "$DRAIN_STATUS" | jq -er .pending)" = 0 || { echo 'Renderer queue did not drain before cutover.'; exit 1; }
fi

jq -n --arg tag "${RELEASE_TAG:-}" --arg source "$SOURCE_REVISION" --arg releaseRun "$RELEASE_RUN_ID" --arg manifest "$MANIFEST_DIGEST" --arg apiArtifact "$API_TAR_DIGEST" --arg workerArtifact "$WORKER_TAR_DIGEST" --arg rendererArtifact "$RENDERER_TAR_DIGEST" --arg apiImage "$API_IMAGE" --arg workerImage "$WORKER_IMAGE" --arg rendererImage "$RENDERER_IMAGE" --arg apiTask "$API_TASK_ARN" --arg workerTask "$WORKER_TASK_ARN" --arg rendererTask "$RENDERER_TASK_ARN" --arg migrationTaskDefinition "$MIGRATION_TASK_ARN" --arg previousApiTask "$PREVIOUS_API_TASK" --arg previousWorkerTask "$PREVIOUS_WORKER_TASK" --arg previousRendererTask "$PREVIOUS_RENDERER_TASK" --arg previousApiCount "$PREVIOUS_API_COUNT" --arg previousWorkerCount "$PREVIOUS_WORKER_COUNT" --arg previousRendererCount "$PREVIOUS_RENDERER_COUNT" --arg migrationTask "$MIGRATION_TASK" --arg authority "$COGNITO_AUTHORITY" --arg client "$COGNITO_CLIENT_ID" '{sourceRevision:$source,releaseRunId:$releaseRun,manifestDigest:$manifest,releaseArtifactDigest:$apiArtifact,workerReleaseArtifactDigest:$workerArtifact,rendererReleaseArtifactDigest:$rendererArtifact,apiImage:$apiImage,workerImage:$workerImage,rendererImage:$rendererImage,taskDefinition:$apiTask,workerTaskDefinition:$workerTask,rendererTaskDefinition:$rendererTask,migrationTaskDefinition:$migrationTaskDefinition,previousTaskDefinition:$previousApiTask,previousWorkerTaskDefinition:$previousWorkerTask,previousRendererTaskDefinition:$previousRendererTask,previousApiDesiredCount:($previousApiCount|tonumber),previousWorkerDesiredCount:($previousWorkerCount|tonumber),previousRendererDesiredCount:($previousRendererCount|tonumber),migrationTask:$migrationTask,cognitoAuthority:$authority,cognitoClientId:$client} + (if $tag == "" then {} else {tag:$tag} end)' > "$DEPLOYMENT_RECORD"

ROLLBACK_REQUIRED=1
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$WORKER_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$RENDERER_SERVICE" --task-definition "$RENDERER_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$API_TASK_ARN" --desired-count 1 --force-new-deployment --query service.taskDefinition --output text
timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE" "$RENDERER_SERVICE"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$ECS_SERVICE" "$API_TASK_ARN"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$WORKER_SERVICE" "$WORKER_TASK_ARN"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$RENDERER_SERVICE" "$RENDERER_TASK_ARN"
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 "$HEALTH_URL/live" >/dev/null
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/ready" >/dev/null
if test "${BOOTSTRAP_RENDERING_FIXTURES:-false}" = true; then
  timeout 300 env INGEST_SMOKE_TOKEN="$ID_TOKEN" node scripts/bootstrap-rendering-fixtures.mjs "$HEALTH_URL" "$RELEASE_DIR/rendering-fixtures.json"
  RENDERING_ACCEPTANCE_FIXTURES="$(jq -ce .fixtures "$RELEASE_DIR/rendering-fixtures.json")"
fi
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-pause" "$HEALTH_URL/v1/rendering/operations/pause" >/dev/null
ACCEPTANCE_ACTIVE=1
ADMISSION="$(curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-admit" "$HEALTH_URL/v1/rendering/operations/admit")"
if test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod; then
  RECEIPT="$(printf '%s' "$ADMISSION" | jq -er '.rollbackReceiptId | select(type == "string" and test("^[a-fA-F0-9-]{36}$"))')"
  jq --arg receipt "$RECEIPT" --arg operation "$RENDER_OPERATION_ID" '. + {renderingRollbackReceiptId:$receipt,renderingOperationId:$operation}' "$DEPLOYMENT_RECORD" > "$DEPLOYMENT_RECORD.tmp"
  mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"
fi
if test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod; then
  timeout 360 env INGEST_SMOKE_TOKEN="$ID_TOKEN" RENDERING_ACCEPTANCE_FIXTURES="$RENDERING_ACCEPTANCE_FIXTURES" node scripts/invoke-regional-acceptance.mjs "$RELEASE_DIR/release-manifest.json"
else
timeout 900 env INGEST_SMOKE_TOKEN="$ID_TOKEN" RENDERING_ACCEPTANCE_FIXTURES="$RENDERING_ACCEPTANCE_FIXTURES" node scripts/smoke-rendering.mjs "$HEALTH_URL" > "$RELEASE_DIR/rendering-smoke.json"
timeout 120 env INGEST_SMOKE_TOKEN="$ID_TOKEN" node scripts/smoke-ingest.mjs "$HEALTH_URL" > "$RELEASE_DIR/ingest-smoke.json"
fi
jq --slurpfile smoke "$RELEASE_DIR/ingest-smoke.json" --slurpfile rendering "$RELEASE_DIR/rendering-smoke.json" '. + {smoke:$smoke[0],renderingSmoke:$rendering[0]}' "$DEPLOYMENT_RECORD" > "$DEPLOYMENT_RECORD.tmp"
mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"
if test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod; then
  jq --slurpfile regional "$RELEASE_DIR/regional-acceptance.json" '. + {regionalAcceptance:$regional[0]}' "$DEPLOYMENT_RECORD" > "$DEPLOYMENT_RECORD.tmp"
  mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"
fi
if test "${DEPLOYMENT_ENVIRONMENT:-}" != nonprod; then
  curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --request POST --header "Authorization: Bearer $ID_TOKEN" --header "Idempotency-Key: $RENDER_OPERATION_ID-activate" "$HEALTH_URL/v1/rendering/operations/activate" >/dev/null
fi
ACCEPTANCE_ACTIVE=0
ROLLBACK_REQUIRED=0
trap - ERR INT TERM
