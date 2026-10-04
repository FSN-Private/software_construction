export const exportMessages = {
  ready: 'Ready',
  unavailable: 'Unavailable',
  pending: 'Waiting',
  preparing: 'Preparing',
  completed: 'Completed',
  partial: 'Completed with failures',
  failed: 'Failed',
  retryable: 'Waiting to retry',
  cancelling: 'Cancelling; storage cleanup pending',
  cancelled: 'Cancelled',
  expired: 'Expired; prepare a new request',
  source_unavailable: 'Original bytes unavailable',
  selected_version_unavailable: 'Selected committed version unavailable',
  permanently_unavailable: 'Permanently unavailable after authorized purge',
  integrity_error: 'Integrity verification failed',
  incompatible_renderer: 'Approved renderer unavailable',
  resource_limit: 'Preparation limit exceeded',
  storage_unavailable: 'Storage unavailable',
  output_invalid: 'Output verification failed',
  stale_basket: 'Basket changed. Refresh and try again.',
  metadata_confirmation_required: 'Confirm the metadata warning first.',
  cost_warning_required: 'Acknowledge the preparation warning first.',
  committed_version_required:
    'Commit or discard working changes before adding this version.',
  advanced_history_required:
    'Enable advanced history to select this hidden version.',
  exports_disabled: 'Exports are unavailable in this environment.',
  quota_exceeded:
    'Export capacity reached. Remove saved work or wait for expiry.',
} as const;
export function exportMessage(code: string) {
  return exportText(
    exportMessages[code as keyof typeof exportMessages] ??
      'Export request unavailable. Refresh and try again.',
  );
}

// English is the fallback catalog; add locale catalogs here without changing component markup.
export const exportLabels: Record<string, string> = {
  'Export baskets': 'Export baskets',
  'Selections keep exact committed versions. Baskets do not extend source retention.':
    'Selections keep exact committed versions. Baskets do not extend source retention.',
  'Saved basket': 'Saved basket',
  'New basket name ': 'New basket name ',
  'Create basket': 'Create basket',
  'Basket name': 'Basket name',
  'Default basket': 'Default basket',
  'Save basket': 'Save basket',
  'Delete basket': 'Delete basket',
  'Clear selections': 'Clear selections',
  ' selections': ' selections',
  'Committed version ': 'Committed version ',
  '; edit': '; edit',
  'Original SHA-256 ': 'Original SHA-256 ',
  '; XMP SHA-256 ': '; XMP SHA-256 ',
  'Output: ': 'Output: ',
  'Preview exact pinned version / refresh preview':
    'Preview exact pinned version / refresh preview',
  'Preview: ': 'Preview: ',
  Remove: 'Remove',
  'Output preset': 'Output preset',
  'JPEG · 2048 pixels · private metadata':
    'JPEG · 2048 pixels · private metadata',
  'JPEG · 2048 pixels · retain renderer metadata':
    'JPEG · 2048 pixels · retain renderer metadata',
  'Unchanged originals': 'Unchanged originals',
  'I accept the embedded metadata disclosure.':
    'I accept the embedded metadata disclosure.',
  'I accept the time, storage and transfer cost of this large export.':
    'I accept the time, storage and transfer cost of this large export.',
  'Prepare immutable export': 'Prepare immutable export',
  'Export requests': 'Export requests',
  'Request ': 'Request ',
  'selections · ': 'selections · ',
  ' · SHA-256 ': ' · SHA-256 ',
  'ZIP: ': 'ZIP: ',
  ' bytes · SHA-256': ' bytes · SHA-256',
  Expires: 'Expires',
  'Download ZIP / renew download session':
    'Download ZIP / renew download session',
  'Save manifest': 'Save manifest',
  'Cancel preparation': 'Cancel preparation',
  'Review current basket readiness and prepare a new immutable request to retry.':
    'Review current basket readiness and prepare a new immutable request to retry.',
};
export const exportTranslations: Record<string, Record<string, string>> = {
  en: exportLabels,
};
export function exportText(
  message: string,
  values: Record<string, string | number> = {},
  locale = globalThis.navigator?.language ?? 'en',
): string {
  const catalog =
    exportTranslations[locale] ??
    exportTranslations[locale.split('-')[0]] ??
    exportTranslations.en;
  return (
    catalog[message] ??
    exportTranslations.en[message] ??
    message
  ).replace(/\{(\w+)\}/g, (match, key: string) =>
    values[key] === undefined ? match : String(values[key]),
  );
}
