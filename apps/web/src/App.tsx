import { useEffect, useState } from 'react';
import { idToken, platformConfig } from './platform-config';

type Status = {
  ready?: string;
  release?: string;
  operationId?: string;
  message: string;
};

export function App() {
  const config = platformConfig();
  const [status, setStatus] = useState<Status>({
    message: config
      ? 'Sign in to check the private service status.'
      : 'Sign-in is configured after environment bootstrap.',
  });

  useEffect(() => {
    const token = idToken();
    if (!config || !token) return;
    window.history.replaceState({}, document.title, window.location.pathname);
    const headers = {
      Authorization: `Bearer ${token}`,
      'X-Operation-Id': `op_${crypto.randomUUID()}`,
    };
    fetch(`${config.apiBaseUrl}/v1/session`, { headers })
      .then(async (response) => {
        if (!response.ok)
          throw new Error('Authenticated session could not be verified.');
        const session = (await response.json()) as { operationId: string };
        const [ready, version] = await Promise.all([
          fetch(`${config.apiBaseUrl}/ready`, { headers }),
          fetch(`${config.apiBaseUrl}/v1/version`, { headers }),
        ]);
        if (!ready.ok || !version.ok)
          throw new Error(
            'Authenticated service status could not be verified.',
          );
        const readyBody = (await ready.json()) as { status: string };
        const versionBody = (await version.json()) as { releaseId: string };
        setStatus({
          ready: readyBody.status,
          release: versionBody.releaseId,
          operationId: session.operationId,
          message: 'Authenticated owner status loaded.',
        });
      })
      .catch((error) =>
        setStatus({
          message:
            error instanceof Error
              ? error.message
              : 'Authenticated service status could not be verified.',
        }),
      );
  }, [config]);

  function signIn() {
    if (!config) return;
    const authorize = new URL(`${config.cognitoHostedUiUrl}/oauth2/authorize`);
    authorize.search = new URLSearchParams({
      client_id: config.cognitoClientId,
      response_type: 'token',
      scope: 'openid',
      redirect_uri: config.redirectUri,
    }).toString();
    window.location.assign(authorize.toString());
  }

  return (
    <main>
      <h1>Darktable Library</h1>
      <section aria-labelledby="sign-in-heading">
        <h2 id="sign-in-heading">Owner sign-in</h2>
        <p>
          The private owner account is provisioned administratively; public
          registration is unavailable.
        </p>
        <button
          type="button"
          disabled={!config}
          onClick={signIn}
          aria-describedby="sign-in-status"
        >
          Sign in as owner
        </button>
        <p id="sign-in-status" role="status">
          {status.message}
        </p>
      </section>
      <section aria-labelledby="service-status-heading">
        <h2 id="service-status-heading">Service and deployment status</h2>
        <dl>
          <dt>Service</dt>
          <dd>{status.ready ?? 'Awaiting authenticated health check'}</dd>
          <dt>Release</dt>
          <dd>{status.release ?? 'Unavailable until owner sign-in'}</dd>
          <dt>Operation ID</dt>
          <dd>
            {status.operationId ?? 'Returned by the API on every request'}
          </dd>
        </dl>
      </section>
    </main>
  );
}
