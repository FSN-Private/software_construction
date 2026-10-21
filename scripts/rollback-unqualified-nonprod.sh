#!/usr/bin/env bash
set -Eeuo pipefail

# The main script already handles failures before activation. This handles a
# successful activation followed by a workflow/evidence failure, under the same
# serialized deployment job and credentials. Never restore from a stale record.
test "${DEPLOYMENT_ENVIRONMENT:-}" = nonprod && test "${AWS_REGION:-}" = ap-southeast-1
for NAME in DEPLOYMENT_RECORD SOURCE_REVISION ECR_REGISTRY ECS_CLUSTER ECS_SERVICE WORKER_SERVICE RENDERER_SERVICE; do
  test -n "${!NAME:-}" || { echo "Missing recovery setting: $NAME"; exit 1; }
done
ACCOUNT="${ECR_REGISTRY%%.*}"
[[ "$ACCOUNT" =~ ^[0-9]{12}$ ]]
jq -e --arg source "$SOURCE_REVISION" '.sourceRevision == $source' "$DEPLOYMENT_RECORD" >/dev/null
SERVICES=("$ECS_SERVICE" "$WORKER_SERVICE" "$RENDERER_SERVICE")
CURRENT_FIELDS=(taskDefinition workerTaskDefinition rendererTaskDefinition)
PREVIOUS_FIELDS=(previousTaskDefinition previousWorkerTaskDefinition previousRendererTaskDefinition)
COUNT_FIELDS=(previousApiDesiredCount previousWorkerDesiredCount previousRendererDesiredCount)
PREVIOUS=() COUNTS=()
for INDEX in 0 1 2; do
  EXPECTED="$(jq -er --arg key "${CURRENT_FIELDS[$INDEX]}" '.[$key]' "$DEPLOYMENT_RECORD")"
  PREVIOUS+=("$(jq -er --arg key "${PREVIOUS_FIELDS[$INDEX]}" '.[$key]' "$DEPLOYMENT_RECORD")")
  COUNTS+=("$(jq -er --arg key "${COUNT_FIELDS[$INDEX]}" '.[$key] | select(type == "number" and . >= 0 and . <= 100 and floor == .)' "$DEPLOYMENT_RECORD")")
  for TASK in "$EXPECTED" "${PREVIOUS[$INDEX]}"; do
    [[ "$TASK" =~ ^arn:aws:ecs:ap-southeast-1:$ACCOUNT:task-definition/[a-zA-Z0-9_-]+:[1-9][0-9]*$ ]]
  done
  ACTUAL="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "${SERVICES[$INDEX]}" --query 'services[0].taskDefinition' --output text)"
  test "$ACTUAL" = "$EXPECTED" || { echo 'Refusing recovery from a stale deployment record.'; exit 1; }
done

STATUS=0
for INDEX in 0 1 2; do
  aws ecs update-service --cluster "$ECS_CLUSTER" --service "${SERVICES[$INDEX]}" --task-definition "${PREVIOUS[$INDEX]}" --desired-count "${COUNTS[$INDEX]}" --force-new-deployment >/dev/null || STATUS=1
done
timeout 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "${SERVICES[@]}" || STATUS=1
for INDEX in 0 1 2; do
  STATE="$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "${SERVICES[$INDEX]}" --output json)" || { STATUS=1; continue; }
  printf '%s' "$STATE" | jq -e --arg task "${PREVIOUS[$INDEX]}" --argjson count "${COUNTS[$INDEX]}" \
    '(.failures | length) == 0 and (.services | length) == 1 and (.services[0] | .taskDefinition == $task and .desiredCount == $count and .runningCount == $count and .pendingCount == 0)' >/dev/null || STATUS=1
done
test "$STATUS" -eq 0 || { echo 'Recovery failed; inspect all three ECS services before further deployment.'; exit 1; }
echo 'Previous task definitions and desired/running counts restored; this release remains unqualified.'
