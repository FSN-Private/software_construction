import { exportMessage, exportText } from './export-messages';
export type ExportPin = {
  itemId: string;
  assetId: string;
  editId: string;
  commitId: string;
  originalSha256: string;
  originalByteLength: number;
  xmpSha256: string;
  displayName: string;
  versionLabel: string;
  reference: string | null;
  advancedHistory: boolean;
  selectedAtUtc: string;
};
export type Basket = {
  id: string;
  name: string;
  isDefault: boolean;
  revision: number;
  items: {
    pin: ExportPin;
    availability: string;
    previewState: string;
    outputName: string;
    reason: string | null;
    originalAvailability?: string;
    originalOutputName?: string;
    originalReason?: string | null;
  }[];
};
export type ExportRequest = {
  id: string;
  basketId: string;
  state: string;
  snapshot: { presetId: string; items: ExportPin[] };
  outcomes: {
    itemId: string;
    status: string;
    reason: string | null;
    filename: string | null;
    sha256: string | null;
    byteLength: number | null;
  }[];
  packageSha256: string | null;
  packageByteLength: number | null;
  expiresAtUtc: string | null;
  reason: string | null;
};
export function exportApi(baseUrl: string, token: string, library: string) {
  return async function request<T>(
    path: string,
    method = 'GET',
    body?: unknown,
    key: string = crypto.randomUUID(),
  ): Promise<T> {
    const response = await fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/exports${path}`,
      {
        method,
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          'Idempotency-Key': key,
          'X-Operation-Id': `op_${crypto.randomUUID()}`,
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        cache: 'no-store',
      },
    );
    if (response.status === 401)
      window.dispatchEvent(new Event('owner-session-expired'));
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(exportMessage(problem.code));
    }
    return response.status === 204
      ? (undefined as T)
      : ((await response.json()) as T);
  };
}
export async function addToDefaultBasket(
  baseUrl: string,
  token: string,
  library: string,
  selection: {
    assetId: string;
    editId: string;
    selectedVersion: string;
    advancedHistory?: boolean;
  },
) {
  const api = exportApi(baseUrl, token, library);
  const baskets = await api<Basket[]>('/baskets');
  const basket = baskets.find((b) => b.isDefault);
  if (!basket)
    throw new Error(exportText('Choose a default export basket first.'));
  await api(`/baskets/${basket.id}/items`, 'POST', {
    revision: basket.revision,
    selection,
  });
  window.dispatchEvent(new Event('exports-changed'));
}
