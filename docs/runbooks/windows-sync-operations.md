# Windows sync operations

Use `Platform.WindowsSync.exe status` first and retain only the safe operation ID/reason code. Do not copy tokens, paths, filenames, or content into tickets.

## Authentication and revocation

For `Authentication required`, obtain a new scoped token and rerun `setup` for the same device/root. The journal and local bytes remain intact. A revoked device cannot enumerate, hydrate, or upload. If revocation was intentional, set `PLATFORM_SYNC_EXPORT`, run `export`, verify the copy, and let the owner decide whether to retain or remove the root/state directories.

## Stuck hydration or upload

Confirm connectivity, available disk, and that the file is not held open by darktable, Explorer, or antivirus. Restart the provider; incomplete hydration files end in `.partial` and are never authoritative, while pending journal operations replay with the same idempotency key. Do not delete a dirty file to clear an upload. Escalate the safe operation ID if retries still fail.

## Disk or quota pressure

Unpin clean content first. Only clean, unpinned, non-pending, non-conflicted files may dehydrate. Free sufficient local space and retry pinning; its result is reported per item. Remote quota errors require server-side capacity remediation and do not authorize local-data deletion.

## Cursor reconciliation

An expired branch cursor is reset and the opaque identity namespace is enumerated again. Existing identical placeholders are retained. Dirty, pending, or conflicted local content must not be removed or overwritten. If reconciliation repeats, preserve the journal and conflict/stash folders and escalate the operation ID.

## Journal repair and rollback

Stop the provider and copy the state directory before repair. SQLite WAL and full synchronization make acknowledged operations durable. Never edit operation acknowledgements manually. Restore a known-compatible state-directory backup, or export local files and register a new device/root. A binary rollback is allowed only while its journal and remote protocol readers support the current schema.

## Safe uninstall

Revoke the device, export retained local data, verify the export, stop the provider, and uninstall the signed MSIX. Package removal intentionally leaves owner data in place. Remove the sync root/state directory only after the owner confirms that pending/conflicted content is no longer needed. Dehydration is not deletion and must not be used as an uninstall shortcut.
