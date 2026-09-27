import { useEffect, useState } from 'react';
import { Comparison, type ComparisonResult } from './Comparison';
import { MergeReview } from './MergeReview';
import { BranchSwitcher } from './BranchSwitcher';
import { HistoryActions } from './HistoryActions';
import './lineage.css';

type Commit = {
  commitId: string;
  parents: string[];
  authorId: string;
  authoredAt: string;
  message: string;
  hidden: boolean;
};
type Node = {
  commit: Commit;
  branches: string[];
  tags: string[];
  hiddenParentIds: string[];
};
type HistoryPage = {
  assetId: string;
  branch: string;
  headCommitId: string;
  offset: number;
  nextOffset: number | null;
  nodes: Node[];
  hasHiddenHistory: boolean;
};

export function Lineage({
  baseUrl,
  token,
  library,
  asset,
  branch,
  onBack,
  onSwitchBranch,
}: {
  baseUrl: string;
  token: string;
  library: string;
  asset: string;
  branch: string;
  onBack: () => void;
  onSwitchBranch: (branch: string) => void;
}) {
  const [offset, setOffset] = useState(0);
  const [showHidden, setShowHidden] = useState(false);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState<HistoryPage>();
  const [selected, setSelected] = useState('');
  const [error, setError] = useState('');
  const [target, setTarget] = useState('');
  const [source, setSource] = useState('');
  const [comparison, setComparison] = useState<ComparisonResult>();
  const [compareError, setCompareError] = useState('');
  const [comparing, setComparing] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [mutationsEnabled, setMutationsEnabled] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/capabilities`,
      {
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
        signal: abort.signal,
      },
    )
      .then(async (response) => {
        if (!response.ok) throw new Error('History capabilities unavailable');
        return response.json() as Promise<{ mutationsEnabled: boolean }>;
      })
      .then((value) => setMutationsEnabled(value.mutationsEnabled))
      .catch(() => {
        if (!abort.signal.aborted) setMutationsEnabled(false);
      });
    return () => abort.abort();
  }, [baseUrl, token, library]);

  useEffect(() => {
    const abort = new AbortController();
    const query = new URLSearchParams({
      branch,
      offset: String(offset),
      limit: '100',
      showHidden: String(showHidden),
    });
    setPage(undefined);
    setError('');
    fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/images/${encodeURIComponent(asset)}/history?${query}`,
      {
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
        signal: abort.signal,
      },
    )
      .then(async (response) => {
        const body = await response.json();
        if (!response.ok)
          throw new Error(body.code ?? 'Image history is unavailable');
        return body as HistoryPage;
      })
      .then((history) => {
        setPage(history);
        setSelected(
          (current) => current || history.nodes[0]?.commit.commitId || '',
        );
        setTarget(
          (current) => current || history.nodes[0]?.commit.commitId || '',
        );
        setSource(
          (current) => current || history.nodes[1]?.commit.commitId || '',
        );
        setComparison(undefined);
      })
      .catch((reason) => {
        if (!abort.signal.aborted)
          setError(
            reason instanceof Error
              ? reason.message
              : 'Image history is unavailable',
          );
      });
    return () => abort.abort();
  }, [asset, baseUrl, branch, library, offset, showHidden, token, refresh]);

  const filtered = page?.nodes.filter((node) => {
    const term = search.trim().toLocaleLowerCase();
    return (
      !term ||
      [
        node.commit.commitId,
        node.commit.message,
        node.commit.authorId,
        ...node.branches,
        ...node.tags,
      ].some((value) => value.toLocaleLowerCase().includes(term))
    );
  });
  const selectedNode = page?.nodes.find(
    (node) => node.commit.commitId === selected,
  );
  const validCommit = (value: string) => /^[0-9a-f]{64}$/.test(value);

  async function compareVersions() {
    if (!validCommit(target) || !validCommit(source)) return;
    setComparing(true);
    setCompareError('');
    setComparison(undefined);
    try {
      const query = new URLSearchParams({ target, source });
      const response = await fetch(
        `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage/images/${encodeURIComponent(asset)}/compare?${query}`,
        { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' },
      );
      const body = (await response.json()) as ComparisonResult & {
        code?: string;
      };
      if (!response.ok)
        throw new Error(body.code ?? 'Version comparison is unavailable');
      setComparison(body);
    } catch (reason) {
      setCompareError(
        reason instanceof Error
          ? reason.message
          : 'Version comparison is unavailable',
      );
    } finally {
      setComparing(false);
    }
  }

  return (
    <section className="lineage" aria-labelledby="lineage-heading">
      <button type="button" onClick={onBack}>
        Back to image details
      </button>
      <h3 id="lineage-heading">Image lineage</h3>
      {!mutationsEnabled && (
        <p role="status">
          History changes are temporarily unavailable. Reading and review remain
          available.
        </p>
      )}
      <p>
        Image <code>{asset}</code> on branch <strong>{branch}</strong>. This is
        an image-filtered view of library-wide commits; parent IDs remain the
        exact library history. A parent ID absent from this view is an unshown
        library commit.
      </p>
      {page && (
        <p>
          Branch head: <code>{page.headCommitId}</code>
        </p>
      )}
      <label>
        <input
          type="checkbox"
          checked={showHidden}
          onChange={(event) => {
            setShowHidden(event.target.checked);
            setOffset(0);
          }}
        />{' '}
        Show hidden commits
      </label>
      {error && <p role="alert">{error}</p>}
      {!page && !error && <p role="status">Loading image history…</p>}
      {page && (
        <>
          {page.hasHiddenHistory && !showHidden && (
            <p role="status">
              Hidden image history exists on this branch. Show hidden commits to
              inspect exact versions.
            </p>
          )}
          <h4>Lineage graph</h4>
          {page.nodes.length === 0 ? (
            <p>No committed image history is available on this branch.</p>
          ) : (
            <ol className="lineage-graph" aria-label="Image lineage graph">
              {page.nodes.map((node) => (
                <li key={node.commit.commitId}>
                  <button
                    type="button"
                    aria-pressed={selected === node.commit.commitId}
                    onClick={() => setSelected(node.commit.commitId)}
                  >
                    <strong>{node.commit.message}</strong>{' '}
                    <code>{node.commit.commitId}</code>
                  </button>
                  <span>
                    {' '}
                    ← parent: {node.commit.parents.join(', ') || 'none'}
                  </span>
                  {node.hiddenParentIds.length > 0 && (
                    <span>
                      {' '}
                      Hidden ancestry: {node.hiddenParentIds.join(', ')}
                    </span>
                  )}
                </li>
              ))}
            </ol>
          )}
          <h4>Searchable history table</h4>
          <label>
            Search loaded history
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
          </label>
          <div className="lineage-table-scroll">
            <table>
              <caption>
                Exact commits and parent relationships for this image
              </caption>
              <thead>
                <tr>
                  <th scope="col">Commit</th>
                  <th scope="col">Message</th>
                  <th scope="col">Parents</th>
                  <th scope="col">Author and time</th>
                  <th scope="col">Branches and tags</th>
                  <th scope="col">Visibility</th>
                </tr>
              </thead>
              <tbody>
                {filtered?.map((node) => (
                  <tr key={node.commit.commitId}>
                    <td>
                      <button
                        type="button"
                        aria-pressed={selected === node.commit.commitId}
                        onClick={() => setSelected(node.commit.commitId)}
                      >
                        {node.commit.commitId}
                      </button>
                    </td>
                    <td>{node.commit.message}</td>
                    <td>{node.commit.parents.join(', ') || 'none'}</td>
                    <td>
                      {node.commit.authorId},{' '}
                      {new Intl.DateTimeFormat(undefined, {
                        dateStyle: 'medium',
                        timeStyle: 'short',
                      }).format(new Date(node.commit.authoredAt))}
                    </td>
                    <td>
                      {[...node.branches, ...node.tags].join(', ') || 'none'}
                    </td>
                    <td>
                      {node.commit.hidden ? 'Hidden' : 'Visible'}
                      {node.hiddenParentIds.length > 0
                        ? '; hidden ancestry'
                        : ''}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {selectedNode && (
            <p role="status">
              Selected committed version{' '}
              <code>{selectedNode.commit.commitId}</code>.
            </p>
          )}
          <section aria-labelledby="choose-comparison-heading">
            <h4 id="choose-comparison-heading">
              Compare two committed versions
            </h4>
            <label>
              Target version
              <input
                type="text"
                list="lineage-commit-options"
                pattern="[0-9a-f]{64}"
                value={target}
                onChange={(event) => setTarget(event.target.value)}
              />
            </label>{' '}
            <label>
              Source version
              <input
                type="text"
                list="lineage-commit-options"
                pattern="[0-9a-f]{64}"
                value={source}
                onChange={(event) => setSource(event.target.value)}
              />
            </label>{' '}
            <datalist id="lineage-commit-options">
              {page.nodes.map((node) => (
                <option key={node.commit.commitId} value={node.commit.commitId}>
                  {node.commit.message}
                </option>
              ))}
            </datalist>
            <button
              type="button"
              disabled={
                !validCommit(target) || !validCommit(source) || comparing
              }
              onClick={() => void compareVersions()}
            >
              Compare versions
            </button>
            {compareError && <p role="alert">{compareError}</p>}
            {comparing && <p role="status">Comparing versions…</p>}
          </section>
          {comparison && (
            <Comparison
              value={comparison}
              baseUrl={baseUrl}
              token={token}
              library={library}
            />
          )}
          <MergeReview
            baseUrl={baseUrl}
            token={token}
            library={library}
            targetBranch={branch}
            targetHead={page.headCommitId}
            mutationsEnabled={mutationsEnabled}
            onPublished={() => {
              setOffset(0);
              setRefresh((value) => value + 1);
            }}
          />
          <BranchSwitcher
            baseUrl={baseUrl}
            token={token}
            library={library}
            branch={branch}
            mutationsEnabled={mutationsEnabled}
            onSwitched={onSwitchBranch}
          />
          <HistoryActions
            baseUrl={baseUrl}
            token={token}
            library={library}
            branch={branch}
            head={page.headCommitId}
            selected={selected}
            hidden={selectedNode?.commit.hidden ?? false}
            mutationsEnabled={mutationsEnabled}
            onChanged={() => setRefresh((value) => value + 1)}
          />
          <nav aria-label="Lineage pages">
            <button
              type="button"
              disabled={offset === 0}
              onClick={() => setOffset(Math.max(0, offset - 100))}
            >
              Previous history page
            </button>{' '}
            <button
              type="button"
              disabled={page.nextOffset === null}
              onClick={() => setOffset(page.nextOffset!)}
            >
              Next history page
            </button>
          </nav>
        </>
      )}
    </section>
  );
}
