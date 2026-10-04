import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { idToken, platformConfig } from './platform-config';
import { Library } from './Library';
import { Gallery } from './Gallery';
import { BranchSwitcher } from './BranchSwitcher';
import { LibraryHistory } from './LibraryHistory';
import { ExportBasket } from './ExportBasket';
import { exportApi } from './export-api';
import { exportText } from './export-messages';
import { Modal } from './Modal';
import { reauthenticationReturnPath } from './recent-auth';
import './workspace.css';

type Status = {
  ready?: string;
  release?: string;
  operationId?: string;
  message: string;
};

function tokenExpiry(token: string | undefined): number {
  try {
    const payload =
      token?.split('.')[1]?.replace(/-/g, '+').replace(/_/g, '/') ?? '';
    const exp = JSON.parse(atob(payload)).exp;
    return typeof exp === 'number' && Number.isFinite(exp)
      ? exp * 1000
      : Infinity;
  } catch {
    return Infinity;
  }
}

export function App() {
  const config = platformConfig();
  const [token, setToken] = useState(idToken);
  const [expired, setExpired] = useState(false);
  const [signOutError, setSignOutError] = useState('');
  const [view, setView] = useState('gallery');
  const scrollPositions = useRef<Record<string, number>>({});
  function openView(next: string) {
    if (
      next === 'exports' &&
      next !== view &&
      !window.dispatchEvent(new Event('gallery-guard', { cancelable: true }))
    )
      return;
    scrollPositions.current[view] = window.scrollY;
    setView(next);
  }
  useLayoutEffect(() => {
    window.scrollTo(0, scrollPositions.current[view] ?? 0);
  }, [view]);
  const [libraryOpened, setLibraryOpened] = useState(false);
  const [branch, setBranch] = useState(
    () => new URLSearchParams(window.location.search).get('branch') ?? 'main',
  );
  const [switching, setSwitching] = useState(false);
  const [mutationsEnabled, setMutationsEnabled] = useState(false);
  const [status, setStatus] = useState<Status>({
    message: config
      ? 'Sign in to your private photo library.'
      : 'Sign-in is configured after environment bootstrap.',
  });

  useEffect(() => {
    if (!config || !token) return;
    const abort = new AbortController();
    const returnPath = reauthenticationReturnPath();
    window.history.replaceState(
      window.history.state,
      '',
      returnPath ?? window.location.pathname + window.location.search,
    );
    const headers = {
      Authorization: `Bearer ${token}`,
      'X-Operation-Id': `op_${crypto.randomUUID()}`,
    };
    void (async () => {
      const session = await fetch(`${config.apiBaseUrl}/v1/session`, {
        headers,
        signal: abort.signal,
      });
      if (!session.ok)
        throw new Error('Owner session expired or unavailable. Sign in again.');
      const body = await session.json();
      setStatus({
        ready: 'verified',
        operationId: body.operationId,
        message: 'Authenticated owner session.',
      });
      const [ready, version] = await Promise.all([
        fetch(`${config.apiBaseUrl}/ready`, { headers, signal: abort.signal }),
        fetch(`${config.apiBaseUrl}/v1/version`, {
          headers,
          signal: abort.signal,
        }),
      ]);
      if (ready.status === 401 || version.status === 401)
        window.dispatchEvent(new Event('owner-session-expired'));
      const readyBody = ready.ok ? await ready.json() : {};
      const versionBody = version.ok ? await version.json() : {};
      if (!abort.signal.aborted)
        setStatus({
          ready: readyBody.status ?? 'verified',
          release: versionBody.releaseId,
          operationId: body.operationId,
          message: 'Authenticated owner status loaded.',
        });
    })().catch((error) => {
      if (!abort.signal.aborted)
        setStatus({
          message:
            error instanceof Error
              ? error.message
              : 'Owner session unavailable.',
        });
    });
    return () => abort.abort();
  }, [token]);

  useEffect(() => {
    const update = () =>
      setBranch(
        new URLSearchParams(window.location.search).get('branch') ?? 'main',
      );
    const expire = () => {
      setExpired(true);
      setSwitching(false);
    };
    window.addEventListener('popstate', update);
    window.addEventListener('gallery-navigation', update);
    window.addEventListener('owner-session-expired', expire);
    // Only actual JWTs have an expiry; fixture tokens intentionally do not.
    const expiry = tokenExpiry(token);
    const timer = window.setInterval(() => {
      if (Date.now() >= expiry) expire();
    }, 1000);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener('popstate', update);
      window.removeEventListener('gallery-navigation', update);
      window.removeEventListener('owner-session-expired', expire);
    };
  }, [token]);

  function signIn() {
    if (!config) return;
    const state = crypto.randomUUID();
    sessionStorage.setItem(
      `lineage.reauth.${state}`,
      window.location.pathname + window.location.search,
    );
    const authorize = new URL(`${config.cognitoHostedUiUrl}/oauth2/authorize`);
    authorize.search = new URLSearchParams({
      client_id: config.cognitoClientId,
      response_type: 'token',
      scope: 'openid',
      redirect_uri: config.redirectUri,
      state,
    }).toString();
    window.location.assign(authorize.toString());
  }
  async function signOut() {
    const bearerExpired = Date.now() >= tokenExpiry(token);
    // Download grants never outlive the bearer that minted them.
    if (
      config?.exportsEnabled &&
      config.galleryLibraryId &&
      token &&
      !bearerExpired
    ) {
      try {
        await exportApi(config.apiBaseUrl, token, config.galleryLibraryId)(
          '/session/revoke',
          'POST',
          {},
        );
      } catch {
        setSignOutError(
          exportText(
            'Download-session revocation failed. Try signing out again.',
          ),
        );
        return;
      }
    }
    window.dispatchEvent(new Event('gallery-sign-out'));
    setSignOutError('');
    setToken(undefined);
    setExpired(false);
    setStatus({ message: 'You have signed out.' });
    for (const key of Object.keys(sessionStorage))
      if (key.startsWith('gallery.draft.') || key.startsWith('lineage.reauth.'))
        sessionStorage.removeItem(key);
    window.history.replaceState(null, '', window.location.pathname);
    if (config) {
      const logout = new URL(`${config.cognitoHostedUiUrl}/logout`);
      logout.search = new URLSearchParams({
        client_id: config.cognitoClientId,
        logout_uri: config.redirectUri,
      }).toString();
      window.location.assign(logout.toString());
    }
  }
  async function openBranches() {
    if (!config?.galleryLibraryId || !token) return;
    if (!window.dispatchEvent(new Event('gallery-guard', { cancelable: true })))
      return;
    setSwitching(true);
    setMutationsEnabled(false);
    try {
      const response = await fetch(
        `${config.apiBaseUrl}/v1/libraries/${encodeURIComponent(config.galleryLibraryId)}/lineage/capabilities`,
        { headers: { Authorization: `Bearer ${token}` } },
      );
      if (response.status === 401)
        window.dispatchEvent(new Event('owner-session-expired'));
      if (response.ok)
        setMutationsEnabled((await response.json()).mutationsEnabled);
    } catch {
      /* Existing branch component explains unavailable state. */
    }
  }
  const signedIn = !!(config && token && status.ready);
  return (
    <main>
      <header className="workspace-header">
        <h1>
          <svg
            aria-hidden="true"
            width="24"
            height="24"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
          >
            <rect x="5" y="7" width="16" height="13" rx="1.5" />
            <path d="M17 7V4H2v13h3M6 17l4-4 3 3 3-5 4 6" />
            <circle cx="10" cy="10" r="1" />
          </svg>{' '}
          <span title="Darktable Library">Library</span>
        </h1>
        {signedIn && !expired && (
          <>
            <nav aria-label="Workspace">
              <button
                className={view === 'gallery' ? 'active' : ''}
                aria-current={view === 'gallery' ? 'page' : undefined}
                onClick={() => openView('gallery')}
              >
                Gallery
              </button>
              <button
                className={view === 'history' ? 'active' : ''}
                aria-current={view === 'history' ? 'page' : undefined}
                onClick={() => openView('history')}
              >
                History
              </button>
              <button
                className={view === 'exports' ? 'active' : ''}
                hidden={!config.exportsEnabled}
                aria-current={view === 'exports' ? 'page' : undefined}
                onClick={() => openView('exports')}
              >
                {exportText('Export baskets')}
              </button>
            </nav>
            <button
              className="branch-control"
              onClick={() => void openBranches()}
              aria-label={`Web branch ${branch}`}
            >
              <span aria-hidden="true">⑂</span> {branch}{' '}
              <span aria-hidden="true">⌄</span>
            </button>
            <div className="header-actions">
              <button
                onClick={() => {
                  setLibraryOpened(true);
                  openView('library');
                }}
              >
                ↑ Import
              </button>
              <details className="owner-menu">
                <summary aria-label="Owner controls">Owner</summary>
                <div>
                  <button onClick={() => openView('diagnostics')}>
                    Service details
                  </button>
                  <button onClick={signOut}>Sign out</button>
                </div>
              </details>
            </div>
          </>
        )}
      </header>
      {signOutError && <p role="alert">{signOutError}</p>}
      {(!signedIn || expired) && (
        <section className="sign-in" aria-labelledby="sign-in-heading">
          <span className="eyebrow">YOUR PRIVATE PHOTO LIBRARY</span>
          <h2 id="sign-in-heading">A little space for your photographs.</h2>
          <p>
            The private owner account is provisioned administratively; public
            registration is unavailable.
          </p>
          <button className="primary" disabled={!config} onClick={signIn}>
            Sign in as owner
          </button>
          {expired && (
            <button onClick={signOut}>{exportText('Sign out')}</button>
          )}
          <p role="status">
            {expired
              ? 'Your session expired. Sign in again; unsaved edits are retained for review.'
              : status.message}
          </p>
        </section>
      )}
      {signedIn && (
        <div hidden={expired}>
          {config.exportsEnabled &&
            config.galleryLibraryId &&
            view === 'exports' && (
              <ExportBasket
                baseUrl={config.apiBaseUrl}
                token={token}
                library={config.galleryLibraryId}
              />
            )}
          {config.galleryLibraryId && (
            <div hidden={view !== 'gallery'}>
              <Gallery
                baseUrl={config.apiBaseUrl}
                token={token}
                library={config.galleryLibraryId}
                onLibrary={() => {
                  setLibraryOpened(true);
                  openView('library');
                }}
                onBranch={() => void openBranches()}
              />
            </div>
          )}
          {!config.galleryLibraryId && view === 'gallery' && (
            <p role="status">
              The gallery library is not configured. Use Import to access
              library controls.
            </p>
          )}
          {view === 'history' && config.galleryLibraryId && (
            <LibraryHistory
              baseUrl={config.apiBaseUrl}
              token={token}
              library={config.galleryLibraryId}
              branch={branch}
              onImage={(asset) => {
                const next = new URLSearchParams(window.location.search);
                next.set('asset', asset);
                next.set('history', '1');
                window.history.pushState({}, '', `?${next}`);
                window.dispatchEvent(new PopStateEvent('popstate'));
                scrollPositions.current.gallery = 0;
                openView('gallery');
              }}
            />
          )}
          {libraryOpened && (
            <section hidden={view !== 'library'} className="secondary-view">
              <button onClick={() => openView('gallery')}>
                ← Back to gallery
              </button>
              <Library
                baseUrl={config.apiBaseUrl}
                token={token}
                previewMode={config.previewMode}
              />
            </section>
          )}
          <section
            hidden={view !== 'diagnostics'}
            className="secondary-view"
            aria-labelledby="service-status-heading"
          >
            <button onClick={() => openView('gallery')}>
              ← Back to gallery
            </button>
            <h2 id="service-status-heading">Service and deployment status</h2>
            <dl>
              <dt>Service</dt>
              <dd>{status.ready}</dd>
              <dt>Release</dt>
              <dd>{status.release ?? 'Unavailable'}</dd>
              <dt>Operation ID</dt>
              <dd>{status.operationId ?? 'Unavailable'}</dd>
            </dl>
          </section>
        </div>
      )}
      {switching && signedIn && !expired && (
        <Modal title="Switch web branch" onClose={() => setSwitching(false)}>
          <BranchSwitcher
            baseUrl={config.apiBaseUrl}
            token={token}
            library={config.galleryLibraryId!}
            branch={branch}
            mutationsEnabled={mutationsEnabled}
            onSwitched={(value) => {
              const next = new URLSearchParams(window.location.search);
              next.set('branch', value);
              next.delete('cursor');
              window.history.pushState({}, '', `?${next}`);
              window.dispatchEvent(new PopStateEvent('popstate'));
              setBranch(value);
              setSwitching(false);
            }}
          />
        </Modal>
      )}
    </main>
  );
}
