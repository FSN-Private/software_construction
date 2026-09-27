import { useEffect, useState } from 'react';
import { beginRecentAuthentication } from './recent-auth';

type Ref = {
  name: string;
  kind: string;
  commitId: string;
  protected: boolean;
  version: number;
};
type Working = {
  id: string;
  assetId: string;
  editId: string | null;
  change: string;
  contentSha256: string | null;
  baseContentSha256: string | null;
  path: string | null;
  version: number;
  staged: boolean;
};
type Reflog = {
  id: string;
  refName: string;
  oldHead: string | null;
  operation: string;
  occurredAt: string;
  recoveryExpiresAt: string | null;
};
type Checkout = { deviceConflictCount: number; galleryWorkingCount: number };
type RewritePreview = {
  branch: string;
  oldHead: string | null;
  newHead: string;
  displacedCommitIds: string[];
  confirmationToken: string;
};
type PreparedStage = {
  id: string;
  head: string;
  ids: string[];
  message: string;
  authoredAt: string;
  key: string;
};

export function HistoryActions({
  baseUrl,
  token,
  library,
  branch,
  head,
  selected,
  hidden,
  mutationsEnabled,
  onChanged,
}: {
  baseUrl: string;
  token: string;
  library: string;
  branch: string;
  head: string;
  selected: string;
  hidden: boolean;
  mutationsEnabled: boolean;
  onChanged: () => void;
}) {
  const [refs, setRefs] = useState<Ref[]>([]);
  const [working, setWorking] = useState<Working[]>([]);
  const [checkout, setCheckout] = useState<Checkout>();
  const [reflog, setReflog] = useState<Reflog[]>([]);
  const [name, setName] = useState('');
  const [tagToRemove, setTagToRemove] = useState('');
  const [branchToRemove, setBranchToRemove] = useState('');
  const [confirmRefRemoval, setConfirmRefRemoval] = useState(false);
  const [selectedWorking, setSelectedWorking] = useState<string[]>([]);
  const [message, setMessage] = useState('');
  const [preparedStage, setPreparedStage] = useState<PreparedStage>();
  const [restoreMode, setRestoreMode] = useState<'restore' | 'revert'>(
    'restore',
  );
  const [confirmRestore, setConfirmRestore] = useState(false);
  const [rewrite, setRewrite] = useState<RewritePreview>();
  const [confirmRewrite, setConfirmRewrite] = useState(false);
  const [recoveryId, setRecoveryId] = useState('');
  const [recoveryName, setRecoveryName] = useState('');
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const root = `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control`;

  async function api(
    path: string,
    method = 'GET',
    body?: unknown,
    client = '__lineage_web__',
    version?: number,
    key?: string,
  ) {
    const response = await fetch(root + path, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        'X-Client-Id': client,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(version === undefined ? {} : { 'If-Match': String(version) }),
        ...(method === 'GET'
          ? {}
          : { 'Idempotency-Key': key ?? crypto.randomUUID() }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      cache: 'no-store',
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.code ?? 'History action failed');
    return result;
  }

  async function load() {
    const [references, gallery, journal, state] = await Promise.all([
      api('/refs'),
      api(
        `/working/${encodeURIComponent(branch)}`,
        'GET',
        undefined,
        '__gallery_web__',
      ),
      api('/reflog'),
      api('/checkout'),
    ]);
    setRefs(references as Ref[]);
    setWorking(gallery as Working[]);
    setReflog(journal as Reflog[]);
    setCheckout(state as Checkout);
  }

  useEffect(() => {
    void load().catch((reason) =>
      setError(
        reason instanceof Error
          ? reason.message
          : 'History actions are unavailable',
      ),
    );
  }, [baseUrl, token, library, branch]);

  async function run(action: () => Promise<unknown>, success: string) {
    setBusy(true);
    setError('');
    setStatus('');
    try {
      await action();
      setStatus(success);
      await load();
      onChanged();
      return true;
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'History action failed',
      );
      await load().catch(() => {});
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function createRef(kind: 'branch' | 'tag') {
    if (!selected || !name.trim()) return;
    await run(
      () =>
        api(`/refs/${kind}`, 'POST', { name: name.trim(), commitId: selected }),
      `${kind} created from the selected commit.`,
    );
    setName('');
  }

  async function removeRef(value: string) {
    const reference = refs.find((item) => item.name === value);
    if (!reference || !confirmRefRemoval) return;
    await run(
      () =>
        api(
          `/refs/${encodeURIComponent(reference.name)}`,
          'DELETE',
          {
            name: reference.name,
            expectedHead: reference.commitId,
            expectedVersion: reference.version,
            confirm: true,
          },
          '__lineage_web__',
          reference.version,
        ),
      `${reference.kind} removed. Its history remains recoverable.`,
    );
    setConfirmRefRemoval(false);
  }

  async function prepareCommit() {
    if (
      !message.trim() ||
      selectedWorking.length === 0 ||
      checkout?.deviceConflictCount
    )
      return;
    setBusy(true);
    setError('');
    try {
      const response = await api(
        `/staging/${encodeURIComponent(branch)}`,
        'POST',
        {
          workingChangeIds: selectedWorking,
          expectedHead: head,
          expectedVersion: 0,
        },
        '__gallery_web__',
      );
      setPreparedStage({
        id: response.id,
        head,
        ids: selectedWorking,
        message: message.trim(),
        authoredAt: new Date().toISOString(),
        key: crypto.randomUUID(),
      });
      setStatus(
        'Working changes staged for review. They are still uncommitted.',
      );
      await load();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Commit preparation failed',
      );
    } finally {
      setBusy(false);
    }
  }

  async function publishCommit() {
    if (!preparedStage) return;
    const done = await run(
      () =>
        api(
          '/commits',
          'POST',
          {
            stagingSetId: preparedStage.id,
            branch,
            expectedHead: preparedStage.head,
            message: preparedStage.message,
            authoredAt: preparedStage.authoredAt,
          },
          '__gallery_web__',
          undefined,
          preparedStage.key,
        ),
      'Committed selected gallery working changes.',
    );
    if (done) {
      setPreparedStage(undefined);
      setSelectedWorking([]);
    }
  }

  async function restore() {
    if (!selected || !message.trim() || !confirmRestore) return;
    await run(
      () =>
        api('/restore', 'POST', {
          branch,
          targetCommitId: selected,
          mode: restoreMode,
          message: message.trim(),
          expectedHead: head,
        }),
      `${restoreMode} created a new commit; later history remains intact.`,
    );
    setConfirmRestore(false);
  }

  async function previewRewrite() {
    if (!selected || branch === 'main') return;
    setBusy(true);
    setError('');
    try {
      const query = new URLSearchParams({ branch, newHead: selected });
      setRewrite((await api(`/rewrite/preview?${query}`)) as RewritePreview);
      setConfirmRewrite(false);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Rewrite preview failed',
      );
    } finally {
      setBusy(false);
    }
  }

  async function applyRewrite() {
    if (!rewrite || !confirmRewrite) return;
    await run(
      () =>
        api('/rewrite', 'POST', {
          branch,
          newHead: rewrite.newHead,
          previewToken: rewrite.confirmationToken,
          confirm: true,
        }),
      'Branch rewritten. Displaced versions remain recoverable for 90 days.',
    );
    setRewrite(undefined);
    setConfirmRewrite(false);
  }

  async function recover() {
    if (!recoveryId || !recoveryName.trim()) return;
    await run(
      () =>
        api(`/recovery/${recoveryId}`, 'POST', { name: recoveryName.trim() }),
      'Recovery branch created from the displaced version.',
    );
    setRecoveryId('');
    setRecoveryName('');
  }

  return (
    <section aria-labelledby="history-actions-heading">
      <h4 id="history-actions-heading">History actions</h4>
      <fieldset disabled={!mutationsEnabled}>
        <p>
          Selected immutable commit <code>{selected || 'none'}</code>; branch
          head <code>{head}</code>. Saving metadata in the gallery creates a
          working change, not a commit.
        </p>
        {error && (
          <p role="alert">
            {error}. Refresh history and review the current state before
            retrying.
          </p>
        )}
        {status && <p role="status">{status}</p>}
        {busy && <p role="status">Checking history state…</p>}
        {error === 'recent_authentication_required' && (
          <button type="button" onClick={beginRecentAuthentication}>
            Sign in again for recovery or rewrite
          </button>
        )}

        <section aria-labelledby="ref-actions-heading">
          <h5 id="ref-actions-heading">Branches and tags</h5>
          <label>
            New branch or tag name
            <input
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={!selected || !name.trim() || busy}
            onClick={() => void createRef('branch')}
          >
            Create branch at selected version
          </button>{' '}
          <button
            type="button"
            disabled={!selected || !name.trim() || busy}
            onClick={() => void createRef('tag')}
          >
            Create tag at selected version
          </button>
          <label>
            Remove tag
            <select
              value={tagToRemove}
              onChange={(event) => setTagToRemove(event.target.value)}
            >
              <option value="">Choose a tag</option>
              {refs
                .filter((item) => item.kind === 'tag')
                .map((item) => (
                  <option key={item.name} value={item.name}>
                    {item.name}
                  </option>
                ))}
            </select>
          </label>
          <label>
            Remove branch
            <select
              value={branchToRemove}
              onChange={(event) => setBranchToRemove(event.target.value)}
            >
              <option value="">Choose a branch</option>
              {refs
                .filter(
                  (item) =>
                    item.kind === 'branch' &&
                    !item.protected &&
                    item.name !== branch,
                )
                .map((item) => (
                  <option key={item.name} value={item.name}>
                    {item.name}
                  </option>
                ))}
            </select>
          </label>
          <label>
            <input
              type="checkbox"
              checked={confirmRefRemoval}
              onChange={(event) => setConfirmRefRemoval(event.target.checked)}
            />{' '}
            Confirm reference removal
          </label>
          <button
            type="button"
            disabled={!tagToRemove || !confirmRefRemoval || busy}
            onClick={() => void removeRef(tagToRemove)}
          >
            Remove selected tag
          </button>{' '}
          <button
            type="button"
            disabled={!branchToRemove || !confirmRefRemoval || busy}
            onClick={() => void removeRef(branchToRemove)}
          >
            Remove selected branch
          </button>
        </section>

        <section aria-labelledby="visibility-actions-heading">
          <h5 id="visibility-actions-heading">Presentation visibility</h5>
          <p>Hiding keeps the version reachable, auditable, and recoverable.</p>
          <button
            type="button"
            disabled={!selected || busy}
            onClick={() =>
              void run(
                () =>
                  api('/visibility', 'PUT', {
                    commitId: selected,
                    hidden: !hidden,
                  }),
                hidden
                  ? 'Version revealed.'
                  : 'Version hidden from normal history.',
              )
            }
          >
            {hidden ? 'Reveal selected version' : 'Hide selected version'}
          </button>
        </section>

        <section aria-labelledby="commit-actions-heading">
          <h5 id="commit-actions-heading">Commit gallery working changes</h5>
          <p>
            {working.length} gallery web changes on {branch};{' '}
            {checkout?.deviceConflictCount ?? 0} device conflicts. Device
            conflicts must be resolved first.
          </p>
          {working.length === 0 ? (
            <p>No gallery working changes are ready to stage.</p>
          ) : (
            <fieldset>
              <legend>Select working changes</legend>
              {working.map((item) => (
                <label key={item.id}>
                  <input
                    type="checkbox"
                    checked={selectedWorking.includes(item.id)}
                    onChange={(event) =>
                      setSelectedWorking((current) =>
                        event.target.checked
                          ? [...current, item.id]
                          : current.filter((id) => id !== item.id),
                      )
                    }
                  />{' '}
                  Image {item.assetId}, edit {item.editId ?? 'source'},{' '}
                  {item.change}, working version {item.version}, XMP{' '}
                  {item.contentSha256 ?? 'absent'}
                  {item.staged ? '; already staged' : ''}
                </label>
              ))}
            </fieldset>
          )}
          <label>
            Commit or restore message
            <input
              value={message}
              maxLength={4096}
              onChange={(event) => setMessage(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={
              !selectedWorking.length ||
              !message.trim() ||
              !!checkout?.deviceConflictCount ||
              busy ||
              !!preparedStage
            }
            onClick={() => void prepareCommit()}
          >
            Prepare selected commit
          </button>
          {preparedStage && (
            <div>
              <p>
                Staged {preparedStage.ids.length} changes against head{' '}
                <code>{preparedStage.head}</code> with message “
                {preparedStage.message}”. No commit exists yet.
              </p>
              <button
                type="button"
                disabled={busy}
                onClick={() => void publishCommit()}
              >
                Confirm and create commit
              </button>
            </div>
          )}
        </section>

        <section aria-labelledby="restore-actions-heading">
          <h5 id="restore-actions-heading">
            Restore or revert without rewriting
          </h5>
          <label>
            Operation
            <select
              value={restoreMode}
              onChange={(event) =>
                setRestoreMode(event.target.value as 'restore' | 'revert')
              }
            >
              <option value="restore">Restore selected state</option>
              <option value="revert">Revert selected commit</option>
            </select>
          </label>
          <label>
            <input
              type="checkbox"
              checked={confirmRestore}
              onChange={(event) => setConfirmRestore(event.target.checked)}
            />{' '}
            Confirm a new version-control commit
          </label>
          <button
            type="button"
            disabled={!selected || !message.trim() || !confirmRestore || busy}
            onClick={() => void restore()}
          >
            Create {restoreMode} commit
          </button>
        </section>

        <section aria-labelledby="rewrite-actions-heading">
          <h5 id="rewrite-actions-heading">Rewrite a non-default branch</h5>
          {branch === 'main' ? (
            <p>The protected default branch cannot be rewritten.</p>
          ) : (
            <>
              <button
                type="button"
                disabled={!selected || busy}
                onClick={() => void previewRewrite()}
              >
                Preview branch rewrite to selected version
              </button>
              {rewrite && (
                <>
                  <p>
                    Branch {rewrite.branch}: current head{' '}
                    <code>{rewrite.oldHead ?? 'none'}</code>, proposed head{' '}
                    <code>{rewrite.newHead}</code>.
                  </p>
                  <p>
                    {rewrite.displacedCommitIds.length} commits leave this
                    branch's reachable history:
                  </p>
                  <ul>
                    {rewrite.displacedCommitIds.map((id) => (
                      <li key={id}>
                        <code>{id}</code>
                      </li>
                    ))}
                  </ul>
                  <label>
                    <input
                      type="checkbox"
                      checked={confirmRewrite}
                      onChange={(event) =>
                        setConfirmRewrite(event.target.checked)
                      }
                    />{' '}
                    I reviewed every displaced commit
                  </label>
                  <button
                    type="button"
                    disabled={!confirmRewrite || busy}
                    onClick={() => void applyRewrite()}
                  >
                    Confirm branch rewrite
                  </button>
                </>
              )}
            </>
          )}
        </section>

        <section aria-labelledby="recovery-actions-heading">
          <h5 id="recovery-actions-heading">Recover displaced history</h5>
          <label>
            Eligible reflog entry
            <select
              value={recoveryId}
              onChange={(event) => setRecoveryId(event.target.value)}
            >
              <option value="">Choose an entry</option>
              {reflog
                .filter(
                  (entry) =>
                    entry.oldHead &&
                    entry.recoveryExpiresAt &&
                    new Date(entry.recoveryExpiresAt).getTime() >= Date.now(),
                )
                .map((entry) => (
                  <option key={entry.id} value={entry.id}>
                    {entry.refName}: {entry.oldHead} (until{' '}
                    {new Intl.DateTimeFormat(undefined, {
                      dateStyle: 'medium',
                      timeStyle: 'short',
                    }).format(new Date(entry.recoveryExpiresAt!))}
                    )
                  </option>
                ))}
            </select>
          </label>
          <label>
            New recovery branch name
            <input
              value={recoveryName}
              onChange={(event) => setRecoveryName(event.target.value)}
            />
          </label>
          <button
            type="button"
            disabled={!recoveryId || !recoveryName.trim() || busy}
            onClick={() => void recover()}
          >
            Create recovery branch
          </button>
          <p>
            Hidden reachable versions remain available through Show hidden
            commits and direct commit IDs. Their visibility has no expiry.
          </p>
        </section>
      </fieldset>
    </section>
  );
}
