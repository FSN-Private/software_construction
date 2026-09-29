# Client State and Lifecycle

No new persistent model/migration. Existing gallery card/detail/preferences and history/compare/ref records retain their contracts.

Applied browse state: URL branch/facets/sort/direction/cursor/asset/history. UI-only range/context keys stripped before browse API. Direct cursor links with unknown offset disclose unknown absolute range rather than invent one.

Filter draft: initialized when opening, validated before apply; cancel discards, reset clears draft only. Density changes presentation only.

Inspector selection: current or exact commit. Clear old preview immediately when selection changes. Compare resolves exact asset/XMP/provenance. Missing historical metadata explicitly unavailable; current working fields separately labelled.

Navigation: initiating image/scroll/page context plus dirty edit flag. Success clears dirty; stale-save refresh retains draft. Session loss is never success. Sign-out clears private UI and owner token; obsolete reads cancelled.
