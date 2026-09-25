# Feature 009 release plan

Status: implementation validation; deployment requires separate authorization.

1. Confirm the approved source build, migration 014–017 rehearsal, gallery unit and PostgreSQL/S3 checks, web build and accessibility checks, dependency audit, and independent review. Record the 100,000-asset page and cached-thumbnail benchmark measurements with environment details.
2. Back up the database and note active `gallery_generations` and `gallery_refresh` state. Apply expand-only migrations with the migration role. Set API `Gallery:LibraryId` and matching web `galleryLibraryId` for the canonical private library.
3. Start refresh, wait for `gallery_refresh.state='complete'` with `completed=total` and `failed=0`, then compare owner active-asset count with gallery projection count for the active generation. Spot-check XMP precedence, hidden location, owner denial, preview provenance, and Windows branch delivery before promotion.
4. If promotion fails, stop new gallery traffic and refresh, restore the prior API/web artifacts and prior active complete gallery generation in one transaction. Keep immutable content and working changes. Re-run the same count and privacy checks before traffic resumes.

No schema contraction, data deletion, branch rewrite, push, or deployment is part of feature implementation approval.
