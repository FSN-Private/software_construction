import { useEffect, useState } from 'react';

const messages = {
  backups: 'Backups',
  refresh: 'Refresh backup health',
  loading: 'Loading backup health.',
  unavailable:
    'Backup protection could not be verified. Refresh or contact the recovery operator.',
  healthy: 'All required data classes have verified recovery copies.',
  attention: 'Backup protection needs attention.',
  originals: 'Original photographs',
  xmp: 'XMP history',
  version_graph: 'Version graph',
  metadata: 'Library metadata',
  audit: 'Audit records',
  configuration: 'Configuration and access',
  recovery_records: 'Recovery evidence',
  verified: 'Verified',
  overdue: 'Overdue',
  incomplete: 'Incomplete',
  integrity_failed: 'Integrity check failed',
  unknown: 'Not verified',
  dataClass: 'Data class',
  state: 'Protection',
  coverage: 'Coverage',
  lastVerified: 'Last verified',
  age: 'Recovery point age (minutes)',
  observed: 'Health observed',
  lastAlert: 'Last alert',
  guidance:
    'Restore and promotion require the independently controlled recovery workflow. A successful job alone does not establish protection.',
};
type Message = keyof typeof messages;
// Same catalog/fallback convention as export-messages; translators can add locales.
export const recoveryTranslations: Record<string, Partial<typeof messages>> = {
  en: messages,
};
export function recoveryText(
  key: Message,
  locale = globalThis.navigator?.language ?? 'en',
): string {
  return (
    recoveryTranslations[locale]?.[key] ??
    recoveryTranslations[locale.split('-')[0]]?.[key] ??
    messages[key]
  );
}
type Protection = {
  dataClass: string;
  state: string;
  lastVerifiedAtUtc: string | null;
  recoveryPointAgeSeconds: number | null;
  coverage: number | null;
  integrity: string;
};
type Health = {
  status: string;
  observedAtUtc: string;
  classes: Protection[];
  exceptions: string[];
  lastAlertAtUtc: string | null;
};
const classes: Message[] = [
  'originals',
  'xmp',
  'version_graph',
  'metadata',
  'audit',
  'configuration',
  'recovery_records',
];
const states: Message[] = [
  'verified',
  'overdue',
  'incomplete',
  'integrity_failed',
];

export function RecoveryHealth({
  baseUrl,
  token,
}: {
  baseUrl: string;
  token: string;
}) {
  const [attempt, setAttempt] = useState(0);
  const [health, setHealth] = useState<Health>();
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const timer = window.setInterval(
      () => setAttempt((value) => value + 1),
      60_000,
    );
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    const abort = new AbortController();
    const timeout = window.setTimeout(() => {
      setFailed(true);
      setLoading(false);
      abort.abort();
    }, 5_000);
    setLoading(true);
    setFailed(false);
    // Discard old success immediately: a failed refresh must never look protected.
    setHealth(undefined);
    void fetch(`${baseUrl}/v1/recovery/health`, {
      headers: {
        Authorization: `Bearer ${token}`,
        'X-Operation-Id': `op_${crypto.randomUUID()}`,
      },
      cache: 'no-store',
      signal: abort.signal,
    })
      .then(async (response) => {
        if (response.status === 401)
          window.dispatchEvent(new Event('owner-session-expired'));
        if (!response.ok) throw new Error('recovery_unavailable');
        const value = (await response.json()) as Health;
        if (
          !value ||
          !Array.isArray(value.classes) ||
          !Array.isArray(value.exceptions)
        )
          throw new Error('recovery_unavailable');
        if (!abort.signal.aborted) setHealth(value);
      })
      .catch(() => {
        if (!abort.signal.aborted) setFailed(true);
      })
      .finally(() => {
        window.clearTimeout(timeout);
        if (!abort.signal.aborted) setLoading(false);
      });
    return () => {
      window.clearTimeout(timeout);
      abort.abort();
    };
  }, [baseUrl, token, attempt]);

  function timestamp(value: string | null | undefined) {
    const date = value ? new Date(value) : undefined;
    return date && Number.isFinite(date.getTime())
      ? date.toLocaleString(navigator.language, { timeZoneName: 'short' })
      : recoveryText('unknown');
  }
  const rows = classes.map((dataClass) => ({
    dataClass,
    protection: health?.classes.find((row) => row?.dataClass === dataClass),
  }));
  const verified =
    health?.status === 'healthy' &&
    health.exceptions.length === 0 &&
    rows.every(
      ({ protection: p }) =>
        p?.state === 'verified' &&
        p.integrity === 'verified' &&
        p.coverage === 1 &&
        typeof p.recoveryPointAgeSeconds === 'number' &&
        p.recoveryPointAgeSeconds >= 0 &&
        p.recoveryPointAgeSeconds <= 3600 &&
        p.lastVerifiedAtUtc &&
        Number.isFinite(Date.parse(p.lastVerifiedAtUtc)),
    );
  return (
    <section
      className="secondary-view"
      aria-labelledby="recovery-health-heading"
      aria-busy={loading}
    >
      <h2 id="recovery-health-heading">{recoveryText('backups')}</h2>
      <button
        aria-disabled={loading}
        onClick={() => {
          if (!loading) setAttempt((value) => value + 1);
        }}
      >
        {recoveryText('refresh')}
      </button>
      <p role={failed ? 'alert' : 'status'} aria-live="polite">
        {recoveryText(
          loading
            ? 'loading'
            : failed
              ? 'unavailable'
              : verified
                ? 'healthy'
                : 'attention',
        )}
      </p>
      {health && (
        <>
          <dl>
            <dt>{recoveryText('observed')}</dt>
            <dd>{timestamp(health.observedAtUtc)}</dd>
            <dt>{recoveryText('lastAlert')}</dt>
            <dd>{timestamp(health.lastAlertAtUtc)}</dd>
          </dl>
          <table>
            <caption>{recoveryText('backups')}</caption>
            <thead>
              <tr>
                <th scope="col">{recoveryText('dataClass')}</th>
                <th scope="col">{recoveryText('state')}</th>
                <th scope="col">{recoveryText('coverage')}</th>
                <th scope="col">{recoveryText('lastVerified')}</th>
                <th scope="col">{recoveryText('age')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ dataClass, protection: p }) => (
                <tr key={dataClass}>
                  <th scope="row">{recoveryText(dataClass)}</th>
                  <td>
                    {recoveryText(
                      states.includes(p?.state as Message)
                        ? (p!.state as Message)
                        : 'unknown',
                    )}
                  </td>
                  <td>
                    {typeof p?.coverage === 'number' &&
                    p.coverage >= 0 &&
                    p.coverage <= 1
                      ? new Intl.NumberFormat(navigator.language, {
                          style: 'percent',
                        }).format(p.coverage)
                      : recoveryText('unknown')}
                  </td>
                  <td>{timestamp(p?.lastVerifiedAtUtc)}</td>
                  <td>
                    {typeof p?.recoveryPointAgeSeconds === 'number' &&
                    Number.isFinite(p.recoveryPointAgeSeconds) &&
                    p.recoveryPointAgeSeconds >= 0
                      ? new Intl.NumberFormat(navigator.language, {
                          maximumFractionDigits: 1,
                        }).format(p.recoveryPointAgeSeconds / 60)
                      : recoveryText('unknown')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
      <p>{recoveryText('guidance')}</p>
    </section>
  );
}
