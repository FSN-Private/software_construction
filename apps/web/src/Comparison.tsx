import { useEffect, useState } from 'react';

export type ImageVersion = {
  commitId: string;
  assetId: string;
  editId: string | null;
  xmpSha256: string | null;
  path: string | null;
  lifecycle: string | null;
  previewState: string;
  previewRequestId: string | null;
  previewProvenance: Provenance | null;
};
type Provenance = {
  sourceSha256: string;
  xmpSha256: string;
  selectedVersion: string;
  rendererRelease: string;
  rendererArtifactDigest: string;
  profile: string;
  compatibilityMatrix: string;
  completedAtUtc: string;
};
type Difference = {
  assetId: string;
  editId: string | null;
  baseContentSha256: string | null;
  oursContentSha256: string | null;
  theirsContentSha256: string | null;
  basePath: string | null;
  oursPath: string | null;
  theirsPath: string | null;
  baseLifecycle: string | null;
  oursLifecycle: string | null;
  theirsLifecycle: string | null;
};
export type ComparisonResult = {
  targetCommitId: string;
  sourceCommitId: string;
  baseCommitId: string | null;
  targetStates: ImageVersion[];
  sourceStates: ImageVersion[];
  differences: Difference[];
  metadataDifferences?: {
    editId: string | null;
    state: string;
    fields: {
      field: string;
      base: string | null;
      target: string | null;
      source: string | null;
      requiresResolution: boolean;
    }[];
  }[];
};
type PreviewStatus = {
  requestId: string;
  state: string;
  provenance: Provenance | null;
  failureCode: string | null;
};

