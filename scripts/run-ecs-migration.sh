#!/usr/bin/env bash
set -Eeuo pipefail

for NAME in ECS_CLUSTER MIGRATION_TASK_DEFINITION ECS_MIGRATION_CONTAINER MIGRATION_NETWORK; do
  test -n "${!NAME:-}" || { echo "Missing required migration setting: $NAME" >&2; exit 1; }
done

OVERRIDES="$(jq -cn --arg name "$ECS_MIGRATION_CONTAINER" '{containerOverrides:[{name:$name,command:["--migrate"]}]}')"
MIGRATION_TASK="$(aws ecs run-task --cluster "$ECS_CLUSTER" --launch-type FARGATE --task-definition "$MIGRATION_TASK_DEFINITION" --network-configuration "$MIGRATION_NETWORK" --overrides "$OVERRIDES" --query 'tasks[0].taskArn' --output text)"
test -n "$MIGRATION_TASK" && test "$MIGRATION_TASK" != None

MIGRATION_ACTIVE=1
cleanup_migration() {
  STATUS=$?
  test "$STATUS" -ne 0 || STATUS=1
  trap - ERR INT TERM
  if test "$MIGRATION_ACTIVE" = 1; then
    set +e
    aws ecs stop-task --cluster "$ECS_CLUSTER" --task "$MIGRATION_TASK" --reason "Release migration interrupted or timed out" >/dev/null
    timeout 120 aws ecs wait tasks-stopped --cluster "$ECS_CLUSTER" --tasks "$MIGRATION_TASK"
  fi
  exit "$STATUS"
}
trap cleanup_migration ERR INT TERM

timeout "${MIGRATION_WAIT_SECONDS:-600}" aws ecs wait tasks-stopped --cluster "$ECS_CLUSTER" --tasks "$MIGRATION_TASK"
MIGRATION_ACTIVE=0
test "$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "$MIGRATION_TASK" --query "tasks[0].containers[?name=='$ECS_MIGRATION_CONTAINER'].exitCode | [0]" --output text)" = 0
trap - ERR INT TERM
printf '%s\n' "$MIGRATION_TASK"
