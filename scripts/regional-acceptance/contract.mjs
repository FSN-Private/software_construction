const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const hex = /^[a-f0-9]{64}$/;
export const functionArn = /^arn:aws:lambda:ap-southeast-1:(\d{12}):function:platform-nonprod-acceptance:([1-9]\d*)$/;
const require = condition => { if (!condition) throw new Error('Invalid regional acceptance data.'); };
const text = value => typeof value === 'string' && value.length > 0 && value.length <= 256 && !/[\r\n]/.test(value);

export function validateOrigin(origin) {
  const url = new URL(origin);
  require(url.origin === origin && url.protocol === 'https:' && /^[a-z0-9]+\.execute-api\.ap-southeast-1\.amazonaws\.com$/.test(url.hostname));
  return origin;
}

export function validateRequest(value) {
  require(value?.schemaVersion === 1 && uuid.test(value.invocationId ?? '') && /^[a-f0-9]{40}$/.test(value.sourceRevision ?? '') && /^[1-9]\d{0,19}$/.test(value.releaseRunId ?? ''));
  require(hex.test(value.manifestSHA256 ?? '') && hex.test(value.bundleSHA256 ?? ''));
  validateOrigin(value.origin);
  require(typeof value.token === 'string' && value.token.length > 0 && value.token.length <= 16384 && !/[\r\n]/.test(value.token));
  require(Array.isArray(value.fixtures) && value.fixtures.length === 5);
  require(value.fixtures.every(item => item && text(item.libraryId) && uuid.test(item.assetId ?? '') && uuid.test(item.editId ?? '')));
  require(new Set(value.fixtures.map(item => JSON.stringify([item.libraryId, item.assetId, item.editId]))).size === 5);
  return value;
}

export function bindings(request) {
  return Object.fromEntries(['schemaVersion', 'invocationId', 'sourceRevision', 'releaseRunId', 'manifestSHA256', 'bundleSHA256', 'origin'].map(key => [key, request[key]]));
}

const metricKeys = ['transport', 'cacheConcurrency', 'fixtureCount', 'uncachedRequestStartP95Ms', 'cacheHitP95Ms', 'uncachedStartSamples', 'cacheSamples'];
export function renderingMetrics(value, allowExceeded = false) {
  require(value && Object.keys(value).every(key => metricKeys.includes(key)) && value.transport === 'http2' && value.cacheConcurrency === 20 && value.fixtureCount === 5);
  for (const [key, count, percentile, limit] of [['uncachedStartSamples', 5, 'uncachedRequestStartP95Ms', 2000], ['cacheSamples', 20, 'cacheHitP95Ms', 500]]) {
    require(Array.isArray(value[key]) && value[key].length === count && value[key].every(number => Number.isFinite(number) && number >= 0));
    const p95 = [...value[key]].sort((a, b) => a - b)[Math.ceil(count * 0.95) - 1];
    // Only reported decimal rounding is reconciled; both thresholds stay strict.
    require(Number.isFinite(value[percentile]) && value[percentile] >= 0 && Math.abs(value[percentile] - p95) <= 0.11);
    if (!allowExceeded) require(p95 <= limit && value[percentile] <= limit);
  }
  return Object.fromEntries(metricKeys.map(key => [key, value[key]]));
}

export function ingestMetrics(value) {
  require(value && Object.keys(value).length === 4 && ['batchId', 'assetId', 'manifestId'].every(key => uuid.test(value[key] ?? '')) && hex.test(value.sha256 ?? ''));
  return Object.fromEntries(['batchId', 'assetId', 'manifestId', 'sha256'].map(key => [key, value[key]]));
}

export function latencyFailure(stderr) {
  if (typeof stderr !== 'string') return undefined;
  for (const line of stderr.split('\n')) {
    try {
      const { phase, ...metrics } = JSON.parse(line);
      if (phase === 'rendering-latency-failure') return renderingMetrics(metrics, true);
    } catch { /* Raw child output must never escape this parser. */ }
  }
  return undefined;
}

export function validateResult(value, request, expectedArn) {
  require(value?.passed === true && value.region === 'ap-southeast-1' && value.functionArn === expectedArn && /^v24\.\d+\.\d+$/.test(value.runtime ?? ''));
  const expected = bindings(request);
  require(Object.entries(expected).every(([key, expectedValue]) => value[key] === expectedValue));
  require(Object.keys(value).every(key => Object.hasOwn(expected, key) || ['passed', 'region', 'functionArn', 'runtime', 'rendering', 'ingest'].includes(key)));
  return { ...expected, region: value.region, functionArn: value.functionArn, runtime: value.runtime, passed: true,
    rendering: renderingMetrics(value.rendering), ingest: ingestMetrics(value.ingest) };
}