export function VersionPreview({
  baseUrl,
  token,
  library,
  version,
  label,
  expectedSourceSha256,
}: {
  baseUrl: string;
  token: string;
  library: string;
  version: ImageVersion;
  label: string;
  expectedSourceSha256?: string;
}) {
  const [status, setStatus] = useState<PreviewStatus>({
    requestId: version.previewRequestId ?? '',
    state: version.previewState,
    provenance: version.previewProvenance,
    failureCode: null,
  });
  const [locator, setLocator] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const exact =
    status.provenance?.selectedVersion === version.commitId &&
    status.provenance.xmpSha256 === version.xmpSha256 &&
    (!expectedSourceSha256 ||
      status.provenance.sourceSha256 === expectedSourceSha256);
  useEffect(() => {
    if (status.state !== 'completed' || !exact || !status.requestId) return;
    const controller = new AbortController();
    fetch(
      `${baseUrl}/v1/renditions/${status.requestId}/derivative?library=${encodeURIComponent(library)}`,
      {
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
        signal: controller.signal,
      },
    )
      .then(async (response) => {
        if (!response.ok) throw new Error('Preview unavailable');
        return (await response.json()) as { locator: string };
      })
      .then((body) => setLocator(body.locator))
      .catch(() => {
        if (!controller.signal.aborted) setError('Preview unavailable');
      });
    return () => controller.abort();
  }, [baseUrl, exact, library, status.requestId, status.state, token]);

  async function requestOrRefresh() {
    setBusy(true);
    setError('');
    try {
      const refreshing = !!status.requestId;
      const response = await fetch(
        refreshing
          ? `${baseUrl}/v1/renditions/${status.requestId}?library=${encodeURIComponent(library)}`
          : `${baseUrl}/v1/renditions`,
        {
          method: refreshing ? 'GET' : 'POST',
          headers: {
            Authorization: `Bearer ${token}`,
            ...(!refreshing ? { 'Content-Type': 'application/json' } : {}),
          },
          body: refreshing
            ? undefined
            : JSON.stringify({
                assetId: version.assetId,
                editId: version.editId,
                selectedVersion: version.commitId,
                rendition: 'preview-2048',
                profile: 'render-v1',
                libraryId: library,
              }),
          cache: 'no-store',
        },
      );
      const body = (await response.json()) as PreviewStatus & { code?: string };
      if (!response.ok) throw new Error(body.code ?? 'Preview unavailable');
      setStatus(body);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Preview unavailable',
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label={`${label} preview`}>
      <p>
        {label} commit <code>{version.commitId}</code>; XMP{' '}
        <code>{version.xmpSha256 ?? 'none'}</code>.
      </p>
      <p role="status">
        Preview:{' '}
        {exact || status.state !== 'completed' ? status.state : 'unavailable'}
        {status.failureCode ? ` (${status.failureCode})` : ''}.
      </p>
      {version.editId && version.xmpSha256 && (
        <button
          type="button"
          disabled={busy || (status.state === 'completed' && exact)}
          onClick={() => void requestOrRefresh()}
        >
          {status.requestId
            ? 'Refresh preview status'
            : 'Request exact preview'}
        </button>
      )}
      {error && <p role="alert">{error}</p>}
      {locator && exact && (
        <img src={locator} alt={`${label} rendering of the selected image`} />
      )}
      {exact && status.provenance && (
        <p>
          Renderer {status.provenance.rendererRelease}; artifact{' '}
          <code>{status.provenance.rendererArtifactDigest}</code>; profile{' '}
          {status.provenance.profile}; completed{' '}
          {new Intl.DateTimeFormat(undefined, {
            dateStyle: 'medium',
            timeStyle: 'short',
          }).format(new Date(status.provenance.completedAtUtc))}
          .
        </p>
      )}
    </section>
  );
}

export function Comparison({
  value,
  baseUrl,
  token,
  library,
  showPreviews = true,
}: {
  value: ComparisonResult;
  baseUrl: string;
  token: string;
  library: string;
  showPreviews?: boolean;
}) {
  return (
    <section aria-labelledby="comparison-heading">
      <h4 id="comparison-heading">Version comparison</h4>
      <p>
        Target <code>{value.targetCommitId}</code>; source{' '}
        <code>{value.sourceCommitId}</code>; common ancestor{' '}
        <code>{value.baseCommitId ?? 'none'}</code>.
      </p>
      {showPreviews && (
        <div className="lineage-comparison">
          {(
            [
              ['Target', value.targetStates],
              ['Source', value.sourceStates],
            ] as const
          ).map(([label, states]) => (
            <div key={label}>
              <h5>{label} image state</h5>
              {states.length === 0 ? (
                <p>Image state absent at this commit.</p>
              ) : (
                states.map((state) => (
                  <VersionPreview
                    key={`${state.commitId}/${state.editId ?? 'source'}`}
                    baseUrl={baseUrl}
                    token={token}
                    library={library}
                    version={state}
                    label={label}
                  />
                ))
              )}
            </div>
          ))}
        </div>
      )}
      <h5>Committed differences</h5>
      {value.metadataDifferences?.map((item) => (
        <section
          key={item.editId ?? 'source'}
          aria-label={`XMP fields for ${item.editId ?? 'source'}`}
        >
          <h6>XMP fields for {item.editId ?? 'source'}</h6>
          {item.state !== 'available' && <p>Metadata state: {item.state}.</p>}
          {item.fields.length > 0 ? (
            <dl>
              {item.fields.map((field) => (
                <div key={field.field}>
                  <dt>{field.field}</dt>
                  <dd>Ancestor: {field.base ?? 'absent'}</dd>
                  <dd>Target: {field.target ?? 'absent'}</dd>
                  <dd>Source: {field.source ?? 'absent'}</dd>
                </div>
              ))}
            </dl>
          ) : (
            <p>No recognized XMP field differences.</p>
          )}
        </section>
      ))}
      {value.differences.length === 0 ? (
        <p>No XMP, path, or lifecycle differences for this image.</p>
      ) : (
        <table>
          <caption>Exact image differences between selected commits</caption>
          <thead>
            <tr>
              <th scope="col">Edit</th>
              <th scope="col">Ancestor XMP</th>
              <th scope="col">Target XMP</th>
              <th scope="col">Source XMP</th>
              <th scope="col">Path and lifecycle</th>
            </tr>
          </thead>
          <tbody>
            {value.differences.map((difference) => (
              <tr key={`${difference.assetId}/${difference.editId}`}>
                <td>{difference.editId ?? 'original'}</td>
                <td>{difference.baseContentSha256 ?? 'none'}</td>
                <td>{difference.oursContentSha256 ?? 'none'}</td>
                <td>{difference.theirsContentSha256 ?? 'none'}</td>
                <td>
                  Target: {difference.oursPath ?? 'none'} /{' '}
                  {difference.oursLifecycle ?? 'none'}; source:{' '}
                  {difference.theirsPath ?? 'none'} /{' '}
                  {difference.theirsLifecycle ?? 'none'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
