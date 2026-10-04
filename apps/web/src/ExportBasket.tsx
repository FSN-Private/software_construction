import { useEffect, useRef, useState } from 'react';
import { exportApi, type Basket, type ExportRequest } from './export-api';
import { exportMessage, exportText } from './export-messages';
export function ExportBasket({
  baseUrl,
  token,
  library,
}: {
  baseUrl: string;
  token: string;
  library: string;
}) {
  const [baskets, setBaskets] = useState<Basket[]>([]);
  const [selected, setSelected] = useState('');
  const [requests, setRequests] = useState<ExportRequest[]>([]);
  const [olderRequests, setOlderRequests] = useState<ExportRequest[]>([]);
  const [historyCursor, setHistoryCursor] = useState<string>();
  const [hasOlder, setHasOlder] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const previousStates = useRef<Record<string, string>>({});
  const historyLoaded = useRef(false);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [preset, setPreset] = useState('jpeg-2048-private-v1');
  const [confirmed, setConfirmed] = useState(false);
  const [warning, setWarning] = useState(false);
  const [limits, setLimits] = useState({
    items: 250,
    baskets: 20,
    requests: 10,
  });
  const [previews, setPreviews] = useState<
    Record<
      string,
      {
        state: string;
        locator: string | null;
        provenance: { selectedVersion: string } | null;
      }
    >
  >({});
  const api = exportApi(baseUrl, token, library);
  const submission = useRef<{ fingerprint: string; key: string } | undefined>(
    undefined,
  );
  const basket =
    baskets.find((b) => b.id === selected) ?? baskets.find((b) => b.isDefault);
  function acceptRequests(history: ExportRequest[]) {
    const signature = (r: ExportRequest) =>
      `${r.state}:${r.outcomes.map((o) => o.status).join(',')}`;
    const changed = history.filter(
      (r) =>
        previousStates.current[r.id] &&
        previousStates.current[r.id] !== signature(r),
    );
    if (changed.length)
      setAnnouncement(
        changed
          .map(
            (r) =>
              exportText(
                'Export {id}: {state}. {completed} completed, {failed} failed.',
                {
                  id: r.id.slice(0, 8),
                  state: exportMessage(r.state),
                  completed: r.outcomes.filter((o) => o.status === 'completed')
                    .length,
                  failed: r.outcomes.filter((o) =>
                    ['failed', 'unavailable'].includes(o.status),
                  ).length,
                },
              ) +
              (['completed', 'partial'].includes(r.state)
                ? ' ' + exportText('Download available.')
                : ''),
          )
          .join(' '),
      );
    previousStates.current = Object.fromEntries(
      history.map((r) => [r.id, signature(r)]),
    );
    setRequests(history);
    const terminal = history.filter((r) =>
      ['expired', 'cancelled', 'unavailable'].includes(r.state),
    );
    if (!historyLoaded.current) {
      setHasOlder(terminal.length === 200);
      setHistoryCursor(terminal.at(-1)?.id);
    }
  }
  async function loadOlder() {
    if (!historyCursor) return;
    const history = await api<ExportRequest[]>(
      `/requests?before=${encodeURIComponent(historyCursor)}`,
    );
    historyLoaded.current = true;
    setOlderRequests((previous) => [
      ...previous,
      ...history.filter((r) => !previous.some((p) => p.id === r.id)),
    ]);
    setHistoryCursor(history.at(-1)?.id);
    setHasOlder(history.length === 200);
  }
  async function refresh() {
    const [saved, history] = await Promise.all([
      api<Basket[]>('/baskets'),
      api<ExportRequest[]>('/requests'),
    ]);
    setBaskets(saved);
    acceptRequests(history);
  }
  useEffect(() => {
    let active = true;
    historyLoaded.current = false;
    previousStates.current = {};
    setOlderRequests([]);
    setAnnouncement('');
    const load = () => {
      void Promise.all([
        exportApi(baseUrl, token, library)<Basket[]>('/baskets'),
        exportApi(baseUrl, token, library)<ExportRequest[]>('/requests'),
        exportApi(baseUrl, token, library)<typeof limits>('/limits'),
      ])
        .then(([b, r, configured]) => {
          if (active) {
            setBaskets(b);
            acceptRequests(r);
            setLimits(configured);
          }
        })
        .catch((error) => {
          if (active) setMessage(error.message);
        });
    };
    load();
    const timer = window.setInterval(load, 5000);
    window.addEventListener('exports-changed', load);
    return () => {
      active = false;
      window.clearInterval(timer);
      window.removeEventListener('exports-changed', load);
    };
  }, [baseUrl, token, library]);
  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    setMessage('');
    try {
      await action();
      await refresh();
    } catch (error) {
      setMessage(
        error instanceof Error
          ? error.message
          : exportText('Export unavailable.'),
      );
    } finally {
      setBusy(false);
    }
  }
  async function download(id: string) {
    const grant = await api<{ code: string; exchangeUrl: string }>(
      `/requests/${id}/download-grants`,
      'POST',
      {},
    );
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = grant.exchangeUrl;
    const input = document.createElement('input');
    input.type = 'hidden';
    input.name = 'code';
    input.value = grant.code;
    form.append(input);
    document.body.append(form);
    form.submit();
    form.remove();
  }
  return (
    <section aria-labelledby="exports-heading">
      <h2 id="exports-heading">{exportText('Export baskets')}</h2>
      <p>
        {exportText(
          'Selections keep exact committed versions. Baskets do not extend source retention.',
        )}
      </p>
      <p role="status" aria-live="polite">
        {message}
      </p>
      <label>
        {exportText('Saved basket')}{' '}
        <select
          value={basket?.id ?? ''}
          onChange={(e) => {
            setSelected(e.target.value);
            setConfirmed(false);
            setWarning(false);
          }}
        >
          {baskets.map((b) => (
            <option key={b.id} value={b.id}>
              {b.name}
              {b.isDefault ? exportText(' (default)') : ''} · {b.items.length}
            </option>
          ))}
        </select>
      </label>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          const name = new FormData(e.currentTarget).get('name');
          void run(async () => {
            const made = await api<Basket>('/baskets', 'POST', { name });
            setSelected(made.id);
          });
        }}
      >
        <label>
          {exportText('New basket name ')}
          <input name="name" required maxLength={120} />
        </label>
        <button disabled={busy || baskets.length >= limits.baskets}>
          {exportText('Create basket')}
        </button>
      </form>
      {basket && (
        <>
          <form
            key={basket.id + '/' + basket.revision}
            onSubmit={(e) => {
              e.preventDefault();
              const data = new FormData(e.currentTarget);
              void run(() =>
                api(`/baskets/${basket.id}`, 'PATCH', {
                  name: data.get('name'),
                  revision: basket.revision,
                  isDefault: data.get('default') === 'on',
                }),
              );
            }}
          >
            <label>
              {exportText('Basket name')}{' '}
              <input
                name="name"
                required
                maxLength={120}
                defaultValue={basket.name}
              />
            </label>
            <label>
              <input
                type="checkbox"
                name="default"
                defaultChecked={basket.isDefault}
              />
              {exportText('Default basket')}
            </label>
            <button disabled={busy}>{exportText('Save basket')}</button>
          </form>
          <button
            disabled={busy}
            onClick={() => {
              if (
                window.confirm(
                  exportText(
                    'Delete this saved basket? Request evidence will remain.',
                  ),
                )
              )
                void run(() =>
                  api(
                    `/baskets/${basket.id}?revision=${basket.revision}`,
                    'DELETE',
                  ),
                );
            }}
          >
            {exportText('Delete basket')}
          </button>
          <button
            disabled={busy || !basket.items.length}
            onClick={() => {
              if (
                window.confirm(
                  exportText('Remove every selection from this basket?'),
                )
              )
                void run(() =>
                  api(
                    `/baskets/${basket.id}/items?revision=${basket.revision}`,
                    'DELETE',
                  ),
                );
            }}
          >
            {exportText('Clear selections')}
          </button>
          <p>
            {basket.items.length} / {limits.items}
            {exportText(' selections')}
          </p>
          <ol>
            {basket.items.map((item) => (
              <li key={item.pin.itemId}>
                <details>
                  <summary>
                    {item.pin.displayName} · {item.pin.versionLabel} ·{' '}
                    {exportMessage(item.availability)}
                  </summary>
                  <p>
                    {exportText('Committed version ')}
                    <code>{item.pin.commitId}</code>
                    {exportText('; edit')} <code>{item.pin.editId}</code>
                  </p>
                  <p>
                    {exportText('Original SHA-256 ')}
                    <code>{item.pin.originalSha256}</code>
                    {exportText('; XMP SHA-256 ')}
                    <code>{item.pin.xmpSha256}</code>
                  </p>
                  <p>
                    {exportText('Output: ')}
                    {item.outputName}
                  </p>
                  <button
                    disabled={busy || item.availability !== 'ready'}
                    onClick={() =>
                      void run(async () => {
                        const preview = await api<{
                          state: string;
                          locator: string | null;
                          provenance: { selectedVersion: string } | null;
                        }>(
                          `/baskets/${basket.id}/items/${item.pin.itemId}/preview`,
                          'POST',
                          {},
                        );
                        if (
                          preview.locator &&
                          preview.provenance?.selectedVersion !==
                            item.pin.commitId
                        )
                          throw new Error(
                            exportText(
                              'Preview does not match the pinned version.',
                            ),
                          );
                        setPreviews((previous) => ({
                          ...previous,
                          [item.pin.itemId]: preview,
                        }));
                      })
                    }
                  >
                    {exportText(
                      'Preview exact pinned version / refresh preview',
                    )}
                  </button>
                  {previews[item.pin.itemId]?.locator && (
                    <img
                      src={previews[item.pin.itemId].locator!}
                      alt={exportText('Pinned version of {name}', {
                        name: item.pin.displayName,
                      })}
                      style={{ maxWidth: '100%', maxHeight: '20rem' }}
                    />
                  )}
                  {previews[item.pin.itemId] && (
                    <p role="status">
                      {exportText('Preview: ')}
                      {exportMessage(previews[item.pin.itemId].state)}
                    </p>
                  )}
                  {item.reason && <p>{exportMessage(item.reason)}</p>}
                </details>
                <button
                  disabled={busy}
                  aria-label={exportText('Remove {name} {id}', {
                    name: item.pin.displayName,
                    id: item.pin.itemId,
                  })}
                  onClick={() =>
                    void run(() =>
                      api(
                        `/baskets/${basket.id}/items/${item.pin.itemId}?revision=${basket.revision}`,
                        'DELETE',
                      ),
                    )
                  }
                >
                  {exportText('Remove')}
                </button>
              </li>
            ))}
          </ol>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void run(async () => {
                const input = {
                  presetId: preset,
                  revision: basket.revision,
                  metadataConfirmed: confirmed,
                  warningAcknowledged: warning,
                };
                const fingerprint = JSON.stringify([library, basket.id, input]);
                if (submission.current?.fingerprint !== fingerprint)
                  submission.current = {
                    fingerprint,
                    key: crypto.randomUUID(),
                  };
                const request = await api<ExportRequest>(
                  `/baskets/${basket.id}/requests`,
                  'POST',
                  input,
                  submission.current.key,
                );
                submission.current = undefined;
                setMessage(
                  exportText(
                    'Captured {count} exact selections in request {id}.',
                    { count: request.snapshot.items.length, id: request.id },
                  ),
                );
              });
            }}
          >
            <label>
              {exportText('Output preset')}{' '}
              <select
                value={preset}
                onChange={(e) => {
                  setPreset(e.target.value);
                  setConfirmed(false);
                }}
              >
                <option value="jpeg-2048-private-v1">
                  {exportText('JPEG · 2048 pixels · private metadata')}
                </option>
                <option value="jpeg-2048-retain-v1">
                  {exportText('JPEG · 2048 pixels · retain renderer metadata')}
                </option>
                <option value="originals-v1">
                  {exportText('Unchanged originals')}
                </option>
              </select>
            </label>
            <p>
              {exportText(
                preset === 'originals-v1'
                  ? 'Original bytes remain unchanged. This does not apply selected XMP edits and may disclose embedded location, device or person metadata.'
                  : preset === 'jpeg-2048-retain-v1'
                    ? 'Rendered images retain embedded renderer metadata, including potentially sensitive location, device or person metadata.'
                    : 'Rendered images use selected committed XMP. The private transform removes embedded descriptive metadata while preserving sRGB and ICC color data.',
              )}
            </p>
            {preset !== 'jpeg-2048-private-v1' && (
              <label>
                <input
                  type="checkbox"
                  checked={confirmed}
                  onChange={(e) => setConfirmed(e.target.checked)}
                />
                {exportText('I accept the embedded metadata disclosure.')}
              </label>
            )}
            {(basket.items.length >= 100 ||
              basket.items.reduce(
                (sum, i) => sum + i.pin.originalByteLength,
                0,
              ) >=
                1024 ** 3) && (
              <label>
                <input
                  type="checkbox"
                  checked={warning}
                  onChange={(e) => setWarning(e.target.checked)}
                />
                {exportText(
                  'I accept the time, storage and transfer cost of this large export.',
                )}
              </label>
            )}
            <button
              disabled={
                busy ||
                !basket.items.length ||
                basket.items.length > limits.items ||
                requests.filter(
                  (r) =>
                    r.basketId === basket.id &&
                    !['expired', 'cancelled', 'unavailable'].includes(r.state),
                ).length >= limits.requests ||
                ((basket.items.length >= 100 ||
                  basket.items.reduce(
                    (sum, i) => sum + i.pin.originalByteLength,
                    0,
                  ) >=
                    1024 ** 3) &&
                  !warning) ||
                (preset !== 'jpeg-2048-private-v1' && !confirmed)
              }
            >
              {exportText('Prepare immutable export')}
            </button>
          </form>
        </>
      )}
      <h3>{exportText('Export requests')}</h3>
      <p role="status" aria-live="polite" aria-atomic="true">
        {announcement}
      </p>
      {[
        ...requests,
        ...olderRequests.filter(
          (r) => !requests.some((current) => current.id === r.id),
        ),
      ].map((request) => (
        <article key={request.id}>
          <h4>
            {exportText('Request ')}
            <code>{request.id}</code>
          </h4>
          <p>
            {exportMessage(request.state)} · {request.snapshot.items.length}{' '}
            {exportText('selections · ')}
            {request.snapshot.presetId}
          </p>
          {request.reason && <p>{exportMessage(request.reason)}</p>}
          <ol>
            {request.outcomes.map((outcome) => (
              <li key={outcome.itemId}>
                {
                  request.snapshot.items.find(
                    (i) => i.itemId === outcome.itemId,
                  )?.displayName
                }
                : {exportMessage(outcome.status)}
                {outcome.reason ? ' · ' + exportMessage(outcome.reason) : ''}
                {outcome.filename && (
                  <span>
                    {' '}
                    · {outcome.filename}
                    {exportText(' · SHA-256 ')}
                    <code>{outcome.sha256}</code>
                  </span>
                )}
              </li>
            ))}
          </ol>
          {request.packageSha256 && (
            <p>
              {exportText('ZIP: ')}
              {request.packageByteLength}
              {exportText(' bytes · SHA-256')}{' '}
              <code>{request.packageSha256}</code>
            </p>
          )}
          {request.expiresAtUtc && (
            <p>
              {exportText('Expires')}{' '}
              <time dateTime={request.expiresAtUtc}>
                {new Date(request.expiresAtUtc).toLocaleString()}
              </time>
            </p>
          )}
          {['completed', 'partial'].includes(request.state) && (
            <button
              disabled={busy}
              onClick={() => void run(() => download(request.id))}
            >
              {exportText('Download ZIP / renew download session')}
            </button>
          )}
          {!request.outcomes.some((o) => o.status === 'pending') && (
            <button
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  const manifest = await api(
                    `/requests/${request.id}/manifest`,
                  );
                  const url = URL.createObjectURL(
                    new Blob([JSON.stringify(manifest, null, 2)], {
                      type: 'application/json',
                    }),
                  );
                  const a = document.createElement('a');
                  a.href = url;
                  a.download = `export-${request.id}-manifest.json`;
                  a.click();
                  URL.revokeObjectURL(url);
                })
              }
            >
              {exportText('Save manifest')}
            </button>
          )}
          {['pending', 'preparing', 'retryable'].includes(request.state) && (
            <button
              disabled={busy}
              onClick={() =>
                void run(() =>
                  api(`/requests/${request.id}/cancel`, 'POST', {}),
                )
              }
            >
              {exportText('Cancel preparation')}
            </button>
          )}
          {['failed', 'expired', 'unavailable', 'cancelled'].includes(
            request.state,
          ) && (
            <p>
              {exportText(
                'Review current basket readiness and prepare a new immutable request to retry.',
              )}
            </p>
          )}
        </article>
      ))}
      {hasOlder && (
        <button disabled={busy} onClick={() => void run(loadOlder)}>
          {exportText('Load older export requests')}
        </button>
      )}
    </section>
  );
}
