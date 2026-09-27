import { useEffect, useState } from 'react';

type CheckoutState = {
  branch: string | null;
  version: number;
  browserWorkingCount: number;
  galleryWorkingCount: number;
  deviceConflictCount: number;
};
type BranchRef = { name: string; kind: string; commitId: string };
type Stash = { id: string; sourceRef: string; createdAt: string };

export function BranchSwitcher({
  baseUrl,
  token,
  library,
  branch,
  mutationsEnabled,
  onSwitched,
}: {
  baseUrl: string;
  token: string;
  library: string;
  branch: string;
  mutationsEnabled: boolean;
  onSwitched: (branch: string) => void;
}) {
  const [refs, setRefs] = useState<BranchRef[]>([]);
  const [state, setState] = useState<CheckoutState>();
  const [destination, setDestination] = useState('');
  const [action, setAction] = useState<'block' | 'preserve' | 'discard'>(
    'block',
  );
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [stash, setStash] = useState('');
  const [stashes, setStashes] = useState<Stash[]>([]);

  async function load() {
    const headers = {
      Authorization: `Bearer ${token}`,
      'X-Client-Id': '__lineage_web__',
    };
    const root = `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control`;
    const [checkoutResponse, refsResponse, stashesResponse] = await Promise.all(
      [
        fetch(`${root}/checkout`, { headers, cache: 'no-store' }),
        fetch(`${root}/refs`, { headers, cache: 'no-store' }),
        fetch(`${root}/stashes`, { headers, cache: 'no-store' }),
      ],
    );
    const checkout = await checkoutResponse.json();
    const branches = await refsResponse.json();
    const saved = await stashesResponse.json();
    if (!checkoutResponse.ok || !refsResponse.ok || !stashesResponse.ok)
      throw new Error(
        checkout.code ??
          branches.code ??
          saved.code ??
          'Branch state is unavailable',
      );
    setState(checkout as CheckoutState);
    setRefs((branches as BranchRef[]).filter((ref) => ref.kind === 'branch'));
    setStashes(saved as Stash[]);
  }

  useEffect(() => {
    void load().catch((reason) =>
      setError(
        reason instanceof Error
          ? reason.message
          : 'Branch state is unavailable',
      ),
    );
  }, [baseUrl, token, library]); // The owner context is stable for this mounted history view.

  async function switchBranch() {
    if (
      !state ||
      !destination ||
      destination === branch ||
      state.deviceConflictCount > 0
    )
      return;
    setBusy(true);
    setError('');
    try {
      const response = await fetch(
        `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control/checkout`,
        {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${token}`,
            'Content-Type': 'application/json',
            'X-Client-Id': '__lineage_web__',
            'Idempotency-Key': crypto.randomUUID(),
            'If-Match': String(state.version),
          },
          body: JSON.stringify({
            branch: destination,
            dirtyAction: action,
            confirmDiscard,
            expectedVersion: state.version,
          }),
        },
      );
      const result = await response.json();
      if (!response.ok) throw new Error(result.code ?? 'Branch switch failed');
      setStash(result.stashId ?? '');
      onSwitched(destination);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Branch switch failed',
      );
      await load().catch(() => {});
    } finally {
      setBusy(false);
    }
  }

  async function restoreStash(id: string) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(
        `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control/stashes/${encodeURIComponent(id)}/restore`,
        {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${token}`,
            'X-Client-Id': '__lineage_web__',
            'Idempotency-Key': crypto.randomUUID(),
          },
        },
      );
      const result = await response.json();
      if (!response.ok) throw new Error(result.code ?? 'Stash restore failed');
      setStash(`Restored ${id}`);
      await load();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Stash restore failed',
      );
      await load().catch(() => {});
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="branch-switch-heading">
      <h4 id="branch-switch-heading">Switch web branch</h4>
      <fieldset disabled={!mutationsEnabled}>
        <p>
          Current gallery branch: <strong>{branch}</strong>.{' '}
          {state?.galleryWorkingCount ?? 0} gallery web changes,{' '}
          {state?.browserWorkingCount ?? 0} browser changes, and{' '}
          {state?.deviceConflictCount ?? 0} device conflicts.
        </p>
        {state?.deviceConflictCount ? (
          <p role="alert">
            Resolve device conflicts before switching branches.
          </p>
        ) : null}
        <label>
          Switch to branch
          <select
            value={destination}
            onChange={(event) => setDestination(event.target.value)}
          >
            <option value="">Choose a branch</option>
            {refs
              .filter((ref) => ref.name !== branch)
              .map((ref) => (
                <option key={ref.name} value={ref.name}>
                  {ref.name}
                </option>
              ))}
          </select>
        </label>
        <fieldset>
          <legend>Handle pending web changes</legend>
          <label>
            <input
              type="radio"
              name="checkout-action"
              checked={action === 'block'}
              onChange={() => setAction('block')}
            />{' '}
            Block if dirty
          </label>
          <label>
            <input
              type="radio"
              name="checkout-action"
              checked={action === 'preserve'}
              onChange={() => setAction('preserve')}
            />{' '}
            Preserve in a stash
          </label>
          <label>
            <input
              type="radio"
              name="checkout-action"
              checked={action === 'discard'}
              onChange={() => setAction('discard')}
            />{' '}
            Discard pending changes
          </label>
        </fieldset>
        {action === 'discard' && (
          <label>
            <input
              type="checkbox"
              checked={confirmDiscard}
              onChange={(event) => setConfirmDiscard(event.target.checked)}
            />{' '}
            I confirm discarding the pending web changes
          </label>
        )}
        <button
          type="button"
          disabled={
            !state ||
            !destination ||
            destination === branch ||
            busy ||
            !!state.deviceConflictCount ||
            (action === 'discard' && !confirmDiscard)
          }
          onClick={() => void switchBranch()}
        >
          Switch branch
        </button>
        {busy && <p role="status">Switching branch…</p>}
        {error && (
          <p role="alert">
            {error}. Pending changes were kept; refresh the branch state and
            review again.
          </p>
        )}
        {stash && (
          <p role="status">
            Stash <code>{stash}</code>.
          </p>
        )}
        <h5>Preserved web changes</h5>
        {stashes.length === 0 ? (
          <p>No preserved web changes.</p>
        ) : (
          <ul>
            {stashes.map((item) => (
              <li key={item.id}>
                Stash <code>{item.id}</code> from branch{' '}
                <strong>{item.sourceRef}</strong>, saved{' '}
                {new Intl.DateTimeFormat(undefined, {
                  dateStyle: 'medium',
                  timeStyle: 'short',
                }).format(new Date(item.createdAt))}
                .{' '}
                <button
                  type="button"
                  disabled={
                    busy ||
                    !!state?.browserWorkingCount ||
                    !!state?.galleryWorkingCount
                  }
                  onClick={() => void restoreStash(item.id)}
                >
                  Restore this stash
                </button>
              </li>
            ))}
          </ul>
        )}
      </fieldset>
    </section>
  );
}
