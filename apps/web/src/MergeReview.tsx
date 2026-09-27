import { useEffect, useState } from 'react';

type Ref = { name: string; kind: string; commitId: string };
type Field = {
  field: string;
  base: string | null;
  target: string | null;
  source: string | null;
  requiresResolution: boolean;
};
type Affected = {
  assetId: string;
  editId: string | null;
  baseXmpSha256: string | null;
  targetXmpSha256: string | null;
  sourceXmpSha256: string | null;
  proposedXmpSha256: string | null;
  proposedPath: string | null;
  proposedLifecycle: string | null;
  targetPath: string | null;
  sourcePath: string | null;
  targetLifecycle: string | null;
  sourceLifecycle: string | null;
  conflicts: string[];
  metadataDifferences: Field[];
  targetPreviewState: string;
  sourcePreviewState: string;
  targetPreviewProvenance?: {
    selectedVersion: string;
    xmpSha256: string;
    rendererRelease: string;
  } | null;
  sourcePreviewProvenance?: {
    selectedVersion: string;
    xmpSha256: string;
    rendererRelease: string;
  } | null;
};
type Proposal = {
  targetBranch: string;
  sourceBranch: string;
  targetHead: string;
  sourceHead: string;
  baseCommitId: string | null;
  policyVersion: string;
  affected: Affected[];
  retained: {
    assetId: string;
    editId: string | null;
    xmpSha256: string | null;
    path: string | null;
    lifecycle: string | null;
  }[];
  proposalDigest: string;
};
type Review = {
  proposalDigest: string;
  reviewDigest: string;
  resolved: {
    assetId: string;
    editId: string | null;
    xmpSha256: string | null;
    xmpBase64: string | null;
    path: string | null;
    lifecycle: string | null;
    changed: boolean;
  }[];
  retained: Proposal['retained'];
};

