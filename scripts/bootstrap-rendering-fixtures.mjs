import { createHash, randomUUID } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

export async function renderingFixtureInputs() {
  const raw = await readFile('tests/rendering/corpus/Pentax-K10D.dng');
  if (hash(raw) !== 'e7ea961edfea725ad2753f261d20da5e6addd5d641e5bcbc373999710f27941f')
    throw new Error('RAW fixture differs from the CC0 corpus pin.');
  const xmps = await Promise.all(['current', 'historical-v1', 'historical-v2', 'historical-v3']
    .map(async name => Buffer.from((await readFile(`tests/rendering/corpus/${name}.xmp`, 'utf8')).replaceAll('IMG_9406.CR2', 'sample.dng'))));
  // Adapt the authored corpus sidecars to the supported DNG; the fifth state uses neutral exposure.
  const neutral = xmps[0].toString().replace('0000803f0000803f0000803f00004842000080c0', '0000803f000000000000803f00004842000080c0');
  xmps.push(Buffer.from(neutral));
  if (new Set(xmps.map(hash)).size !== 5) throw new Error('Expected five distinct RAW/XMP combinations.');
  return { raw, xmps };
}

export async function bootstrapRenderingFixtures(baseUrl, token, output) {
  if (!baseUrl || !token || !output) throw new Error('Fixture bootstrap needs a base URL, owner token and output path.');
  const { raw, xmps } = await renderingFixtureInputs();
  const root = `deployment-rendering-fixtures/${randomUUID()}`;
  const libraryId = 'main';
  const request = async (route, options = {}) => {
    const response = await fetch(new URL(route, baseUrl), {
      signal: AbortSignal.timeout(15000), ...options,
      headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': randomUUID(), ...options.headers },
    });
    if (!response.ok) throw new Error(`Fixture ${options.method ?? 'GET'} ${route} failed with HTTP ${response.status}`);
    return response.json();
  };
  async function upload(path, bytes) {
    const batch = await request('/v1/import-batches', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ atomic: false, items: [{ path, sha256: hash(bytes), length: bytes.length }] }),
    });
    if (!Number.isSafeInteger(batch.partSize) || batch.partSize <= 0) throw new Error('Invalid upload part size.');
    const session = batch.items[0];
    await request(`/v1/upload-sessions/${session.id}/transfer`, { method: 'POST', headers: { 'If-Match': '"1"' } });
    for (let offset = 0, part = 1; offset < bytes.length; offset += batch.partSize, part++) {
      const chunk = bytes.subarray(offset, offset + batch.partSize);
      await request(`/v1/upload-sessions/${session.id}/parts/${part}`, { method: 'PUT',
        headers: { 'If-Match': '"1"', 'X-Content-SHA256': hash(chunk) }, body: chunk });
    }
    await request(`/v1/upload-sessions/${session.id}/complete`, { method: 'POST', headers: { 'If-Match': '"1"' } });
    for (let attempt = 0; attempt < 45; attempt++) {
      const status = await request(`/v1/import-batches/${batch.id}`);
      const item = status.items[0];
      if (item.state === 'active' && item.assetId) return item;
      if (item.code || ['failed', 'quarantined', 'integrity-error'].includes(item.state))
        throw new Error(`Fixture publication failed: ${item.code ?? item.state}`);
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
    throw new Error('Fixture publication timed out.');
  }
  const original = await upload(`${root}/sample.dng`, raw);
  const fixtures = [];
  for (const [index, xmp] of xmps.entries()) {
    const path = `${root}/sample${index ? `_${String(index).padStart(2, '0')}` : ''}.dng.xmp`;
    const edit = await upload(path, xmp);
    if (edit.assetId !== original.assetId || !edit.editId) throw new Error('Fixture XMP association differs from the uploaded RAW.');
    await request(`/v1/libraries/${libraryId}/version-control/working`, { method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-Client-Id': 'deployment-rendering-fixtures' },
      body: JSON.stringify({ assetId: edit.assetId, editId: edit.editId, branch: 'main', change: 'added',
        baseContentSha256: null, contentSha256: hash(xmp), path, lifecycle: 'active', expectedVersion: 0 }),
    });
    fixtures.push({ libraryId, assetId: edit.assetId, editId: edit.editId });
  }
  if (new Set(fixtures.map(item => item.editId)).size !== 5) throw new Error('Fixture edit identities are not distinct.');
  await writeFile(output, JSON.stringify({ fixtures, rawSha256: hash(raw), xmpSha256: xmps.map(hash), path: root }, null, 2) + '\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.env.DEPLOYMENT_ENVIRONMENT !== 'nonprod') throw new Error('Fixture bootstrap is restricted to nonprod.');
  await bootstrapRenderingFixtures(process.argv[2], process.env.INGEST_SMOKE_TOKEN, process.argv[3]);
}
