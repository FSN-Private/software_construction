# Security incident runbook

Owner: platform maintainer. Treat exposed credentials, unexpected workflow identity use, leaked telemetry, or manifest mismatch as an incident.

1. Stop deployments and revoke the affected GitHub token, AWS role session/credential, or Cognito user; preserve workflow logs and immutable evidence.
2. Record scope, timestamps, affected revisions, and known exposure in a private incident record. Do not paste secrets into issues, logs, or chat.
3. Rotate credentials, invalidate affected artifacts, and review CloudTrail/GitHub audit events before restoring access.
4. Resume only after a corrected revision passes CI and an owner approves the incident closure. Escalate immediately for production credentials or evidence tampering.