export function MergeReview({
  baseUrl,
  token,
  library,
  targetBranch,
  targetHead,
  mutationsEnabled,
  onPublished,
}: {
  baseUrl: string;
  token: string;
  library: string;
  targetBranch: string;
  targetHead: string;
  mutationsEnabled: boolean;
  onPublished: () => void;
}) {
  const [refs, setRefs] = useState<Ref[]>([]);
  const [source, setSource] = useState('');
  const [proposal, setProposal] = useState<Proposal>();
  const [review, setReview] = useState<Review>();
  const [confirmationKey, setConfirmationKey] = useState('');
  const [authoredAt, setAuthoredAt] = useState('');
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [message, setMessage] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [published, setPublished] = useState('');

  useEffect(() => {
    let active = true;
    fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/version-control/refs`,
      {
        headers: { Authorization: `Bearer ${token}` },
        cache: 'no-store',
      },
    )
      .then(async (response) => {
        const body = await response.json();
        if (!response.ok)
          throw new Error(body.code ?? 'Branches are unavailable');
        return body as Ref[];
      })
      .then((value) => {
        if (active) setRefs(value.filter((ref) => ref.kind === 'branch'));
      })
      .catch((reason) => {
        if (active)
          setError(
            reason instanceof Error
              ? reason.message
              : 'Branches are unavailable',
          );
      });
    return () => {
      active = false;
    };
  }, [baseUrl, library, token]);

  const sourceRef = refs.find((ref) => ref.name === source);
  const prepared = proposal && {
    targetBranch,
    sourceBranch: proposal.sourceBranch,
    expectedTargetHead: proposal.targetHead,
    expectedSourceHead: proposal.sourceHead,
  };
  const conflictKeys =
    proposal?.affected.flatMap((item) =>
      item.conflicts.map((conflict) => key(item, conflict)),
    ) ?? [];
  const resolved = conflictKeys.every(
    (item) => choices[item] === 'target' || choices[item] === 'source',
  );

  async function request(path: string, body: unknown, idempotencyKey?: string) {
    const response = await fetch(
      `${baseUrl}/v1/libraries/${encodeURIComponent(library)}/lineage${path}`,
      {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          'X-Client-Id': '__lineage_web__',
          ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}),
        },
        body: JSON.stringify(body),
        cache: 'no-store',
      },
    );
    const value = await response.json();
    if (!response.ok) throw new Error(value.code ?? 'Merge request failed');
    return value;
  }

  async function prepare() {
    if (!sourceRef) return;
    setBusy(true);
    setError('');
    setProposal(undefined);
    setReview(undefined);
    setChoices({});
    setConfirmed(false);
    try {
      const value = await request('/merges/prepare', {
        targetBranch,
        sourceBranch: sourceRef.name,
        expectedTargetHead: targetHead,
        expectedSourceHead: sourceRef.commitId,
      });
      setProposal(value as Proposal);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Merge preparation failed',
      );
    } finally {
      setBusy(false);
    }
  }

  async function reviewResult() {
    if (!proposal || !prepared || !resolved) return;
    setBusy(true);
    setError('');
    setReview(undefined);
    try {
      const result = await request('/merges/review', {
        prepared,
        proposalDigest: proposal.proposalDigest,
        resolutions: choices,
      });
      setReview(result as Review);
      setConfirmationKey(crypto.randomUUID());
      setAuthoredAt(new Date().toISOString());
      setConfirmed(false);
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Merge review failed',
      );
    } finally {
      setBusy(false);
    }
  }

  async function publish() {
    if (
      !proposal ||
      !prepared ||
      !review ||
      !confirmed ||
      !resolved ||
      !message.trim()
    )
      return;
    setBusy(true);
    setError('');
    try {
      const result = await request(
        '/merges/confirm',
        {
          reviewed: {
            prepared,
            proposalDigest: proposal.proposalDigest,
            resolutions: choices,
          },
          reviewDigest: review.reviewDigest,
          message: message.trim(),
          authoredAt,
          confirm: true,
        },
        confirmationKey,
      );
      setPublished(result.commitId as string);
      setProposal(undefined);
      onPublished();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Merge confirmation failed',
      );
      setConfirmed(false);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="merge-review-heading">
      <h4 id="merge-review-heading">Merge branches</h4>
      <p>
        Target branch <strong>{targetBranch}</strong>, head{' '}
        <code>{targetHead}</code>.
      </p>
      <label>
        Source branch
        <select
          value={source}
          onChange={(event) => {
            setSource(event.target.value);
            setProposal(undefined);
            setReview(undefined);
          }}
        >
          <option value="">Choose a branch</option>
          {refs
            .filter((ref) => ref.name !== targetBranch)
            .map((ref) => (
              <option key={ref.name} value={ref.name}>
                {ref.name}
              </option>
            ))}
        </select>
      </label>
      {sourceRef && (
        <p>
          Source head <code>{sourceRef.commitId}</code>.
        </p>
      )}
      <button
        type="button"
        disabled={!sourceRef || busy}
        onClick={() => void prepare()}
      >
        Prepare merge
      </button>
      {busy && <p role="status">Checking merge state…</p>}
      {error && (
        <p role="alert">
          {error}. Refresh and review the current branches before trying again.
        </p>
      )}
      {published && (
        <p role="status">
          Merge commit <code>{published}</code> was created.
        </p>
      )}
      {proposal && (
        <>
          <p>
            Policy <code>{proposal.policyVersion}</code>. Common ancestor:{' '}
            <code>{proposal.baseCommitId ?? 'none'}</code>.
          </p>
          <p>
            Review digest <code>{proposal.proposalDigest}</code>. This proposal
            is bound to both branch heads and all affected images.
          </p>
          {proposal.affected.length === 0 ? (
            <p>No changed image states need merging.</p>
          ) : (
            <ol className="merge-affected">
              {proposal.affected.map((item) => (
                <li key={`${item.assetId}/${item.editId}`}>
                  <h5>
                    Image <code>{item.assetId}</code>, edit{' '}
                    <code>{item.editId ?? 'source'}</code>
                  </h5>
                  <p>
                    Base XMP <code>{item.baseXmpSha256 ?? 'absent'}</code>;
                    target XMP <code>{item.targetXmpSha256 ?? 'absent'}</code>;
                    source XMP <code>{item.sourceXmpSha256 ?? 'absent'}</code>.
                  </p>
                  <p>
                    Proposed XMP{' '}
                    <code>
                      {item.proposedXmpSha256 ?? 'pending resolution'}
                    </code>
                    ; path {item.proposedPath ?? 'pending resolution'};
                    lifecycle {item.proposedLifecycle ?? 'pending resolution'}.
                  </p>
                  <p>
                    Target preview: {item.targetPreviewState}. Source preview:{' '}
                    {item.sourcePreviewState}. Unavailable previews do not
                    affect XMP review.
                  </p>
                  {item.targetPreviewProvenance && (
                    <p>
                      Target render:{' '}
                      {item.targetPreviewProvenance.rendererRelease}; commit{' '}
                      <code>
                        {item.targetPreviewProvenance.selectedVersion}
                      </code>
                      ; XMP{' '}
                      <code>{item.targetPreviewProvenance.xmpSha256}</code>.
                    </p>
                  )}
                  {item.sourcePreviewProvenance && (
                    <p>
                      Source render:{' '}
                      {item.sourcePreviewProvenance.rendererRelease}; commit{' '}
                      <code>
                        {item.sourcePreviewProvenance.selectedVersion}
                      </code>
                      ; XMP{' '}
                      <code>{item.sourcePreviewProvenance.xmpSha256}</code>.
                    </p>
                  )}
                  {item.metadataDifferences.length > 0 && (
                    <ul>
                      {item.metadataDifferences.map((field) => (
                        <li key={field.field}>
                          {field.field}: base{' '}
                          <code>{field.base ?? 'absent'}</code>, target{' '}
                          <code>{field.target ?? 'absent'}</code>, source{' '}
                          <code>{field.source ?? 'absent'}</code>
                          {field.requiresResolution
                            ? '; choose below'
                            : '; disjoint change'}
                        </li>
                      ))}
                    </ul>
                  )}
                  {item.conflicts.map((conflict) => (
                    <label key={conflict}>
                      Resolve {conflict} for image {item.assetId}
                      <select
                        value={choices[key(item, conflict)] ?? ''}
                        onChange={(event) => {
                          setChoices((current) => ({
                            ...current,
                            [key(item, conflict)]: event.target.value,
                          }));
                          setReview(undefined);
                        }}
                      >
                        <option value="">Choose target or source</option>
                        <option value="target">Target value</option>
                        <option value="source">Source value</option>
                      </select>
                    </label>
                  ))}
                </li>
              ))}
            </ol>
          )}
          <details>
            <summary>
              {proposal.retained.length} unchanged image states retained from
              the target branch
            </summary>
            <ul>
              {proposal.retained.map((item) => (
                <li key={`${item.assetId}/${item.editId}`}>
                  Image <code>{item.assetId}</code>, edit{' '}
                  <code>{item.editId ?? 'source'}</code>, XMP{' '}
                  <code>{item.xmpSha256 ?? 'absent'}</code>, path{' '}
                  {item.path ?? 'absent'}, lifecycle{' '}
                  {item.lifecycle ?? 'absent'}
                </li>
              ))}
            </ul>
          </details>
          <button
            type="button"
            disabled={!resolved || busy}
            onClick={() => void reviewResult()}
          >
            Review exact merge result
          </button>
          {review && (
            <section aria-label="Exact reviewed merge result">
              <p>
                Reviewed result digest <code>{review.reviewDigest}</code>.
                Confirm only after checking every changed image.
              </p>
              <ol>
                {review.resolved.map((item) => (
                  <li key={`${item.assetId}/${item.editId}`}>
                    Image <code>{item.assetId}</code>, edit{' '}
                    <code>{item.editId ?? 'source'}</code>:{' '}
                    {item.changed ? 'changed' : 'retained'}; XMP{' '}
                    <code>{item.xmpSha256 ?? 'absent'}</code>; path{' '}
                    {item.path ?? 'absent'}; lifecycle{' '}
                    {item.lifecycle ?? 'absent'}.
                    {item.xmpBase64 && (
                      <details>
                        <summary>Exact proposed XMP bytes</summary>
                        <pre>{decodeXmp(item.xmpBase64)}</pre>
                      </details>
                    )}
                  </li>
                ))}
              </ol>
              <p>
                {review.retained.length} other target image states remain in the
                proposed library result.
              </p>
            </section>
          )}
          <label>
            Merge commit message
            <input
              value={message}
              maxLength={4096}
              onChange={(event) => setMessage(event.target.value)}
            />
          </label>
          <label>
            <input
              type="checkbox"
              checked={confirmed}
              onChange={(event) => setConfirmed(event.target.checked)}
            />{' '}
            I reviewed the complete proposed library result and selected every
            conflict
          </label>
          <button
            type="button"
            disabled={
              !review ||
              !confirmed ||
              !resolved ||
              !message.trim() ||
              busy ||
              proposal.affected.length === 0 ||
              !mutationsEnabled
            }
            onClick={() => void publish()}
          >
            Create merge commit
          </button>
        </>
      )}
    </section>
  );
}

function key(item: Affected, field: string) {
  return `${item.assetId}/${item.editId ?? 'source'}:${field}`;
}

function decodeXmp(base64: string) {
  return new TextDecoder().decode(
    Uint8Array.from(atob(base64), (character) => character.charCodeAt(0)),
  );
}
