# T054 nominal-client location decision

Status: **PROPOSED; owner decision required before specification or implementation**.
Date: 2026-10-21. Scope: controlled non-production only, Feature008 NFR001/SC001
and Feature011 T054. Existing thresholds remain binding until explicitly revised.

## Observations

PR45 repaired the native reference gate. Source1130d9c passed master CI39483592800
and release39485682288. Bootstrap39488893341 passed native comparison, migration,
startup and all five uncached renders. Request-start p951040.5ms met2000ms; cached
p95647ms failed500ms. Rollback to stopped API6/ingest4/renderer1 is confirmed.
No activation, ingest acceptance, qualified fixtures or successful deployment.

Twenty API telemetry events in the cache timing window have p9551.4824ms.
These lack exact request/route correlation. Mixed one-minute gateway metrics
include115requests and cannot isolate the cache cohort. They suggest substantial
delay outside measured application work; they do not prove a particular cause.

[Public transport run39543960738](https://github.com/FSN-Private/software_construction/actions/runs/39543960738)
uses two GitHub runners and the existing public Singapore endpoint with the API
stopped. All responses are35byte HTTP500 errors. PING is207.6-221.1ms; twenty
concurrent complete-response p95 is425.7ms and502.3ms. These failures are
diagnostic data, not healthy acceptance or a proved lower bound for healthy API
responses. Neither the runners' precise location nor a packet-level cause is
established.

[Same-runner version control39544365704](https://github.com/FSN-Private/software_construction/actions/runs/39544365704)
runs the unchanged probe under deployment Node22.23.3 then repository24.20.0:

| Sample | Node | PING range ms | Public error-response p95 ms |
| --- | --- | --- | --- |
| 1 | 22.23.3 | 220.0-220.5 | 485.3 |
| 1 | 24.20.0 | 222.1-222.7 | 455.5 |
| 2 | 22.23.3 | 197.9-198.7 | 421.3 |
| 2 | 24.20.0 | 197.6-198.3 | 401.1 |

Every series contains twenty35byte500 responses. Fixed version order and limited
samples prevent attributing the difference solely to Node. The comparison does
not demonstrate a runtime-only repair. Do not add application and transport
percentiles arithmetically or treat an error-path percentile as healthy latency.

## Owner choice

1. **Keep500ms from external GitHub runners as the release requirement.** Do not
   activate this release. Continue authenticated ingress/client/response-path
   investigation and, if justified, design delivery changes. Additional
   application micro-optimization is not supported by the current evidence.
   There is no demonstrated fix or guarantee that the present topology meets
   that end-to-end requirement. No threshold waiver is proposed.
2. **Define the nonprod nominal performance client in Singapore**, while retaining
   the current external-runner diagnostics separately. Run the complete real
   rendering and ingest acceptance through the same public HTTPS API from an
   on-demand regional client. Keep500ms/2000ms/30s, all five distinct uncached
   renders, all twenty concurrent cached streams, full-response timing, TLS,
   authentication, isolation, provenance and failure/rollback gates. This changes
   acceptance geography and the documented nominal-client interpretation; it
   does not fix or establish remote-user500ms performance. Owner approval is
   necessary, and actual regional acceptance may still fail.

Recommendation: option2 for a defined, reproducible nonprod reference benchmark.
Record the remaining remote-user performance limitation explicitly. This is not
production promotion, a numerical tolerance, an accepted failed run or permission
to silently replace the current gate before approval.

## Concrete regional design, subject to approval

Use one on-demand Node24 Lambda in ap-southeast-1 with512MiB memory,300s timeout,
reserved concurrency1, no provisioned concurrency, schedule, public function URL
or event source. No always-running runner or app resource increase. Call the
existing public API, not an internal route. Reuse the existing smoke scripts;
do not add prewarm calls, hidden retries, discarded samples or lower concurrency.
Cold function startup occurs before the workload; the first actual API request
still includes its HTTP/2 connection establishment, as the current script does.

The protected workflow keeps bootstrap, admission, activation and rollback
control. It invokes an exact published function version synchronously with
bounded fixture/source inputs and a short-lived owner token obtained by the
existing owner login. Keep that token in memory and a restricted temporary
invoke file only; never in function configuration, logs, artifacts or GitHub
outputs. Delete the temporary file in finally. Do not log the invocation event,
response headers, locators, input metadata or raw exception detail.

The function execution role needs no application/data/secret/AWS-read authority;
only narrowly scoped diagnostic log writes. The GitHub deploy role receives
InvokeFunction on this one function's qualified versions, not creation/admin
rights. No new unauthenticated public surface, wildcard IAM grants, database access,
VPC/NAT, persistent fixture storage, secret copy or production permission.

Bind the client bundle checksum to release evidence, verify published version
and code SHA before invocation, record source/release/function version/runtime
and region, all samples and results, and reject missing/mismatched evidence.
Use synchronous invocation with no automatic retry; both transport ambiguity and
FunctionError fail closed. Return validated bounded results to the existing
deployment evidence; activation and fixture persistence still require rendering,
ingest and evidence-upload success. A regional fail still invokes normal rollback.

The maximum configured compute per invocation is150GB-seconds. Charges are
on-demand request/duration plus associated API/log traffic; no zero-cost claim or
free-tier assumption. Confirm Singapore prices and retention before applying,
stay within the existing nonprod budget and remove the client when superseded.
AWS documents [synchronous invocation](https://docs.aws.amazon.com/lambda/latest/dg/invocation-sync.html),
[Node runtimes](https://docs.aws.amazon.com/lambda/latest/dg/lambda-nodejs.html) and
[request/duration pricing](https://aws.amazon.com/lambda/pricing/).

## Work after a decision

Option2 requires approved requirements/design/NFRs, an architecture decision,
numbered implementation tasks, secure runtime/IAM/source-binding/error-path
checks and independent review before push. Provisioning and gated integration
need concrete final approval after the implementation/template are reviewable.
No regional resources, specification changes or implementation exist yet.
Only then produce a fresh source-bound release and rerun actual bootstrap. Save
fixture IDs only after every gate passes. Option1 leaves the current gate intact
and authorizes investigation only, not deployment or a topology change.
