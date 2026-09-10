# Alert delivery failure runbook

Owner: platform maintainer. Abort the affected deployment if its required alert path cannot notify the named owner.

1. Record the alarm ARN, SNS topic/subscription state, dashboard link, test timestamp, and operation or cost context.
2. Confirm the email subscription and KMS/SNS/CloudWatch permissions; do not disable the alarm to clear the condition.
3. Test one non-production alarm after the repair and require delivery within 15 minutes.
4. Escalate after 15 minutes without notification; keep manual monitoring active until the test succeeds.
