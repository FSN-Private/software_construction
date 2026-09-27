import { useEffect, useState } from 'react';
import { Lineage } from './Lineage';
import './gallery.css';

type Card = {
  assetId: string;
  path: string;
  filename: string;
  fileType: string;
  capturedAt: string | null;
  camera: string | null;
  lens: string | null;
  rating: number | null;
  rejected: boolean | null;
  width: number | null;
  height: number | null;
  projectionState: string;
  previewState: 'pending' | 'failed' | 'available';
  previewRequestId: string | null;
};
type Page = {
  cards: Card[];
  count: number;
  nextCursor: string | null;
  generation: number;
  sort: string;
  direction: string;
  filters: Record<string, string[]>;
};
type Field = {
  name: string;
  text: string | null;
  number: number | null;
  date: string | null;
  boolean: boolean | null;
  source: string;
  state: string;
  timestampBasis: string | null;
};
type Detail = {
  assetId: string;
  editId: string | null;
  path: string;
  fileType: string;
  originalSha256: string;
  xmpSha256: string | null;
  lifecycleVersion: number;
  projectionState: string;
  parseErrorCode: string | null;
  fields: Field[];
  working: {
    branch: string;
    webVersion: number | null;
    webXmpSha256: string | null;
    deviceConflictCount: number;
  };
  thumbnail: {
    state: 'pending' | 'failed' | 'available';
    requestId: string | null;
    failureCode: string | null;
    sourceSha256: string | null;
    xmpSha256: string | null;
    selectedVersion: string | null;
    rendererRelease: string | null;
    rendererArtifactDigest: string | null;
    profile: string | null;
    matrixVersion: string | null;
    completedAt: string | null;
  };
};
type Preference = { hideGeolocation: boolean; version: number };

const filterFields: { key: string; label: string; type?: string }[] = [
  { key: 'captureFrom', label: 'Captured from', type: 'datetime-local' },
  { key: 'captureTo', label: 'Captured to', type: 'datetime-local' },
  { key: 'importFrom', label: 'Imported from', type: 'datetime-local' },
  { key: 'importTo', label: 'Imported to', type: 'datetime-local' },
  { key: 'path', label: 'Filename or path' },
  { key: 'fileType', label: 'File type' },
  { key: 'camera', label: 'Camera' },
  { key: 'lens', label: 'Lens' },
  { key: 'focalLengthMin', label: 'Focal length minimum', type: 'number' },
  { key: 'focalLengthMax', label: 'Focal length maximum', type: 'number' },
  { key: 'apertureMin', label: 'Aperture minimum', type: 'number' },
  { key: 'apertureMax', label: 'Aperture maximum', type: 'number' },
  { key: 'shutterMin', label: 'Exposure seconds minimum', type: 'number' },
  { key: 'shutterMax', label: 'Exposure seconds maximum', type: 'number' },
  { key: 'isoMin', label: 'ISO minimum', type: 'number' },
  { key: 'isoMax', label: 'ISO maximum', type: 'number' },
  { key: 'widthMin', label: 'Width minimum', type: 'number' },
  { key: 'widthMax', label: 'Width maximum', type: 'number' },
  { key: 'heightMin', label: 'Height minimum', type: 'number' },
  { key: 'heightMax', label: 'Height maximum', type: 'number' },
  { key: 'orientation', label: 'Orientation', type: 'number' },
  { key: 'rating', label: 'Rating', type: 'number' },
  { key: 'color', label: 'Color label' },
  { key: 'tag', label: 'Tags (comma separated)' },
  { key: 'title', label: 'Title' },
  { key: 'description', label: 'Description' },
  { key: 'creator', label: 'Creator' },
  { key: 'latitudeMin', label: 'Latitude minimum', type: 'number' },
  { key: 'latitudeMax', label: 'Latitude maximum', type: 'number' },
  { key: 'longitudeMin', label: 'Longitude minimum', type: 'number' },
  { key: 'longitudeMax', label: 'Longitude maximum', type: 'number' },
];

function displayedDate(value: string | null) {
  return value
    ? new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(value))
    : 'Unknown';
}

function localDateInput(value: string) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function displayedField(field: Field) {
  if (field.state !== 'present') return field.state;
  if (field.text !== null) return field.text;
  if (field.number !== null)
    return new Intl.NumberFormat().format(field.number);
  if (field.date !== null) return displayedDate(field.date);
  return String(field.boolean);
}

