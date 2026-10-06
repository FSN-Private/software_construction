# Parameterized recovery cost and capacity estimate

Status: inputs and owner approval are pending. This worksheet covers T023/T026; it does not certify a budget or a 24-hour restore. Use the [AWS Pricing Calculator](https://calculator.aws/) with **Asia Pacific (Sydney)** prices dated at review, plus source-region transfer charges. Save the private calculator export with the independent recovery evidence; record only its digest and approval reference here. No average photo size or production dollar amount is assumed.

Use at least 100,000 images and 1,000,000 content/version records for the reference library. Measure the complete protected object inventory, including historical exact versions and small XMP objects. Use bytes divided by 1,073,741,824 for the GiB quantities below; convert the provider's quoted rate units explicitly. All prices are USD before tax and discounts.

| Input | Measurement or pricing source | Value |
|---|---|---|
| `W` | Monthly average warm GiB: recent payloads, all retained database dumps, manifests, configuration, audit/evidence, temporary capture and isolated restore copies | Pending |
| `C` | Monthly average cold billable GiB: sum `count * max(sizeBytes, 131072)` over objects actually transitioned to Glacier Instant Retrieval, divided by 1,073,741,824 | Pending |
| `G` | Measured monthly additional protected GiB, including changed versions and captures; split warm/cold by measured age distribution | Pending |
| `T_seed`, `T_month`, `T_drill` | Source-to-recovery initial seed, normal monthly transfer and full rehearsal transfer GiB; include retry/rehash traffic where it crosses regions | Pending |
| `R_month`, `R_drill` | Monthly and complete-drill cold retrieval GiB, including retry/read amplification | Pending |
| `Q` | Monthly S3 PUT/COPY/GET/LIST/transition counts, KMS operations, DynamoDB reads/writes, SQS requests and evidence/log ingestion | Pending |
| `H` | Monthly and full-drill Fargate vCPU-hours, GiB-hours and ephemeral-storage GiB-hours; capture, copy, verification, restore and temporary API | Pending |
| `D` | Recovery RDS instance-hours, storage GiB-month and billable backup GiB-month; include isolated validation databases | Pending |
| `E` | ECR retained image GiB-month, interface endpoint-hours/data, CloudWatch alarms/log retention, SNS delivery and independent keys | Pending |
| `P_*` | Dated regional unit prices for each preceding resource, from the corresponding AWS calculator line | Pending |
| `M` | Owner-approved maximum protected bytes, monthly growth ceiling, monthly USD ceiling and one-rehearsal USD ceiling | Pending |

The monthly estimate is `W * P_standard + C * P_GIR + T_month * P_transfer + R_month * P_retrieval + priced(Q, H, D, E) + early_deletion_charges`. Initial seeding adds `T_seed * P_transfer` plus seed requests/compute. A full quarterly drill adds `T_drill * P_transfer + R_drill * P_retrieval + priced(drill_requests, drill_compute, drill_database, drill_target_storage)`. Report that full event cost and its monthly amortization (`event_cost / 3`) separately; amortization must not hide the cash needed to run a drill. Recompute the next-quarter estimate with measured `G` and the retention/age distribution. Do not assume snapshots stop growing or expire automatically.

Glacier Instant Retrieval has a 128-KB minimum billable object size and a 90-day minimum storage duration. An authorized deletion after a logical hold expires can still incur an early-deletion charge if the object entered Glacier recently. Include the remaining billed duration per retired version; the 90-day logical hold and the storage-class billing period are different clocks. Request, retrieval, transfer and lifecycle-transition charges also apply. [AWS S3 pricing](https://aws.amazon.com/s3/pricing/).

Measure seed/copy/hash throughput, database dump/restore duration and full owner-ready duration at `M`. A lower bound for the restore duration is the largest of `requiredBytes / sustainedCopyBytesPerSecond`, `requiredBytes / sustainedHashBytesPerSecond`, and database restore duration; actual end-to-end timing additionally includes provisioning, materialization, graph validation, access reconstruction and owner login. A bound alone does not prove RTO. Qualify only a measured complete run within 86,400 seconds and a verified cutoff within 3,600 seconds of declared loss.

Record the dated rate set, calculator export SHA-256, workload inventory SHA-256, measured throughput evidence, approval identity/reference and exact limits before enabling schedules or transitions. Exceeding either the approved growth/byte/cost ceiling or measured RPO/RTO blocks qualification; the operator must investigate and obtain a revised approved envelope. No owner approval or AWS measurement is recorded by this worksheet.
