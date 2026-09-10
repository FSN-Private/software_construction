#!/usr/bin/env bash
set -Eeuo pipefail

for NAME in AWS_REGION AWS_ROLE_ARN ECS_CLUSTER ECS_SERVICE WORKER_SERVICE HEALTH_URL COGNITO_CLIENT_ID COGNITO_OWNER_USERNAME COGNITO_OWNER_PASSWORD COMPATIBLE_TASK_DEFINITION COMPATIBLE_WORKER_TASK_DEFINITION ROLLBACK_REASON; do
  test -n "${!NAME:-}" || { echo "Missing required rollback setting: $NAME"; exit 1; }
done

CURRENT_API_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --query 'services[0].taskDefinition' --output text)"
CURRENT_WORKER_TASK="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$WORKER_SERVICE" --query 'services[0].taskDefinition' --output text)"
RESTORE_CURRENT=0

restore_current() {
  STATUS=$?
  test "$STATUS" -ne 0 || STATUS=1
  trap - ERR INT TERM
  if test "$RESTORE_CURRENT" = 1; then
    set +e
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$CURRENT_API_TASK" --force-new-deployment >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$CURRENT_WORKER_TASK" --force-new-deployment >/dev/null
    timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
  fi
  exit "$STATUS"
}

trap restore_current ERR INT TERM
RESTORE_CURRENT=1
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --task-definition "$COMPATIBLE_WORKER_TASK_DEFINITION" --force-new-deployment --query service.taskDefinition --output text
aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$COMPATIBLE_TASK_DEFINITION" --force-new-deployment --query service.taskDefinition --output text
timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$ECS_SERVICE" "$COMPATIBLE_TASK_DEFINITION"
bash scripts/assert-ecs-service-task.sh "$ECS_CLUSTER" "$WORKER_SERVICE" "$COMPATIBLE_WORKER_TASK_DEFINITION"
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 "$HEALTH_URL/live" >/dev/null
ID_TOKEN="$(aws cognito-idp initiate-auth --auth-flow USER_PASSWORD_AUTH --client-id "$COGNITO_CLIENT_ID" --auth-parameters "USERNAME=$COGNITO_OWNER_USERNAME,PASSWORD=$COGNITO_OWNER_PASSWORD" --query 'AuthenticationResult.IdToken' --output text)"
test -n "$ID_TOKEN" && test "$ID_TOKEN" != None
curl --fail --silent --show-error --retry 3 --retry-all-errors --max-time 15 --header "Authorization: Bearer $ID_TOKEN" "$HEALTH_URL/ready" >/dev/null
timeout 120 env INGEST_SMOKE_TOKEN="$ID_TOKEN" node scripts/smoke-ingest.mjs "$HEALTH_URL" > rollback-smoke.json
jq -n --arg reason "$ROLLBACK_REASON" --arg api "$COMPATIBLE_TASK_DEFINITION" --arg worker "$COMPATIBLE_WORKER_TASK_DEFINITION" --slurpfile smoke rollback-smoke.json '{reason:$reason,restoredTaskDefinition:$api,restoredWorkerTaskDefinition:$worker,smoke:$smoke[0]}' > rollback-record.json
RESTORE_CURRENT=0
trap - ERR INT TERM
