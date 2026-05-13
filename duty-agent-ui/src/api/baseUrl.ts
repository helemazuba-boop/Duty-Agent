const rawApiBaseUrl = import.meta.env.VITE_API_BASE_URL?.trim() ?? '';

export const API_BASE_URL = rawApiBaseUrl.replace(/\/+$/, '');

export function apiUrl(path: string): string {
  if (!API_BASE_URL) return path;
  return `${API_BASE_URL}${path.startsWith('/') ? path : `/${path}`}`;
}

export function wsUrl(path: string): string {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;

  if (!API_BASE_URL) {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${window.location.host}${normalizedPath}`;
  }

  const url = new URL(API_BASE_URL);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.pathname = `${url.pathname.replace(/\/+$/, '')}${normalizedPath}`;
  url.search = '';
  url.hash = '';
  return url.toString();
}

