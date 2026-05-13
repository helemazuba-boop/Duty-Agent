const tokenStorageKey = 'duty_access_token';

function getHashToken(hash: string): string | null {
  const rawHash = hash.startsWith('#') ? hash.slice(1) : hash;
  if (!rawHash) return null;

  if (rawHash.startsWith('access_token=')) {
    return new URLSearchParams(rawHash).get('access_token');
  }

  const queryIndex = rawHash.indexOf('?');
  if (queryIndex >= 0) {
    return new URLSearchParams(rawHash.slice(queryIndex + 1)).get('access_token');
  }

  const legacyMatch = rawHash.match(/(?:^|&)access_token=([^&]+)/);
  return legacyMatch ? decodeURIComponent(legacyMatch[1]) : null;
}

function getUrlToken(): string | null {
  return new URLSearchParams(window.location.search).get('access_token') ?? getHashToken(window.location.hash);
}

function stripAccessTokenFromSearch(search: string): string {
  const params = new URLSearchParams(search);
  params.delete('access_token');
  const nextSearch = params.toString();
  return nextSearch ? `?${nextSearch}` : '';
}

function stripAccessTokenFromHash(hash: string): string {
  if (!hash.startsWith('#/')) {
    return '#/dashboard';
  }

  const rawHash = hash.slice(1);
  const [pathPart, query = ''] = rawHash.split('?');
  const params = new URLSearchParams(query);
  params.delete('access_token');
  const nextQuery = params.toString();
  return `#${pathPart || '/dashboard'}${nextQuery ? `?${nextQuery}` : ''}`;
}

const urlToken = getUrlToken();
const devToken = (window as any).__DEV_TOKEN__;
const activeToken = devToken ?? urlToken;

if (activeToken) {
  localStorage.setItem(tokenStorageKey, activeToken);
}

if (urlToken) {
  const nextUrl = `${window.location.pathname}${stripAccessTokenFromSearch(window.location.search)}${stripAccessTokenFromHash(window.location.hash)}`;
  window.history.replaceState(null, '', nextUrl);
}

