import { useEffect, useState } from 'react';
import { addToDefaultBasket } from './export-api';
import { exportText } from './export-messages';
import type { ImageVersion } from './Comparison';
import { platformConfig } from './platform-config';

type Commit = {
  commitId: string;
  parents: string[];
  message: string;
  authoredAt: string;
  hidden: boolean;
  changes: { assetId: string; path: string | null }[];
};

export function LibraryHistory({
  baseUrl,
  token,
  library,
  branch,
  onImage,
}: {
  baseUrl: string;
  token: string;
  library: string;
  branch: string;
  onImage: (asset: string) => void;
}) {
  const [commits, setCommits] = useState<Commit[]>();
  const [error, setError] = useState('');
  const [exportStatus, setExportStatus] = useState('');
  const [offset, setOffset] = useState(0);
  const [showHidden, setShowHidden] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    setCommits(undefined);
    setError('');
    fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control/graph/${encodeURIComponent(branch)}?showHidden=${showHidden}&offset=${offset}&limit=25`,
      {
        headers: { Authorization: `Bearer ${token}` },
        signal: abort.signal,
        cache: 'no-store',
      },
    )
      .then(async (response) => {
        if (response.status === 401)
          window.dispatchEvent(new Event('owner-session-expired'));
        if (!response.ok) throw new Error('Library history unavailable.');
        return response.json() as Promise<Commit[]>;
      })
      .then((value) => {
        if (!abort.signal.aborted) setCommits(value);
      })
      .catch((reason) => {
        if (!abort.signal.aborted) setError(String(reason));
      });
    return () => abort.abort();
  }, [baseUrl, token, library, branch, offset, showHidden, refresh]);
  return (
    <section
      className="secondary-view"
      aria-labelledby="library-history-heading"
    >
      <h2 id="library-history-heading">Library history</h2>
      <p className="muted">
        Library-wide commits on {branch}. Open an affected image to inspect its
        complete lineage and version-control tools.
      </p>
      <label>
        <input
          type="checkbox"
          checked={showHidden}
          onChange={(event) => {
            setShowHidden(event.target.checked);
            setOffset(0);
          }}
        />{' '}
        Show hidden library commits
      </label>
      {exportStatus && <p role="status">{exportStatus}</p>}
      {error && (
        <p role="alert">
          {error}{' '}
          <button onClick={() => setRefresh(refresh + 1)}>Retry history</button>
        </p>
      )}
      {!commits && !error && <p role="status">Loading library history…</p>}
      {commits?.length === 0 && (
        <p>No committed library history on this branch.</p>
      )}
      {commits && (
        <ul className="history-list">
          {commits.map((commit) => (
            <li key={commit.commitId}>
              <h3>{commit.message}</h3>
              <p>
                <time>{new Date(commit.authoredAt).toLocaleString()}</time> ·{' '}
                {commit.hidden ? 'Hidden' : 'Committed'}
              </p>
              <details>
                <summary>Commit and parent identities</summary>
                <code>{commit.commitId}</code>
                <p>Parents: {commit.parents.join(', ') || 'Original commit'}</p>
              </details>
              <div className="gallery-actions">
                <button
                  hidden={!platformConfig()?.exportsEnabled}
                  onClick={() => {
                    void (async () => {
                      for (const assetId of new Set(
                        commit.changes.map((c) => c.assetId),
                      )) {
                        const response = await fetch(
                          `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/images/${encodeURIComponent(assetId)}/compare?target=${encodeURIComponent(commit.commitId)}&source=${encodeURIComponent(commit.commitId)}`,
                          {
                            headers: { Authorization: `Bearer ${token}` },
                            cache: 'no-store',
                          },
                        );
                        if (!response.ok)
                          throw new Error(
                            exportText('Committed image states unavailable.'),
                          );
                        const data = (await response.json()) as {
                          targetStates: ImageVersion[];
                        };
                        for (const state of data.targetStates.filter((s) =>
                          commit.changes.some((c) => c.assetId === s.assetId),
                        )) {
                          if (state.editId)
                            await addToDefaultBasket(baseUrl, token, library, {
                              assetId: state.assetId,
                              editId: state.editId,
                              selectedVersion: commit.commitId,
                              advancedHistory: showHidden && commit.hidden,
                            });
                        }
                      }
                      setExportStatus(
                        exportText(
                          'Added exact committed image versions to the default export basket.',
                        ),
                      );
                    })().catch((error) => setError(error.message));
                  }}
                >
                  {exportText('Add changed image versions to basket')}
                  {commit.hidden
                    ? exportText(' (advanced hidden history)')
                    : ''}
                </button>
                {Array.from(
                  new Map(
                    commit.changes.map((change) => [change.assetId, change]),
                  ).values(),
                ).map((change) => (
                  <button
                    key={change.assetId}
                    onClick={() => onImage(change.assetId)}
                  >
                    History for {change.path ?? change.assetId}
                  </button>
                ))}
              </div>
            </li>
          ))}
        </ul>
      )}
      <nav aria-label="Library history pages">
        <button
          disabled={offset === 0}
          onClick={() => setOffset(Math.max(0, offset - 25))}
        >
          Previous commits
        </button>{' '}
        <button
          disabled={!commits || commits.length < 25}
          onClick={() => setOffset(offset + 25)}
        >
          More commits
        </button>
      </nav>
    </section>
  );
}
