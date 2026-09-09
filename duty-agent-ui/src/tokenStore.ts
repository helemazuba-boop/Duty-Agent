// Token lives in memory per page load; sessionStorage mirrors it so a plain
// F5 inside the host WebView restores the current backend token instead of
// 401-ing until the host re-navigates. sessionStorage dies with the WebView
// session (app restart → new backend token), so a stale token never survives
// a host restart.
const STORAGE_KEY = 'duty_access_token';

let _token: string | null = null;
let _restored = false;

function restoreFromSessionStorage(): void {
  if (_restored) return;
  _restored = true;
  try {
    _token = sessionStorage.getItem(STORAGE_KEY);
  } catch {
    // Storage unavailable (privacy mode etc.) — memory-only fallback.
  }
}

export function setToken(token: string): void {
  _token = token;
  try {
    sessionStorage.setItem(STORAGE_KEY, token);
  } catch {
    // Memory-only fallback.
  }
}

export function getToken(): string | null {
  restoreFromSessionStorage();
  return _token;
}

export function clearToken(): void {
  _token = null;
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Memory-only fallback.
  }
}
