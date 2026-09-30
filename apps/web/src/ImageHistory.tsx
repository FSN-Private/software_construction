import { useEffect, useRef, useState } from 'react';
import {
  Comparison,
  type ComparisonResult,
  type ImageVersion,
} from './Comparison';

type Node = {
  commit: {
    commitId: string;
    parents: string[];
    message: string;
    authoredAt: string;
    hidden: boolean;
  };
  branches: string[];
  tags: string[];
  hiddenParentIds: string[];
};
type History = {
  nodes: Node[];
  headCommitId: string;
  hasHiddenHistory: boolean;
  nextOffset: number | null;
};

export function ImageHistory({
  baseUrl,
  token,
  library,
  asset,
  branch,
  onSelection,
  onFullHistory,
  initialCommit,
}: {
  baseUrl: string;
  token: string;
  library: string;
  asset: string;
  branch: string;
  onSelection: (historical: boolean, version?: ImageVersion) => void;
  onFullHistory: () => void;
  initialCommit?: string;
}) {
  const [history, setHistory] = useState<History>();
  const [selected, setSelected] = useState('');
  const [compareSource, setCompareSource] = useState('');
  const [comparison, setComparison] = useState<ComparisonResult>();
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const selectionRequest = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    const abort = new AbortController();
    setHistory(undefined);
    setError('');
    setSelected('');
    setComparison(undefined);
    fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/images/${encodeURIComponent(asset)}/history?branch=${encodeURIComponent(branch)}&offset=0&limit=5&showHidden=false`,
      {
        headers: { Authorization: `Bearer ${token}` },
        signal: abort.signal,
        cache: 'no-store',
      },
    )
      .then(async (response) => {
        if (response.status === 401)
          window.dispatchEvent(new Event('owner-session-expired'));
        if (!response.ok) throw new Error('Image history unavailable.');
        return response.json() as Promise<History>;
      })
      .then((value) => {
        if (!abort.signal.aborted) setHistory(value);
      })
      .catch((reason) => {
        if (!abort.signal.aborted) setError(String(reason));
      });
    return () => {
      abort.abort();
      selectionRequest.current?.abort();
    };
  }, [baseUrl, token, library, asset, branch, refresh]);

  async function select(
    commitId: string,
    sourceCommitId = history?.headCommitId ?? '',
  ) {
    selectionRequest.current?.abort();
    setSelected(commitId);
    setCompareSource(sourceCommitId);
    setComparison(undefined);
    setError('');
    onSelection(!!commitId);
    if (!commitId || !history) return;
    const abort = new AbortController();
    selectionRequest.current = abort;
    try {
      const response = await fetch(
        `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/images/${encodeURIComponent(asset)}/compare?target=${encodeURIComponent(commitId)}&source=${encodeURIComponent(sourceCommitId)}`,
        {
          headers: { Authorization: `Bearer ${token}` },
          signal: abort.signal,
          cache: 'no-store',
        },
      );
      if (response.status === 401)
        window.dispatchEvent(new Event('owner-session-expired'));
      if (!response.ok)
        throw new Error(
          'Selected version unavailable. Open full history to review.',
        );
      const body = (await response.json()) as ComparisonResult;
      const exact = body.targetStates.find(
        (state) => state.assetId === asset && state.commitId === commitId,
      );
      if (!exact)
        throw new Error('Exact image state unavailable for this commit.');
      if (!abort.signal.aborted) {
        onSelection(true, exact);
        setComparison(body);
      }
    } catch (reason) {
      if (!abort.signal.aborted) setError(String(reason));
    }
  }
  useEffect(() => {
    if (history && initialCommit) void select(initialCommit);
  }, [history, initialCommit]);
  return (
    <section className="image-history" aria-labelledby="image-history-heading">
      <h3 id="image-history-heading">Version history</h3>
      <p className="muted history-caption">
        Image states in library-wide commits.
      </p>
      {error && (
        <p role="alert">
          {error}{' '}
          <button
            onClick={() =>
              selected ? void select(selected) : setRefresh(refresh + 1)
            }
          >
            Retry image history
          </button>
        </p>
      )}
      {!history && !error && <p role="status">Loading versions…</p>}
      {history && (
        <>
          <button
            className="current-state"
            aria-pressed={!selected}
            onClick={() => void select('')}
          >
            Current working view · {branch}
          </button>
          {history.nodes.length === 0 && (
            <p>No committed image history on this branch.</p>
          )}
          <ol className="history-list">
            {history.nodes.map((node) => (
              <li key={node.commit.commitId}>
                <button
                  aria-pressed={selected === node.commit.commitId}
                  onClick={() => void select(node.commit.commitId)}
                >
                  <strong>{node.commit.message}</strong>
                  <small>
                    {node.commit.commitId === history.headCommitId
                      ? `Current commit · ${branch}`
                      : new Date(node.commit.authoredAt).toLocaleString()}
                  </small>
                  {node.branches.length + node.tags.length > 0 && (
                    <small>
                      {[...node.branches, ...node.tags].join(' · ')}
                    </small>
                  )}
                </button>
                <details>
                  <summary>Version identity</summary>
                  <code>{node.commit.commitId}</code>
                  <p>
                    Parents:{' '}
                    {node.commit.parents.join(', ') || 'Original commit'}
                  </p>
                </details>
                {(node.commit.parents.length > 1 ||
                  node.hiddenParentIds.length > 0) && (
                  <small>
                    Merged or collapsed ancestry; inspect full history.
                  </small>
                )}
              </li>
            ))}
          </ol>
          {(history.hasHiddenHistory || history.nextOffset !== null) && (
            <p className="muted">
              Additional or hidden ancestry is available in full history.
            </p>
          )}
        </>
      )}
      <div className="gallery-actions">
        <button onClick={onFullHistory}>View image history</button>
      </div>
      {selected && history && (
        <div className="inline-comparison">
          <label>
            Compare selected version with
            <select
              value={compareSource}
              onChange={(event) => void select(selected, event.target.value)}
            >
              {history.nodes.map((node) => (
                <option key={node.commit.commitId} value={node.commit.commitId}>
                  {node.commit.message} · {node.commit.commitId.slice(0, 12)}
                </option>
              ))}
            </select>
          </label>
          {comparison && (
            <details>
              <summary>Version comparison</summary>
              <Comparison
                value={comparison}
                baseUrl={baseUrl}
                token={token}
                library={library}
                showPreviews={false}
              />
            </details>
          )}
        </div>
      )}
    </section>
  );
}
