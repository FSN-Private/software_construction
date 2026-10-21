# Research and decisions

Owner approved Singapore nominal-client design2026-10-21 after reportdb8f18c.
Diagnostics branch `fix/011-render-cache-qualification` preserves raw evidence;
master1130d9c already includes the exact native-reference extension. Regional
client does not qualify remote-user500ms or any previous failed deployment.

Use the current existing smoke scripts unchanged. Node24 managed Lambda is
supported; record actual process/runtime identity. Synchronous Invoke requires
examining FunctionError/payload rather than HTTP status alone. Version-specific
GetFunctionConfiguration supplies CodeSha256 and settings. AWS CLI is already
in the protected deploy environment; invoke with AWS_MAX_ATTEMPTS=1 and bounded
timeouts, no SDK dependency. Numeric versionARN and exact release zip/code SHA
provide the source binding. A separate stack avoids changing persistent app data.

PowerShell and .NET ZipArchive are existing repository tooling; canonical Linux
packaging normalizes entries/timestamps. Do not invent a ZIP implementation or
introduce a packaging dependency. Zip bytes used for provisioning must come from
the verified release, even if local archives differ across toolchain patches.

References checked2026-10-21:
- https://docs.aws.amazon.com/lambda/latest/api/API_Invoke.html
- https://docs.aws.amazon.com/lambda/latest/api/API_GetFunctionConfiguration.html
- https://docs.aws.amazon.com/lambda/latest/dg/lambda-nodejs.html
- https://aws.amazon.com/lambda/pricing/ (confirm Singapore rate before apply).

No unresolved material clarification. Cost ceiling150GB-seconds per configured
invocation, not a zero-cost guarantee. No new dependencies or actions; existing
vendored CDK and pinned action risks/controls remain.
