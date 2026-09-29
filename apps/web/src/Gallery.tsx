import { useEffect, useRef, useState } from 'react';
import { Lineage } from './Lineage';
import { Modal } from './Modal';
import { ImageHistory } from './ImageHistory';
import { VersionPreview, type ImageVersion } from './Comparison';
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
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    setUrl(undefined);
    setFailed(false);
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
      .then((result) => {
        if (!controller.signal.aborted) setUrl(result.locator);
      })
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true);
      });
    return () => controller.abort();
  }, [baseUrl, token, library, card.previewState, card.previewRequestId]);
  return url && !failed ? (
    <img
      src={url}
      alt={`Thumbnail of ${card.filename}`}
      loading="lazy"
      onError={() => setFailed(true)}
    />
  ) : (
    <span className="gallery-placeholder" role="status">
      {card.previewState === 'failed' || failed
        ? 'Thumbnail failed'
        : 'Thumbnail pending'}
    </span>
  );
}

export function Gallery({
  baseUrl,
  token,
  library,
  onLibrary,
}: {
  baseUrl: string;
  token: string;
  library: string;
  onLibrary?: () => void;
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
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [filterError, setFilterError] = useState('');
  const [density, setDensity] = useState('comfortable');
  const [refresh, setRefresh] = useState(0);
  const [version, setVersion] = useState<ImageVersion>();
  const [historical, setHistorical] = useState(false);
  const [editDirty, setEditDirty] = useState(false);
  const dirty = useRef(false);
  dirty.current = editDirty;
  const currentLocation = useRef(location);
  currentLocation.current = location;
  const params = new URLSearchParams(location);
  const selected = params.get('asset');
  const draftKey = `gallery.draft.${library}.${params.get('branch') ?? 'main'}.${selected ?? ''}`;
  useEffect(() => {
    if (!selected) return;
    const saved = sessionStorage.getItem(draftKey);
    if (!saved) {
      setEditValue('');
      setEditLongitude('');
      setEditDirty(false);
      return;
    }
    try {
      const draft = JSON.parse(saved) as {
        field: string;
        value: string;
        longitude: string;
      };
      if (
        ![
          'rating',
          'rejected',
          'color',
          'tags',
          'title',
          'description',
          'location',
        ].includes(draft.field)
      )
        return;
      setEditField(draft.field);
      setEditValue(draft.value);
      setEditLongitude(draft.longitude);
      setEditDirty(true);
    } catch {
      sessionStorage.removeItem(draftKey);
    }
  }, [draftKey, selected]);
  useEffect(() => {
    if (editDirty && selected)
      sessionStorage.setItem(
        draftKey,
        JSON.stringify({
          field: editField,
          value: editValue.slice(0, 16384),
          longitude: editLongitude,
        }),
      );
  }, [editDirty, editField, editValue, editLongitude, selected, draftKey]);

  function navigate(next: URLSearchParams, state: object = {}) {
    if (
      dirty.current &&
      !window.confirm(
        'Leave this image and discard the unsaved metadata draft?',
      )
    )
      return;
    setEditDirty(false);
    sessionStorage.removeItem(draftKey);
    const search = next.toString();
    window.history.replaceState(
      {
        ...window.history.state,
        galleryScroll: window.scrollY,
        gallerySearch: location,
      },
      '',
      window.location.href,
    );
    window.history.pushState(
      state,
      '',
      `${window.location.pathname}${search ? '?' + search : ''}`,
    );
    setLocation(window.location.search);
    window.dispatchEvent(new Event('gallery-navigation'));
    if (next.has('asset')) window.scrollTo(0, 0);
  }

  async function api<T>(
    path: string,
    method = 'GET',
    body?: object,
    signal?: AbortSignal,
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
      signal,
    });
    const result = (await response.json()) as T & { code?: string };
    if (response.status === 401)
      window.dispatchEvent(new Event('owner-session-expired'));
    if (!response.ok) throw new Error(result.code ?? 'Gallery request failed');
    return result;
  }

  useEffect(() => {
    const onPop = () => {
      if (
        dirty.current &&
        !window.confirm(
          'Leave this image and discard the unsaved metadata draft?',
        )
      ) {
        window.history.pushState(
          window.history.state,
          '',
          `${window.location.pathname}${currentLocation.current}`,
        );
        return;
      }
      setEditDirty(false);
      setLocation(window.location.search);
    };
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (dirty.current) {
        event.preventDefault();
        event.returnValue = '';
      }
    };
    const guard = (event: Event) => {
      if (dirty.current) {
        event.preventDefault();
        window.alert(
          'Save or discard your unsaved metadata draft before switching branches.',
        );
      }
    };
    const signOut = () => {
      dirty.current = false;
      setEditDirty(false);
    };
    window.addEventListener('popstate', onPop);
    window.addEventListener('beforeunload', beforeUnload);
    window.addEventListener('gallery-guard', guard);
    window.addEventListener('gallery-sign-out', signOut);
    return () => {
      window.removeEventListener('popstate', onPop);
      window.removeEventListener('beforeunload', beforeUnload);
      window.removeEventListener('gallery-guard', guard);
      window.removeEventListener('gallery-sign-out', signOut);
    };
  }, []);
  useEffect(() => {
    const abort = new AbortController();
    const values = new URLSearchParams(location);
    setDraft(
      Object.fromEntries(
        [
          ...filterFields,
          { key: 'rejected', label: 'Rejected' },
          { key: 'locationPresent', label: 'Location present' },
        ].map((field) => [
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
    setHistorical(false);
    setVersion(undefined);
    if (values.has('asset')) {
      const asset = values.get('asset');
      setDetail(undefined);
      void api<Detail>(
        `/libraries/${encodeURIComponent(library)}/gallery/${encodeURIComponent(asset ?? '')}?branch=${encodeURIComponent(values.get('branch') ?? 'main')}`,
        'GET',
        undefined,
        abort.signal,
      )
        .then((value) => {
          if (!abort.signal.aborted) setDetail(value);
        })
        .catch((reason) => {
          if (!abort.signal.aborted) setError(String(reason));
        });
    } else {
      values.delete('asset');
      values.delete('history');
      values.delete('galleryStart');
      setPage(undefined);
      void api<Page>(
        `/libraries/${encodeURIComponent(library)}/gallery?${values}`,
        'GET',
        undefined,
        abort.signal,
      )
        .then((value) => {
          if (!abort.signal.aborted) {
            setPage(value);
            const scroll = window.history.state?.galleryScroll;
            if (typeof scroll === 'number')
              window.requestAnimationFrame(() => {
                window.scrollTo(0, scroll);
                const id = window.history.state?.galleryFocus;
                if (id)
                  document.getElementById(id)?.focus({ preventScroll: true });
              });
          }
        })
        .catch((reason) => {
          if (!abort.signal.aborted) setError(String(reason));
        });
    }
    return () => abort.abort();
  }, [baseUrl, token, library, location, refresh]);
  useEffect(() => {
    void api<Preference>(
      `/libraries/${encodeURIComponent(library)}/gallery/preferences`,
    )
      .then(setPreference)
      .catch((reason) => setError(String(reason)));
  }, [baseUrl, token, library]);

  function applyFilters() {
    for (const [from, to] of [
      ['captureFrom', 'captureTo'],
      ['importFrom', 'importTo'],
      ...[
        'focalLength',
        'aperture',
        'shutter',
        'iso',
        'width',
        'height',
        'latitude',
        'longitude',
      ].map((key) => [key + 'Min', key + 'Max']),
    ]) {
      if (
        draft[from] &&
        draft[to] &&
        (from.endsWith('From')
          ? new Date(draft[from]).getTime() > new Date(draft[to]).getTime()
          : Number(draft[from]) > Number(draft[to]))
      ) {
        setFilterError(
          `${filterFields.find((field) => field.key === from)?.label}: minimum must not exceed maximum.`,
        );
        return;
      }
    }
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
    next.delete('galleryStart');
    next.delete('asset');
    navigate(next);
    setFiltersOpen(false);
  }
  async function run(action: () => Promise<void>) {
    const startedAt = currentLocation.current;
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (reason) {
      if (startedAt === currentLocation.current) setError(String(reason));
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
    const startedAt = currentLocation.current;
    const updated = await api<Detail>(
      `/libraries/${encodeURIComponent(library)}/gallery/${encodeURIComponent(selected)}?branch=${encodeURIComponent(params.get('branch') ?? 'main')}`,
    );
    if (startedAt === currentLocation.current) setDetail(updated);
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
      setEditDirty(false);
      sessionStorage.removeItem(draftKey);
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

  function backToGallery() {
    const next = new URLSearchParams(location);
    next.delete('asset');
    next.delete('history');
    if (
      dirty.current &&
      !window.confirm(
        'Leave this image and discard the unsaved metadata draft?',
      )
    )
      return;
    setEditDirty(false);
    if (window.history.state?.galleryDetail) window.history.back();
    else navigate(next);
  }
  function openHistory() {
    const next = new URLSearchParams(location);
    next.set('history', '1');
    navigate(next, { galleryHistory: true });
  }
  function openFilters() {
    const values = new URLSearchParams(location);
    setDraft(
      Object.fromEntries(
        [...filterFields, { key: 'rejected' }, { key: 'locationPresent' }].map(
          (field) => {
            const definition = filterFields.find(
              (item) => item.key === field.key,
            );
            return [
              field.key,
              definition?.type === 'datetime-local'
                ? localDateInput(values.get(field.key) ?? '')
                : (values.get(field.key) ?? ''),
            ];
          },
        ),
      ),
    );
    setFilterError('');
    setFiltersOpen(true);
  }
  const hidesLocation = preference?.hideGeolocation !== false;
  const common = [
    'captureFrom',
    'captureTo',
    'rating',
    'camera',
    'fileType',
    'tag',
    'color',
  ];
  const activeKeys = [
    ...filterFields.map((field) => field.key),
    'rejected',
    'locationPresent',
  ].filter(
    (key) =>
      params.has(key) &&
      (!hidesLocation ||
        (!key.startsWith('latitude') &&
          !key.startsWith('longitude') &&
          key !== 'locationPresent')),
  );
  const renderFilter = (field: {
    key: string;
    label: string;
    type?: string;
  }) => (
    <label key={field.key}>
      {field.label}
      {field.key === 'rating' ? (
        <select
          aria-label="Rating"
          value={draft.rating ?? ''}
          onChange={(event) =>
            setDraft({ ...draft, rating: event.target.value })
          }
        >
          <option value="">Any rating</option>
          {[0, 1, 2, 3, 4, 5].map((rating) => (
            <option key={rating} value={rating}>
              {rating} stars (exact)
            </option>
          ))}
        </select>
      ) : (
        <input
          type={field.type ?? 'text'}
          step={field.type === 'number' ? 'any' : undefined}
          value={draft[field.key] ?? ''}
          onChange={(event) => {
            setFilterError('');
            setDraft({ ...draft, [field.key]: event.target.value });
          }}
        />
      )}
    </label>
  );
  const searchScope = params.has('title')
    ? 'title'
    : params.has('description')
      ? 'description'
      : 'path';
  const metadataLabel = (name: string) =>
    ({
      capturedAt: 'Capture date',
      focalLength: 'Focal length',
      shutter: 'Exposure (seconds)',
      iso: 'ISO',
      fileType: 'File type',
    })[name] ??
    name
      .replace(/([A-Z])/g, ' $1')
      .replace(/^./, (letter) => letter.toUpperCase());
  const filename = detail?.path.split('/').at(-1) ?? '';
  const title =
    detail?.fields.find(
      (field) => field.name === 'title' && field.state === 'present',
    )?.text ?? filename;
  const rating = detail?.fields.find(
    (field) => field.name === 'rating',
  )?.number;
  const renderFields = (fields: Field[]) => (
    <dl className="gallery-fields">
      {fields.map((field, index) => (
        <div key={`${field.name}-${index}`}>
          <dt>{metadataLabel(field.name)}</dt>
          <dd>
            {displayedField(field)}{' '}
            <small>
              {field.source}
              {field.timestampBasis ? `; ${field.timestampBasis}` : ''}
            </small>
          </dd>
        </div>
      ))}
    </dl>
  );

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
          next.delete('galleryStart');
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
    <section
      aria-labelledby="gallery-heading"
      className={`gallery density-${density}`}
    >
      {error && (
        <p role="alert">
          {error}{' '}
          <button onClick={() => setRefresh(refresh + 1)}>Retry gallery</button>
          {selected && <button onClick={backToGallery}>Back to results</button>}
        </p>
      )}
      {selected ? (
        <>
          <div className="detail-toolbar">
            <button onClick={backToGallery}>← Back to results</button>
            <span className="muted">{filename}</span>
            <button
              className="close-detail"
              aria-label="Close image details"
              onClick={backToGallery}
            >
              ×
            </button>
          </div>
          {!detail && !error && <p role="status">Loading details…</p>}
          {detail && (
            <article className="image-detail">
              <div className="detail-preview">
                {historical ? (
                  version ? (
                    <VersionPreview
                      key={version.commitId}
                      baseUrl={baseUrl}
                      token={token}
                      library={library}
                      version={version}
                      label="Selected version"
                    />
                  ) : (
                    <p role="status">
                      Selected historical preview is loading or unavailable.
                      Current image is not substituted.
                    </p>
                  )
                ) : (
                  <>
                    <Thumbnail
                      key={`${detail.assetId}/${detail.thumbnail.requestId}`}
                      baseUrl={baseUrl}
                      token={token}
                      library={library}
                      card={{
                        assetId: detail.assetId,
                        path: detail.path,
                        filename,
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
                    <p className="muted preview-caption">
                      Current thumbnail · {detail.working.branch}
                      {detail.thumbnail.selectedVersion
                        ? ` · version ${detail.thumbnail.selectedVersion.slice(0, 12)}`
                        : ''}
                    </p>
                  </>
                )}
                <details className="preview-provenance">
                  <summary>Preview provenance and source identity</summary>
                  <p>
                    Original SHA-256: <code>{detail.originalSha256}</code>
                  </p>
                  <p>
                    Current thumbnail: {detail.thumbnail.state}. Source:{' '}
                    <code>{detail.thumbnail.sourceSha256}</code>; XMP:{' '}
                    <code>{detail.thumbnail.xmpSha256}</code>; version:{' '}
                    <code>{detail.thumbnail.selectedVersion}</code>; renderer:{' '}
                    {detail.thumbnail.rendererRelease}; artifact:{' '}
                    <code>{detail.thumbnail.rendererArtifactDigest}</code>;
                    profile: {detail.thumbnail.profile}; matrix:{' '}
                    {detail.thumbnail.matrixVersion}; completed:{' '}
                    {displayedDate(detail.thumbnail.completedAt)}.
                  </p>
                  <p>
                    Metadata projection: {detail.projectionState}
                    {detail.parseErrorCode ? ` (${detail.parseErrorCode})` : ''}
                    .
                  </p>
                </details>
              </div>
              <aside
                className="image-inspector"
                aria-label="Image metadata and history"
              >
                <h2 id="gallery-heading">{title}</h2>
                <p
                  className="image-rating"
                  aria-label={
                    rating == null ? 'Rating unknown' : `Rating ${rating} of 5`
                  }
                >
                  {rating == null
                    ? 'Unrated'
                    : '★'.repeat(rating) + '☆'.repeat(5 - rating)}
                </p>
                <p className="working-state">
                  Branch: {detail.working.branch}.{' '}
                  {detail.working.webVersion
                    ? `Web metadata change ${detail.working.webVersion} is uncommitted.`
                    : 'No web working change.'}{' '}
                  {detail.working.deviceConflictCount > 0
                    ? `${detail.working.deviceConflictCount} device conflicts require resolution.`
                    : ''}
                </p>
                {historical && (
                  <p role="status" className="version-disclosure">
                    Selected-version field metadata is unavailable from the
                    current service. Exact commit/XMP identity and preview
                    provenance appear beside the image; the following metadata
                    belongs to the current working state.
                  </p>
                )}
                <h3>{historical ? 'Current working metadata' : 'Metadata'}</h3>
                {renderFields(
                  detail.fields
                    .filter(
                      (field) =>
                        !['latitude', 'longitude', 'location'].includes(
                          field.name,
                        ) || !hidesLocation,
                    )
                    .filter((field) =>
                      [
                        'camera',
                        'lens',
                        'capturedAt',
                        'captureDate',
                        'focalLength',
                        'aperture',
                        'shutter',
                        'iso',
                        'width',
                        'height',
                        'rating',
                        'tags',
                        'title',
                      ].includes(field.name),
                    ),
                )}
                <details className="more-metadata">
                  <summary>More metadata</summary>
                  {renderFields(
                    detail.fields.filter(
                      (field) =>
                        ![
                          'camera',
                          'lens',
                          'capturedAt',
                          'captureDate',
                          'focalLength',
                          'aperture',
                          'shutter',
                          'iso',
                          'width',
                          'height',
                          'rating',
                          'tags',
                          'title',
                        ].includes(field.name) &&
                        (!hidesLocation ||
                          !['latitude', 'longitude', 'location'].includes(
                            field.name,
                          )),
                    ),
                  )}
                </details>
                <ImageHistory
                  key={`${detail.assetId}/${detail.working.branch}`}
                  baseUrl={baseUrl}
                  token={token}
                  library={library}
                  asset={detail.assetId}
                  branch={detail.working.branch}
                  onSelection={(chosen, state) => {
                    setHistorical(chosen);
                    setVersion(state);
                  }}
                  onFullHistory={openHistory}
                />
                <details className="image-actions">
                  <summary>Image actions</summary>
                  <div className="gallery-actions">
                    <button
                      disabled={busy}
                      onClick={() => void run(() => downloadOriginal(detail))}
                    >
                      Download original
                    </button>
                    <button
                      disabled={busy || !detail.thumbnail.selectedVersion}
                      onClick={() => void run(() => fullPreview(detail))}
                    >
                      Open full preview
                    </button>
                    <button
                      onClick={() => {
                        if (
                          !dirty.current ||
                          window.confirm(
                            'Keep your unsaved edit and open Library controls?',
                          )
                        )
                          onLibrary?.();
                      }}
                    >
                      Import, delete, or restore in Library controls
                    </button>
                  </div>
                </details>
                <details className="metadata-edit">
                  <summary>Edit XMP metadata</summary>{' '}
                  <form
                    onChange={() => setEditDirty(true)}
                    onSubmit={(event) => {
                      event.preventDefault();
                      void run(saveMetadata);
                    }}
                  >
                    <h4>Edit XMP metadata</h4>
                    <p>
                      Changes remain uncommitted on branch{' '}
                      {detail.working.branch}.
                    </p>
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
                          onChange={(event) =>
                            setEditLongitude(event.target.value)
                          }
                        />
                      </label>
                    )}
                    <p id="gallery-edit-help">
                      Blank rating, color, title, description, or location
                      clears that value. Separate tags with commas.
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
                  </form>
                </details>
                <details className="retained-location">
                  <summary>Retained location</summary>
                  <p className="muted">
                    Reveal private coordinates only on explicit request.
                  </p>
                  <button
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
                </details>
                {editStatus && <p role="status">{editStatus}</p>}
              </aside>
            </article>
          )}
        </>
      ) : (
        <>
          <div className="gallery-topline">
            <h2 id="gallery-heading">All photos</h2>
            <p className="gallery-count" role="status" aria-live="polite">
              {busy
                ? 'Working…'
                : page
                  ? `${new Intl.NumberFormat().format(page.count)} matching images`
                  : 'Loading gallery…'}
            </p>
            <div className="gallery-toolbar">
              <form
                key={location}
                className="gallery-search"
                onSubmit={(event) => {
                  event.preventDefault();
                  const data = new FormData(event.currentTarget);
                  const scope = String(data.get('scope'));
                  const next = new URLSearchParams(location);
                  next.delete(scope);
                  if (String(data.get('term')).trim())
                    next.set(scope, String(data.get('term')).trim());
                  next.delete('cursor');
                  next.delete('galleryStart');
                  navigate(next);
                }}
              >
                <label className="sr-only" htmlFor="photo-search">
                  Search photos
                </label>
                <input
                  id="photo-search"
                  name="term"
                  type="search"
                  placeholder="Search photos…"
                  defaultValue={params.get(searchScope) ?? ''}
                />
                <label className="sr-only" htmlFor="search-scope">
                  Search in
                </label>
                <select
                  id="search-scope"
                  name="scope"
                  defaultValue={searchScope}
                >
                  <option value="path">Filename / path</option>
                  <option value="title">Title</option>
                  <option value="description">Description</option>
                </select>
                <button type="submit" className="sr-only">
                  Search
                </button>
              </form>
              <button onClick={openFilters} aria-haspopup="dialog">
                ☷ Filters{activeKeys.length ? ` (${activeKeys.length})` : ''}
              </button>
              <label className="sr-only" htmlFor="gallery-sort">
                Sort by
              </label>
              <select
                id="gallery-sort"
                value={params.get('sort') ?? 'captureDate'}
                onChange={(event) => {
                  const next = new URLSearchParams(location);
                  next.set('sort', event.target.value);
                  next.delete('cursor');
                  next.delete('galleryStart');
                  navigate(next);
                }}
              >
                {[
                  ['captureDate', 'Capture date'],
                  ['importDate', 'Import date'],
                  ['filename', 'Filename'],
                  ['rating', 'Rating'],
                  ['dimensions', 'Dimensions'],
                ].map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
              <label className="sr-only" htmlFor="gallery-direction">
                Direction
              </label>
              <select
                id="gallery-direction"
                value={params.get('direction') ?? 'desc'}
                onChange={(event) => {
                  const next = new URLSearchParams(location);
                  next.set('direction', event.target.value);
                  next.delete('cursor');
                  next.delete('galleryStart');
                  navigate(next);
                }}
              >
                <option value="desc">Descending ↓</option>
                <option value="asc">Ascending ↑</option>
              </select>
              <label className="sr-only" htmlFor="grid-density">
                Grid density
              </label>
              <select
                id="grid-density"
                value={density}
                onChange={(event) => setDensity(event.target.value)}
              >
                <option value="comfortable">▦ Comfortable</option>
                <option value="compact">▦ Compact</option>
              </select>
            </div>
          </div>
          <div className="filter-chips" aria-label="Active filters">
            {activeKeys.map((key) => (
              <button
                key={key}
                aria-label={`Remove ${filterFields.find((field) => field.key === key)?.label ?? key} filter`}
                onClick={() => {
                  const next = new URLSearchParams(location);
                  next.delete(key);
                  next.delete('cursor');
                  next.delete('galleryStart');
                  navigate(next);
                }}
              >
                {filterFields.find((field) => field.key === key)?.label ?? key}:{' '}
                {params.get(key)} <span aria-hidden="true">×</span>
              </button>
            ))}
          </div>
          {page?.count === 0 && (
            <div className="gallery-empty">
              <h3>
                {activeKeys.length
                  ? 'No photos match these filters'
                  : 'Your library is ready for its first photos'}
              </h3>
              <p className="muted">
                {activeKeys.length
                  ? 'Try adjusting or resetting your filters.'
                  : 'Import photographs and their darktable sidecars to begin.'}
              </p>
              <button onClick={activeKeys.length ? openFilters : onLibrary}>
                {activeKeys.length ? 'Adjust filters' : 'Import photos'}
              </button>
            </div>
          )}
          {page && (
            <>
              <ul className="gallery-grid">
                {page.cards.map((card) => (
                  <li key={card.assetId}>
                    <button
                      id={`photo-${card.assetId}`}
                      className="gallery-card"
                      onClick={() => {
                        window.history.replaceState(
                          {
                            ...window.history.state,
                            galleryFocus: `photo-${card.assetId}`,
                          },
                          '',
                          window.location.href,
                        );
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
                      <span className="card-caption">
                        <span className="card-filename" title={card.filename}>
                          {card.filename}
                        </span>
                        <span
                          className="card-rating"
                          aria-label={`Rating ${card.rating ?? 'unknown'}`}
                        >
                          {card.rating == null
                            ? 'Unrated'
                            : '★'.repeat(card.rating) +
                              '☆'.repeat(5 - card.rating)}
                          {card.rejected ? ' · Rejected' : ''}
                        </span>
                      </span>
                      {card.projectionState === 'stale' && (
                        <small>Metadata refresh pending</small>
                      )}
                    </button>
                  </li>
                ))}
              </ul>
              <nav className="gallery-pagination" aria-label="Gallery pages">
                <span>
                  {params.has('cursor') && !params.has('galleryStart')
                    ? `This page: ${page.cards.length} of ${page.count}`
                    : `${page.cards.length ? Number(params.get('galleryStart') ?? 0) + 1 : 0}–${Number(params.get('galleryStart') ?? 0) + page.cards.length} of ${new Intl.NumberFormat().format(page.count)}`}
                </span>
                <button
                  disabled={!params.has('cursor')}
                  onClick={() => {
                    const previous = window.history.state?.previousSearch;
                    if (typeof previous === 'string')
                      navigate(new URLSearchParams(previous));
                    else {
                      const next = new URLSearchParams(location);
                      next.delete('cursor');
                      next.delete('galleryStart');
                      navigate(next);
                    }
                  }}
                >
                  Previous page
                </button>
                <button
                  disabled={!page.nextCursor}
                  onClick={() => {
                    const next = new URLSearchParams(location);
                    next.set('cursor', page.nextCursor!);
                    if (!params.has('cursor') || params.has('galleryStart'))
                      next.set(
                        'galleryStart',
                        String(
                          Number(params.get('galleryStart') ?? 0) +
                            page.cards.length,
                        ),
                      );
                    navigate(next, { previousSearch: location });
                  }}
                >
                  Next page
                </button>
              </nav>
            </>
          )}
          {preference && (
            <details className="gallery-preferences">
              <summary>Gallery privacy</summary>
              <button
                disabled={busy}
                onClick={() => void run(updatePreference)}
              >
                {preference.hideGeolocation
                  ? 'Show location in gallery'
                  : 'Hide location in gallery'}
              </button>
            </details>
          )}
          {filtersOpen && (
            <Modal
              title="Filters"
              className="filter-drawer"
              onClose={() => setFiltersOpen(false)}
            >
              <form
                onSubmit={(event) => {
                  event.preventDefault();
                  applyFilters();
                }}
              >
                <div className="filter-common">
                  {filterFields
                    .filter((field) => common.includes(field.key))
                    .map(renderFilter)}
                </div>
                <p className="muted filter-help">
                  Criteria combine with AND. Tags are comma-separated; rating is
                  an exact value.
                </p>
                <details className="filter-advanced">
                  <summary>More filters</summary>
                  <div className="gallery-filters">
                    {filterFields
                      .filter(
                        (field) =>
                          !common.includes(field.key) &&
                          (!hidesLocation ||
                            (!field.key.startsWith('latitude') &&
                              !field.key.startsWith('longitude'))),
                      )
                      .map(renderFilter)}
                    <label>
                      Rejected
                      <select
                        aria-label="Rejected"
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
                    {!hidesLocation && (
                      <label>
                        Location present
                        <select
                          aria-label="Location present"
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
                </details>
                {filterError && <p role="alert">{filterError}</p>}
                <div className="filter-footer">
                  <button
                    type="button"
                    onClick={() => {
                      setDraft({});
                      setFilterError('');
                    }}
                  >
                    Reset
                  </button>
                  <button className="primary" type="submit">
                    Show photos
                  </button>
                </div>
              </form>
            </Modal>
          )}
        </>
      )}
    </section>
  );
}
