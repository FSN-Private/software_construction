# Feature 008 validation

## User-approved modeled capacity result

The owner approved a modeled, rather than literal, 100,000-image capacity result on 2026-09-24 because a literal run would exceed the USD 20 monthly non-production budget.

Five approved-image darktable renders of the CC0 reference CR2/XMP fixture at 512px took 4,335, 4,570, 4,162, 4,192, and 4,080 ms. The nearest-rank p95 is 4,570 ms, which is within the 30-second native render-finish target.

At that measured p95, 99,000 thumbnails in 300 seconds require **1,509** single-concurrency renderer tasks. This is a conservative lower bound: it excludes SQS, S3, API callback, task start, and scaling delay.

The present one-task non-production configuration therefore does not satisfy NFR-017 / SC-006. Cache-hit and uncached request-start latency also remain unmeasured until a deployed HTTP fixture exists. See [modeled capacity evidence](evidence/modeled-capacity.json) and run `node tests/rendering/performance/model-capacity.mjs` to verify the calculation.
