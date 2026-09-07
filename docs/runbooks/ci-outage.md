# CI outage runbook

Owner: platform maintainer. Abort a release when `ci / required` is missing, stale, cancelled, skipped, or failing.

1. Record the PR SHA, check URL, time, and failed job output; do not rerun a different SHA as evidence for the failed revision.
2. Classify the failure as product, registry, runner, or GitHub Actions outage. Retry only a documented transient external outage.
3. For a suspected credential or workflow-integrity event, stop and follow the security-incident runbook.
4. Resume only when every stable component check and `ci / required` succeeds for the intended SHA. Escalate after 15 minutes without an actionable failure or recovery.
