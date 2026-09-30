export interface PlatformConfig {
  readonly apiBaseUrl: string;
  readonly cognitoHostedUiUrl: string;
  readonly cognitoClientId: string;
  readonly redirectUri: string;
  readonly galleryLibraryId?: string;
  readonly previewMode?: boolean;
}

declare global {
  interface Window {
    __PLATFORM_CONFIG__?: Partial<PlatformConfig>;
  }
}

export function platformConfig(): PlatformConfig | undefined {
  const config = window.__PLATFORM_CONFIG__;
  if (
    !config?.apiBaseUrl ||
    !config.cognitoHostedUiUrl ||
    !config.cognitoClientId ||
    !config.redirectUri
  )
    return undefined;
  return config as PlatformConfig;
}

export function idToken(): string | undefined {
  return (
    new URLSearchParams(window.location.hash.slice(1)).get('id_token') ??
    undefined
  );
}
