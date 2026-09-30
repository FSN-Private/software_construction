import { useEffect, useRef, useState } from 'react';

type Item = {
  id: string;
  path: string;
  state: string;
  lifecycleVersion: number;
  length: number;
  parts: { number: number }[];
  code?: string;
};
type Batch = { id: string; state: string; partSize: number; items: Item[] };
type Asset = {
  id: string;
  state: string;
  lifecycleVersion: number;
  path: string;
  deletedUntil?: string;
};
type Edit = {
  id: string;
  state: string;
  lifecycleVersion: number;
  ordinal: number;
  deletedUntil?: string;
};

export function Library({
  baseUrl,
  token,
  previewMode = false,
}: {
  baseUrl: string;
  token: string;
  previewMode?: boolean;
}) {
  const [files, setFiles] = useState<File[]>([]);
  const directoryInput = useRef<HTMLInputElement>(null);
  const [atomic, setAtomic] = useState(false);
  const [batch, setBatch] = useState<Batch>();
  const [assets, setAssets] = useState<Asset[]>([]);
  const [edits, setEdits] = useState<Record<string, Edit[]>>({});
  const [deleted, setDeleted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState(
    'Choose images and darktable sidecars to import.',
  );
  const [error, setError] = useState('');
  const [paths, setPaths] = useState<Record<string, string>>({});
  const [resumeId, setResumeId] = useState(
    () => sessionStorage.getItem('ingest-batch') ?? '',
  );
  const [current, setCurrent] = useState(0);
  useEffect(
    () => directoryInput.current?.setAttribute('webkitdirectory', ''),
    [],
  );
  const importPath = (file: File) => file.webkitRelativePath || file.name;

  async function request<T>(
    path: string,
    method = 'GET',
    body?: BodyInit,
    version?: number,
    key: string = crypto.randomUUID(),
    sha?: string,
  ): Promise<T> {
    const response = await fetch(baseUrl + '/v1' + path, {
      method,
      body,
      headers: {
        Authorization: `Bearer ${token}`,
        'Idempotency-Key': key,
        ...(version ? { 'If-Match': String(version) } : {}),
        ...(body
          ? {
              'Content-Type': sha
                ? 'application/octet-stream'
                : 'application/json',
            }
          : {}),
        ...(sha ? { 'X-Content-SHA256': sha } : {}),
      },
    });
    const result = await response.json();
    if (!response.ok)
      throw new Error(
        `${result.code ?? 'request_failed'}. Refresh the current state and retry; use an alternate path for a collision.`,
      );
    return result as T;
  }
  async function hash(bytes: ArrayBuffer) {
    return Array.from(
      new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)),
      (b) => b.toString(16).padStart(2, '0'),
    ).join('');
  }
  async function refresh(after?: string) {
    const page = await request<Asset[]>(
      `/assets?deleted=${deleted}${after ? `&after=${after}` : ''}`,
    );
    setAssets((old) => (after ? [...old, ...page] : page));
  }
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (e) {
      setError(
        e instanceof Error
          ? e.message
          : 'Request failed. Retry when the service is available.',
      );
    } finally {
      setBusy(false);
    }
  }
  useEffect(() => {
    if (previewMode) return;
    void run(() => refresh());
  }, [deleted, previewMode]);
  useEffect(() => {
    if (!batch || !['created', 'processing'].includes(batch.state)) return;
    const timer = window.setInterval(() => {
      request<Batch>(`/import-batches/${batch.id}`)
        .then(setBatch)
        .catch(() =>
          setError(
            'Progress is temporarily unavailable. Use Refresh batch to retry.',
          ),
        );
    }, 2000);
    return () => window.clearInterval(timer);
  }, [batch?.id, batch?.state]);

  async function upload(resume: boolean) {
    if (files.length === 0) throw new Error('Choose the original files first.');
    const seen = new Set<string>();
    for (const file of files) {
      const path = importPath(file);
      const key = path.replaceAll('\\', '/').normalize('NFC').toUpperCase();
      if (seen.has(key))
        throw new Error(
          `Duplicate destination path: ${path}. Choose a different folder or remove one file.`,
        );
      seen.add(key);
    }
    let selected: Batch;
    if (resume) selected = await request<Batch>(`/import-batches/${resumeId}`);
    else {
      setMessage(
        'Calculating file checksums. Keep your source files until every item is active.',
      );
      const items = [];
      for (const file of files)
        items.push({
          path: importPath(file),
          length: file.size,
          sha256: await hash(await file.arrayBuffer()),
        });
      selected = await request<Batch>(
        '/import-batches',
        'POST',
        JSON.stringify({ atomic, items }),
      );
      sessionStorage.setItem('ingest-batch', selected.id);
      setResumeId(selected.id);
    }
    setBatch(selected);
    setCurrent(0);
    for (const item of selected.items) {
      if (item.state !== 'uploading') continue;
      const file = files.find(
        (f) => importPath(f) === item.path && f.size === item.length,
      );
      if (!file)
        throw new Error(`Reselect the original file for ${item.path}.`);
      await request(
        `/upload-sessions/${item.id}/transfer`,
        'POST',
        undefined,
        item.lifecycleVersion,
        `${item.id}-transfer`,
      );
      for (
        let offset = 0, number = 1;
        offset < file.size;
        offset += selected.partSize, number++
      ) {
        if (item.parts.some((p) => p.number === number)) continue;
        const bytes = await file
          .slice(offset, offset + selected.partSize)
          .arrayBuffer();
        await request(
          `/upload-sessions/${item.id}/parts/${number}`,
          'PUT',
          bytes,
          item.lifecycleVersion,
          `${item.id}_part_${number}`,
          await hash(bytes),
        );
        setMessage(`Uploaded part ${number} of ${item.path}.`);
      }
      await request(
        `/upload-sessions/${item.id}/complete`,
        'POST',
        undefined,
        item.lifecycleVersion,
        `${item.id}_complete`,
      );
      setCurrent((value) => value + 1);
    }
    setBatch(await request<Batch>(`/import-batches/${selected.id}`));
    setMessage(
      'Transfer finished. Verification continues; refresh the library after the batch completes.',
    );
  }
  async function change(
    asset: Asset | Edit,
    kind: 'assets' | 'edits',
    action: 'delete' | 'restore' | 'move',
  ) {
    const path = paths[asset.id] || undefined;
    if (action === 'move' && !path)
      throw new Error('Enter the destination path first.');
    await request(
      `/${kind}/${asset.id}${action === 'delete' ? '' : action === 'move' ? '/path' : '/restore'}`,
      action === 'delete' ? 'DELETE' : action === 'move' ? 'PATCH' : 'POST',
      action === 'delete' || kind === 'edits'
        ? undefined
        : JSON.stringify({ path }),
      asset.lifecycleVersion,
    );
    setMessage(
      `${action} completed. Deleted items remain recoverable for 90 days.`,
    );
    setEdits({});
    await refresh();
  }
  async function download(id: string, kind: 'assets' | 'edits') {
    const response = await fetch(`${baseUrl}/v1/${kind}/${id}/content`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!response.ok)
      throw new Error(
        'Content is unavailable or failed integrity verification.',
      );
    const bytes = await response.arrayBuffer();
    if ((await hash(bytes)) !== response.headers.get('X-Content-SHA256'))
      throw new Error('Download checksum failed.');
    const url = URL.createObjectURL(new Blob([bytes]));
    const link = document.createElement('a');
    link.href = url;
    link.download =
      kind === 'edits'
        ? `${id}.xmp`
        : (assets
            .find((a) => a.id === id)
            ?.path.split('/')
            .pop() ?? id);
    link.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    setMessage('Downloaded bytes verified.');
  }

  if (previewMode)
    return (
      <section aria-labelledby="library-heading">
        <h2 id="library-heading">Import and organize photographs</h2>
        <p role="status">
          This is a sample gallery preview. Files cannot be imported or saved
          here. Connect the owner backend to import real photographs and
          folders.
        </p>
      </section>
    );

  return (
    <section aria-labelledby="library-heading">
      <h2 id="library-heading">Import and organize photographs</h2>
      <label htmlFor="import-files">Images and darktable sidecars</label>{' '}
      <input
        id="import-files"
        type="file"
        multiple
        disabled={busy || previewMode}
        onChange={(e) => setFiles(Array.from(e.target.files ?? []))}
      />
      <label htmlFor="import-folder">
        Or select a folder (including subfolders)
      </label>{' '}
      <input
        id="import-folder"
        ref={directoryInput}
        type="file"
        multiple
        disabled={busy || previewMode}
        onChange={(event) => setFiles(Array.from(event.target.files ?? []))}
      />
      {files.length > 0 && (
        <p role="status">
          {files.length} files selected. Relative folder paths are preserved.
        </p>
      )}
      <p>
        <label>
          <input
            type="checkbox"
            checked={atomic}
            disabled={busy}
            onChange={(e) => setAtomic(e.target.checked)}
          />{' '}
          Publish all files together
        </label>
      </p>
      <button
        disabled={busy || previewMode || files.length === 0}
        onClick={() => void run(() => upload(false))}
      >
        Import selected files
      </button>{' '}
      <label htmlFor="resume-batch">Batch ID to resume</label>{' '}
      <input
        id="resume-batch"
        value={resumeId}
        disabled={busy}
        onChange={(e) => setResumeId(e.target.value)}
      />{' '}
      <button
        disabled={busy || previewMode || !resumeId || files.length === 0}
        onClick={() => void run(() => upload(true))}
      >
        Resume with selected files
      </button>
      <p role="status" aria-live="polite">
        {message}
      </p>
      {error && <p role="alert">{error}</p>}
      {busy && (
        <progress
          aria-label="Files transferred"
          value={current}
          max={files.length || 1}
        />
      )}
      {batch && (
        <div>
          <h3>Batch: {batch.state}</h3>
          <button
            disabled={busy}
            onClick={() =>
              void run(async () =>
                setBatch(await request<Batch>(`/import-batches/${batch.id}`)),
              )
            }
          >
            Refresh batch
          </button>
          <ul>
            {batch.items.map((item) => (
              <li key={item.id}>
                {item.path}: {item.state}
                {item.code && ` — ${item.code}`}
                {item.state === 'uploading' && (
                  <button
                    disabled={busy}
                    onClick={() =>
                      void run(async () => {
                        await request(
                          `/upload-sessions/${item.id}`,
                          'DELETE',
                          undefined,
                          item.lifecycleVersion,
                        );
                        setBatch(
                          await request<Batch>(`/import-batches/${batch.id}`),
                        );
                      })
                    }
                  >
                    Cancel {item.path}
                  </button>
                )}
              </li>
            ))}
          </ul>
          <p>
            For quarantined input, correct the reported format, source
            association, or destination path and import again. Keep the original
            source files.
          </p>
        </div>
      )}
      <h3>Stored photographs</h3>
      <label>
        <input
          type="checkbox"
          checked={deleted}
          disabled={busy}
          onChange={(e) => setDeleted(e.target.checked)}
        />{' '}
        Show recoverable deletions
      </label>{' '}
      <button disabled={busy} onClick={() => void run(() => refresh())}>
        Refresh library
      </button>
      <ul>
        {assets.map((asset) => (
          <li key={asset.id}>
            <p>
              {asset.path} — {asset.state}
              {asset.deletedUntil &&
                `; restore by ${new Date(asset.deletedUntil).toLocaleString()}`}
            </p>
            <label htmlFor={`path-${asset.id}`}>
              {deleted ? 'Alternate restore path (optional)' : 'New path'}
            </label>{' '}
            <input
              id={`path-${asset.id}`}
              value={paths[asset.id] ?? ''}
              disabled={busy}
              onChange={(e) =>
                setPaths({ ...paths, [asset.id]: e.target.value })
              }
            />{' '}
            {asset.state === 'integrity-error' ? (
              <p role="alert">
                Integrity verification failed. Follow the recovery runbook
                before restoring access.
              </p>
            ) : deleted ? (
              <button
                disabled={busy}
                onClick={() =>
                  void run(() => change(asset, 'assets', 'restore'))
                }
              >
                Restore {asset.path}
              </button>
            ) : (
              <>
                <button
                  disabled={busy}
                  onClick={() => void run(() => download(asset.id, 'assets'))}
                >
                  Download {asset.path}
                </button>{' '}
                <button
                  disabled={busy}
                  onClick={() =>
                    void run(() => change(asset, 'assets', 'move'))
                  }
                >
                  Move {asset.path}
                </button>{' '}
                <button
                  disabled={busy}
                  onClick={() =>
                    void run(() => change(asset, 'assets', 'delete'))
                  }
                >
                  Delete {asset.path}
                </button>{' '}
                <button
                  disabled={busy}
                  onClick={() =>
                    void run(async () =>
                      setEdits({
                        ...edits,
                        [asset.id]: await request<Edit[]>(
                          `/assets/${asset.id}/edits`,
                        ),
                      }),
                    )
                  }
                >
                  Show sidecars for {asset.path}
                </button>
                <ul>
                  {edits[asset.id]?.map((edit) => (
                    <li key={edit.id}>
                      Edit {edit.ordinal}: {edit.state}
                      {edit.deletedUntil &&
                        `; restore by ${new Date(edit.deletedUntil).toLocaleString()}`}{' '}
                      {edit.state === 'recoverable-deleted' ? (
                        <button
                          disabled={busy}
                          onClick={() =>
                            void run(() => change(edit, 'edits', 'restore'))
                          }
                        >
                          Restore edit {edit.ordinal}
                        </button>
                      ) : (
                        <>
                          <button
                            disabled={busy}
                            onClick={() =>
                              void run(() => download(edit.id, 'edits'))
                            }
                          >
                            Download edit {edit.ordinal}
                          </button>{' '}
                          <button
                            disabled={busy}
                            onClick={() =>
                              void run(() => change(edit, 'edits', 'delete'))
                            }
                          >
                            Delete edit {edit.ordinal}
                          </button>
                        </>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
          </li>
        ))}
      </ul>
      {assets.length > 0 && assets.length % 100 === 0 && (
        <button
          disabled={busy}
          onClick={() => void run(() => refresh(assets.at(-1)?.id))}
        >
          Load more photographs
        </button>
      )}
    </section>
  );
}
