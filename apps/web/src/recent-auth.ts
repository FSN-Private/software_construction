import { platformConfig } from './platform-config';

const prefix = 'lineage.reauth.';

export function beginRecentAuthentication() {
  const config = platformConfig();
  if (!config) return false;
  const state = crypto.randomUUID();
  sessionStorage.setItem(
    prefix + state,
    window.location.pathname + window.location.search,
  );
  const logout = new URL(`${config.cognitoHostedUiUrl}/logout`);
  logout.search = new URLSearchParams({
    client_id: config.cognitoClientId,
    response_type: 'token',
    scope: 'openid',
    redirect_uri: config.redirectUri,
    state,
  }).toString();
  window.location.assign(logout.toString());
  return true;
}

export function reauthenticationReturnPath() {
  const state = new URLSearchParams(window.location.hash.slice(1)).get('state');
  if (!state || !/^[0-9a-f-]{36}$/.test(state)) return undefined;
  const key = prefix + state;
  const path = sessionStorage.getItem(key);
  sessionStorage.removeItem(key);
  return path?.startsWith('/') && !path.startsWith('//') ? path : undefined;
}
