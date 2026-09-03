# Lineage and Merge Contract v1

Lineage and compare requests return exact version IDs, graph/table data, hidden/recovery status, differences, and preview/provenance state. Mutating commands require expected ref/working state and idempotency. Merge preparation/confirmation returns `stale_head` if input heads move; submit passes complete resolver result to feature 006. Errors include `protected_ref`, `unresolved_conflict`, `preview_unavailable`, `recovery_expired`, and `dirty_checkout`.
