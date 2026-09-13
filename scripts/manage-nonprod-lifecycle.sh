#!/usr/bin/env bash
set -Eeuo pipefail

ACTION="${1:-}"
case "$ACTION" in
  start-database|start|stop|report) ;;
  *) echo 'Expected start-database, start, stop, or report.' >&2; exit 2 ;;
esac

require() {
  test -n "${!1:-}" || { echo "Missing required lifecycle setting: $1" >&2; exit 2; }
}

if test "$ACTION" != report; then
  require DATABASE_INSTANCE
fi
if test "$ACTION" = start || test "$ACTION" = stop; then
  require ECS_CLUSTER
  require ECS_SERVICE
  require WORKER_SERVICE
fi

database_state() {
  aws rds describe-db-instances \
    --db-instance-identifier "$DATABASE_INSTANCE" \
    --query 'DBInstances[0].DBInstanceStatus' \
    --output text
}

start_database() {
  local state
  state="$(database_state)"
  case "$state" in
    available) ;;
    stopped)
      aws rds start-db-instance --db-instance-identifier "$DATABASE_INSTANCE" >/dev/null
      timeout --foreground 1500 aws rds wait db-instance-available --db-instance-identifier "$DATABASE_INSTANCE"
      ;;
    starting)
      timeout --foreground 1500 aws rds wait db-instance-available --db-instance-identifier "$DATABASE_INSTANCE"
      ;;
    *) echo "Database $DATABASE_INSTANCE cannot start from state: $state" >&2; return 1 ;;
  esac
}

stop_database() {
  local state
  state="$(database_state)"
  case "$state" in
    stopped) ;;
    available)
      aws rds stop-db-instance --db-instance-identifier "$DATABASE_INSTANCE" >/dev/null
      timeout --foreground 1500 aws rds wait db-instance-stopped --db-instance-identifier "$DATABASE_INSTANCE"
      ;;
    stopping)
      timeout --foreground 1500 aws rds wait db-instance-stopped --db-instance-identifier "$DATABASE_INSTANCE"
      ;;
    *) echo "Database $DATABASE_INSTANCE cannot stop from state: $state" >&2; return 1 ;;
  esac
}

service_count() {
  aws ecs describe-services \
    --cluster "$ECS_CLUSTER" \
    --services "$1" \
    --query 'services[0].desiredCount' \
    --output text
}

report_state() {
  local label="$1" database=unknown api=unknown worker=unknown probed status=0
  if test -n "${DATABASE_INSTANCE:-}"; then
    if probed="$(database_state 2>/dev/null)"; then database="$probed"; else status=1; fi
  else
    status=1
  fi
  if test "$label" != start-database; then
    if test -n "${ECS_CLUSTER:-}" && test -n "${ECS_SERVICE:-}" && test -n "${WORKER_SERVICE:-}"; then
      if probed="$(service_count "$ECS_SERVICE" 2>/dev/null)"; then api="$probed"; else status=1; fi
      if probed="$(service_count "$WORKER_SERVICE" 2>/dev/null)"; then worker="$probed"; else status=1; fi
    else
      status=1
    fi
  fi
  printf 'action=%s database=%s api=%s worker=%s\n' "$label" "$database" "$api" "$worker"
  return "$status"
}

case "$ACTION" in
  start-database)
    start_database
    report_state "$ACTION"
    ;;
  start)
    start_database
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --desired-count 1 >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --desired-count 1 >/dev/null
    timeout --foreground 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
    report_state "$ACTION"
    ;;
  stop)
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --desired-count 0 >/dev/null
    aws ecs update-service --cluster "$ECS_CLUSTER" --service "$WORKER_SERVICE" --desired-count 0 >/dev/null
    timeout --foreground 600 aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" "$WORKER_SERVICE"
    stop_database
    report_state "$ACTION"
    ;;
  report)
    report_state "${REQUESTED_ACTION:-unknown}"
    ;;
esac
