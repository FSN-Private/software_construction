#!/usr/bin/env bash
set -euo pipefail

test "$#" -eq 3 || { echo "Usage: $0 <cluster> <service> <expected-task-definition>" >&2; exit 2; }

CLUSTER="$1"
SERVICE="$2"
EXPECTED_TASK_DEFINITION="$3"
ACTUAL_TASK_DEFINITION="$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --query 'services[0].taskDefinition' --output text)"

if test "$ACTUAL_TASK_DEFINITION" != "$EXPECTED_TASK_DEFINITION"; then
  echo "ECS service $SERVICE stabilized on an unexpected task definition." >&2
  exit 1
fi
