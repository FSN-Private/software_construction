# Cost anomaly runbook

Owner: platform maintainer. Trigger: budget threshold or anomaly notification.

1. Freeze new non-production provisioning and record account, region, service, tag, forecast, and actual cost.
2. Use Cost Explorer and the operations dashboard to identify the resource; preserve tags and evidence before deletion.
3. Stop or delete only the confirmed non-production cost source. NAT gateways, idle ECS/RDS capacity, log retention, and artifact storage are first checks.
4. Escalate at the approved monthly cap or when spend cannot be explained within one hour. Do not treat a Budget notification as an instantaneous hard cap.