function Thumbnail({
  baseUrl,
  token,
  library,
  card,
}: {
  baseUrl: string;
  token: string;
  library: string;
  card: Card;
}) {
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    if (card.previewState !== 'available' || !card.previewRequestId) return;
    const controller = new AbortController();
    fetch(
      `${baseUrl}/v1/renditions/${card.previewRequestId}/derivative?library=${encodeURIComponent(library)}`,
      {
        headers: { Authorization: `Bearer ${token}` },
        signal: controller.signal,
      },
    )
      .then(async (response) => {
        if (!response.ok) throw new Error('Preview unavailable');
        return (await response.json()) as { locator: string };
      })
      .then((result) => setUrl(result.locator))
      .catch(() => setUrl(undefined));
    return () => controller.abort();
  }, [baseUrl, token, library, card.previewState, card.previewRequestId]);
  return url ? (
    <img src={url} alt={`Thumbnail of ${card.filename}`} loading="lazy" />
  ) : (
    <span className="gallery-placeholder" role="status">
      {card.previewState === 'failed'
        ? 'Thumbnail failed'
        : 'Thumbnail pending'}
    </span>
  );
}

export function Gallery({
  baseUrl,
  token,
  library,
}: {
  baseUrl: string;
  token: string;
  library: string;
}) {
  const [location, setLocation] = useState(() => window.location.search);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [page, setPage] = useState<Page>();
  const [detail, setDetail] = useState<Detail>();
  const [preference, setPreference] = useState<Preference>();
  const [revealed, setRevealed] = useState<{
    latitude: number;
    longitude: number;
  }>();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [editField, setEditField] = useState('rating');
  const [editValue, setEditValue] = useState('');
  const [editLongitude, setEditLongitude] = useState('');
  const [editStatus, setEditStatus] = useState('');
  const params = new URLSearchParams(location);
  const selected = params.get('asset');

  function navigate(next: URLSearchParams, state: object = {}) {
    const search = next.toString();
    window.history.pushState(
      state,
      '',
      `${window.location.pathname}${search ? '?' + search : ''}`,
    );
    setLocation(window.location.search);
  }

  async function api<T>(
    path: string,
    method = 'GET',
    body?: object,
  ): Promise<T> {
    const response = await fetch(`${baseUrl}/v1${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        'Idempotency-Key': crypto.randomUUID(),
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      cache: 'no-store',
    });
    const result = (await response.json()) as T & { code?: string };
    if (!response.ok) throw new Error(result.code ?? 'Gallery request failed');
    return result;
  }

  useEffect(() => {
    const onPop = () => setLocation(window.location.search);
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);
  useEffect(() => {
    const values = new URLSearchParams(location);
    setDraft(
      Object.fromEntries(
        filterFields.map((field) => [
          field.key,
          field.type === 'datetime-local'
            ? localDateInput(values.get(field.key) ?? '')
            : (values.get(field.key) ?? ''),
        ]),
      ),
    );
    setError('');
    setRevealed(undefined);
    setEditStatus('');
    if (values.has('asset')) {
      const asset = values.get('asset');
      setDetail(undefined);
      void api<Detail>(
        `/libraries/${encodeURIComponent(library)}/gallery/${encodeURIComponent(asset ?? '')}?branch=${encodeURIComponent(values.get('branch') ?? 'main')}`,
      )
        .then(setDetail)
        .catch((reason) => setError(String(reason)));
    } else {
      values.delete('asset');
      setPage(undefined);
      void api<Page>(
        `/libraries/${encodeURIComponent(library)}/gallery?${values}`,
      )
        .then(setPage)
        .catch((reason) => setError(String(reason)));
    }
  }, [baseUrl, token, library, location]);
  useEffect(() => {
    void api<Preference>(
      `/libraries/${encodeURIComponent(library)}/gallery/preferences`,
    )
      .then(setPreference)
      .catch((reason) => setError(String(reason)));
  }, [baseUrl, token, library]);

  function applyFilters() {
    const next = new URLSearchParams(location);
    for (const field of filterFields) {
      next.delete(field.key);
      if (draft[field.key]?.trim())
        next.set(
          field.key,
          field.type === 'datetime-local'
            ? new Date(draft[field.key]).toISOString()
            : draft[field.key].trim(),
        );
    }
    for (const key of ['rejected', 'locationPresent']) {
      next.delete(key);
      if (draft[key]) next.set(key, draft[key]);
    }
    next.delete('cursor');
    next.delete('asset');
    navigate(next);
  }
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (reason) {
      setError(String(reason));
    } finally {
      setBusy(false);
    }
  }
  async function downloadOriginal(asset: Detail) {
    const response = await fetch(
      `${baseUrl}/v1/assets/${asset.assetId}/content`,
      { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' },
    );
    if (!response.ok) throw new Error('Original unavailable');
    const bytes = await response.arrayBuffer();
    const hash = Array.from(
      new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)),
      (value) => value.toString(16).padStart(2, '0'),
    ).join('');
    if (hash !== response.headers.get('X-Content-SHA256'))
      throw new Error('Original checksum failed');
    const url = URL.createObjectURL(new Blob([bytes]));
    const link = document.createElement('a');
    link.href = url;
    link.download = asset.path.split('/').at(-1) ?? asset.assetId;
    link.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async function fullPreview(asset: Detail) {
    if (
      !asset.editId ||
      !asset.thumbnail.selectedVersion ||
      !asset.thumbnail.profile
    )
      throw new Error('A committed preview version is unavailable');
    const request = await api<{ requestId: string; state: string }>(
      `/renditions`,
      'POST',
      {
        assetId: asset.assetId,
        editId: asset.editId,
        selectedVersion: asset.thumbnail.selectedVersion,
        rendition: 'preview-2048',
        profile: asset.thumbnail.profile,
        libraryId: library,
      },
    );
    if (request.state !== 'completed')
      throw new Error(
        'Full preview is pending; retry after rendering completes',
      );
    const locator = await api<{ locator: string }>(
      `/renditions/${request.requestId}/derivative?library=${encodeURIComponent(library)}`,
    );
    window.open(locator.locator, '_blank', 'noopener,noreferrer');
  }
  async function updatePreference() {
    if (!preference) return;
    const updated = await api<Preference>(
      `/libraries/${encodeURIComponent(library)}/gallery/preferences`,
      'PUT',
      {
        hideGeolocation: !preference.hideGeolocation,
        expectedVersion: preference.version,
      },
    );
    setPreference(updated);
    const next = new URLSearchParams(location);
    next.delete('cursor');
    if (updated.hideGeolocation)
      for (const key of [
        'locationPresent',
        'latitudeMin',
        'latitudeMax',
        'longitudeMin',
        'longitudeMax',
      ])
        next.delete(key);
    navigate(next);
  }

  async function refreshDetail() {
    if (!selected) return;
    setDetail(
      await api<Detail>(
        `/libraries/${encodeURIComponent(library)}/gallery/${encodeURIComponent(selected)}?branch=${encodeURIComponent(params.get('branch') ?? 'main')}`,
      ),
    );
  }
  async function saveMetadata() {
    if (!detail) return;
    let value: unknown = editValue;
    if (editField === 'rating' || editField === 'color')
      value = editValue === '' ? null : Number(editValue);
    if (editField === 'rejected') value = editValue === 'true';
    if (editField === 'tags')
      value = editValue
        .split(',')
        .map((tag) => tag.trim())
        .filter(Boolean);
    if (editField === 'location')
      value =
        editValue === '' && editLongitude === ''
          ? null
          : { latitude: Number(editValue), longitude: Number(editLongitude) };
    try {
      await api(
        `/libraries/${encodeURIComponent(library)}/gallery/${detail.assetId}/metadata`,
        'PATCH',
        {
          branch: detail.working.branch,
          editId: detail.editId,
          expectedWorkingVersion: detail.working.webVersion ?? 0,
          expectedBaseSha256: detail.working.webXmpSha256 ?? detail.xmpSha256,
          changes: { [editField]: value },
        },
      );
      setEditStatus('Metadata saved as an uncommitted working change.');
      await refreshDetail();
    } catch (reason) {
      if (String(reason).includes('stale_working_state')) {
        setEditStatus(
          'The working state changed. Details refreshed; review and retry your edit.',
        );
        await refreshDetail();
      } else throw reason;
    }
  }

  if (selected && params.get('history') === '1')
    return (
      <Lineage
        key={`${selected}/${params.get('branch') ?? 'main'}`}
        baseUrl={baseUrl}
        token={token}
        library={library}
        asset={selected}
        branch={params.get('branch') ?? 'main'}
        onSwitchBranch={(nextBranch) => {
          const next = new URLSearchParams(location);
          next.set('branch', nextBranch);
          next.delete('cursor');
          navigate(next);
        }}
        onBack={() => {
          if (window.history.state?.galleryHistory) window.history.back();
          else {
            const next = new URLSearchParams(location);
            next.delete('history');
            navigate(next);
          }
        }}
      />
    );

  return (
    <section aria-labelledby="gallery-heading" className="gallery">
      <h2 id="gallery-heading">Gallery</h2>
      {error && <p role="alert">{error}</p>}
      <p role="status" aria-live="polite">
        {busy
          ? 'Working…'
          : selected
            ? detail
              ? `Details for ${detail.path}`
              : 'Loading details…'
            : page
              ? `${new Intl.NumberFormat().format(page.count)} matching images`
              : 'Loading gallery…'}
      </p>
      {selected && detail ? (
        <article>
          <button
            type="button"
            onClick={() => {
              if (window.history.state?.galleryDetail) window.history.back();
              else {
                const next = new URLSearchParams(location);
                next.delete('asset');
                navigate(next);
              }
            }}
          >
            Back to results
          </button>
          <h3>{detail.path}</h3>
          <p>
            Original SHA-256: <code>{detail.originalSha256}</code>
          </p>
          <p>
            Metadata projection: {detail.projectionState}
            {detail.parseErrorCode ? ` (${detail.parseErrorCode})` : ''}.
          </p>
          <p>
            Branch: {detail.working.branch}.{' '}
            {detail.working.webVersion
              ? `Web metadata change ${detail.working.webVersion} is uncommitted.`
              : 'No web working change.'}{' '}
            {detail.working.deviceConflictCount > 0
              ? `${detail.working.deviceConflictCount} device conflicts require resolution.`
              : ''}
          </p>
          <p>
            Thumbnail: {detail.thumbnail.state}
            {detail.thumbnail.failureCode
              ? ` (${detail.thumbnail.failureCode})`
              : ''}
            .
          </p>
          {detail.thumbnail.state === 'available' &&
            detail.thumbnail.requestId && (
              <Thumbnail
                baseUrl={baseUrl}
                token={token}
                library={library}
                card={{
                  assetId: detail.assetId,
                  path: detail.path,
                  filename: detail.path.split('/').at(-1) ?? detail.path,
                  fileType: detail.fileType,
                  capturedAt: null,
                  camera: null,
                  lens: null,
                  rating: null,
                  rejected: null,
                  width: null,
                  height: null,
                  projectionState: detail.projectionState,
                  previewState: detail.thumbnail.state,
                  previewRequestId: detail.thumbnail.requestId,
                }}
              />
            )}
          {detail.thumbnail.state === 'available' && (
            <p>
              Preview source: {detail.thumbnail.sourceSha256}; XMP:{' '}
              {detail.thumbnail.xmpSha256}; version:{' '}
              {detail.thumbnail.selectedVersion}; renderer:{' '}
              {detail.thumbnail.rendererRelease}; artifact:{' '}
              {detail.thumbnail.rendererArtifactDigest}; profile:{' '}
              {detail.thumbnail.profile}; matrix:{' '}
              {detail.thumbnail.matrixVersion}; completed:{' '}
              {displayedDate(detail.thumbnail.completedAt)}.
            </p>
          )}
          <div className="gallery-actions">
            <button
              type="button"
              onClick={() => {
                const next = new URLSearchParams(location);
                next.set('history', '1');
                navigate(next, { galleryHistory: true });
              }}
            >
              View image history
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => void run(() => downloadOriginal(detail))}
            >
              Download original
            </button>
            <button
              type="button"
              disabled={busy || !detail.thumbnail.selectedVersion}
              onClick={() => void run(() => fullPreview(detail))}
            >
              Open full preview
            </button>
            <a href="#library-heading">
              Import, delete, or restore in Library controls
            </a>
          </div>
          <h4>Metadata</h4>
          <dl className="gallery-fields">
            {detail.fields.map((field, index) => (
              <div key={`${field.name}-${index}`}>
                <dt>{field.name}</dt>
                <dd>
                  {displayedField(field)}{' '}
                  <small>
                    ({field.source}
                    {field.timestampBasis ? `; ${field.timestampBasis}` : ''})
                  </small>
                </dd>
              </div>
            ))}
          </dl>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void run(saveMetadata);
            }}
          >
            <h4>Edit XMP metadata</h4>
            <p>Changes remain uncommitted on branch {detail.working.branch}.</p>
            <label>
              Field{' '}
              <select
                value={editField}
                onChange={(event) => {
                  setEditField(event.target.value);
                  setEditValue('');
                  setEditLongitude('');
                }}
              >
                <option value="rating">Rating</option>
                <option value="rejected">Rejected</option>
                <option value="color">Color label</option>
                <option value="tags">Tags</option>
                <option value="title">Title</option>
                <option value="description">Description</option>
                <option value="location">Location</option>
              </select>
            </label>{' '}
            <label>
              {editField === 'location' ? 'Latitude' : 'Value'}{' '}
              {editField === 'rejected' ? (
                <select
                  value={editValue}
                  onChange={(event) => setEditValue(event.target.value)}
                  required
                >
                  <option value="">Select</option>
                  <option value="true">Rejected</option>
                  <option value="false">Not rejected</option>
                </select>
              ) : (
                <input
                  type={
                    ['rating', 'color', 'location'].includes(editField)
                      ? 'number'
                      : 'text'
                  }
                  min={
                    editField === 'rating'
                      ? 0
                      : editField === 'color'
                        ? 0
                        : editField === 'location'
                          ? -90
                          : undefined
                  }
                  max={
                    editField === 'rating'
                      ? 5
                      : editField === 'color'
                        ? 4
                        : editField === 'location'
                          ? 90
                          : undefined
                  }
                  step={editField === 'location' ? 'any' : undefined}
                  value={editValue}
                  onChange={(event) => setEditValue(event.target.value)}
                  aria-describedby="gallery-edit-help"
                />
              )}
            </label>{' '}
            {editField === 'location' && (
              <label>
                Longitude{' '}
                <input
                  type="number"
                  min={-180}
                  max={180}
                  step="any"
                  value={editLongitude}
                  onChange={(event) => setEditLongitude(event.target.value)}
                />
              </label>
            )}
            <p id="gallery-edit-help">
              Blank rating, color, title, description, or location clears that
              value. Separate tags with commas.
            </p>
            <button type="submit" disabled={busy}>
              Save working change
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => void run(refreshDetail)}
            >
              Refresh details
            </button>
            {editStatus && <p role="status">{editStatus}</p>}
          </form>
          <button
            type="button"
            disabled={busy}
            onClick={() =>
              void run(async () =>
                setRevealed(
                  await api(
                    `/libraries/${encodeURIComponent(library)}/gallery/${detail.assetId}/location?branch=${encodeURIComponent(detail.working.branch)}`,
                  ),
                ),
              )
            }
          >
            Reveal retained location
          </button>
          {revealed && (
            <p role="status">
              Coordinates: {revealed.latitude}, {revealed.longitude}
            </p>
          )}
        </article>
      ) : (
        <>
          <div className="gallery-actions">
            <label>
              Sort by{' '}
              <select
                value={params.get('sort') ?? 'captureDate'}
                onChange={(event) => {
                  const next = new URLSearchParams(location);
                  next.set('sort', event.target.value);
                  next.delete('cursor');
                  navigate(next);
                }}
              >
                <option value="captureDate">Capture date</option>
                <option value="importDate">Import date</option>
                <option value="filename">Filename</option>
                <option value="rating">Rating</option>
                <option value="dimensions">Dimensions</option>
              </select>
            </label>
            <label>
              Direction{' '}
              <select
                value={params.get('direction') ?? 'desc'}
                onChange={(event) => {
                  const next = new URLSearchParams(location);
                  next.set('direction', event.target.value);
                  next.delete('cursor');
                  navigate(next);
                }}
              >
                <option value="asc">Ascending</option>
                <option value="desc">Descending</option>
              </select>
            </label>
            {preference && (
              <button
                type="button"
                disabled={busy}
                onClick={() => void run(updatePreference)}
              >
                {preference.hideGeolocation
                  ? 'Show location in gallery'
                  : 'Hide location in gallery'}
              </button>
            )}
          </div>
          <details>
            <summary>Filter images</summary>
            <form
              onSubmit={(event) => {
                event.preventDefault();
                applyFilters();
              }}
            >
              <div className="gallery-filters">
                {filterFields
                  .filter(
                    (field) =>
                      !preference?.hideGeolocation ||
                      (!field.key.startsWith('latitude') &&
                        !field.key.startsWith('longitude')),
                  )
                  .map((field) => (
                    <label key={field.key}>
                      {field.label}
                      <input
                        type={field.type ?? 'text'}
                        step={field.type === 'number' ? 'any' : undefined}
                        value={draft[field.key] ?? ''}
                        onChange={(event) =>
                          setDraft({
                            ...draft,
                            [field.key]: event.target.value,
                          })
                        }
                      />
                    </label>
                  ))}
                <label>
                  Rejected{' '}
                  <select
                    value={draft.rejected ?? ''}
                    onChange={(event) =>
                      setDraft({ ...draft, rejected: event.target.value })
                    }
                  >
                    <option value="">Any</option>
                    <option value="true">Rejected</option>
                    <option value="false">Not rejected</option>
                  </select>
                </label>
                {!preference?.hideGeolocation && (
                  <label>
                    Location present{' '}
                    <select
                      value={draft.locationPresent ?? ''}
                      onChange={(event) =>
                        setDraft({
                          ...draft,
                          locationPresent: event.target.value,
                        })
                      }
                    >
                      <option value="">Any</option>
                      <option value="true">Present</option>
                      <option value="false">Missing</option>
                    </select>
                  </label>
                )}
              </div>
              <button type="submit">Apply filters</button>{' '}
              <button
                type="button"
                onClick={() => {
                  setDraft({});
                  const next = new URLSearchParams(location);
                  for (const field of filterFields) next.delete(field.key);
                  next.delete('rejected');
                  next.delete('locationPresent');
                  next.delete('cursor');
                  navigate(next);
                }}
              >
                Clear filters
              </button>
            </form>
          </details>
          {page && (
            <>
              <p>
                Active filters:{' '}
                {Object.entries(page.filters).length === 0
                  ? 'None'
                  : Object.entries(page.filters)
                      .map(([key, values]) => `${key}: ${values.join(', ')}`)
                      .join('; ')}
              </p>
              <ul className="gallery-grid">
                {page.cards.map((card) => (
                  <li key={card.assetId}>
                    <button
                      type="button"
                      className="gallery-card"
                      onClick={() => {
                        const next = new URLSearchParams(location);
                        next.set('asset', card.assetId);
                        navigate(next, { galleryDetail: true });
                      }}
                    >
                      <Thumbnail
                        baseUrl={baseUrl}
                        token={token}
                        library={library}
                        card={card}
                      />
                      <strong>{card.filename}</strong>
                      <span>
                        {card.fileType.toUpperCase()} · {card.width ?? '?'} ×{' '}
                        {card.height ?? '?'}
                      </span>
                      <span>Captured {displayedDate(card.capturedAt)}</span>
                      <span>
                        Rating {card.rating ?? 'unknown'}
                        {card.rejected ? ' · rejected' : ''}
                      </span>
                      <span>
                        {card.projectionState === 'stale'
                          ? 'Metadata refresh pending'
                          : card.previewState === 'failed'
                            ? 'Preview failed'
                            : card.previewState === 'pending'
                              ? 'Preview pending'
                              : ''}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
              <nav aria-label="Gallery pages">
                <button
                  type="button"
                  disabled={!params.has('cursor')}
                  onClick={() => window.history.back()}
                >
                  Previous page
                </button>{' '}
                <button
                  type="button"
                  disabled={!page.nextCursor}
                  onClick={() => {
                    const next = new URLSearchParams(location);
                    next.set('cursor', page.nextCursor!);
                    navigate(next);
                  }}
                >
                  Next page
                </button>
              </nav>
            </>
          )}
        </>
      )}
    </section>
  );
}
