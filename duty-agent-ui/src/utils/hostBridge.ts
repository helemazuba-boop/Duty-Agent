/**
 * WebView2 宿主桥:web ↔ WinForms 双向消息。
 * web → 宿主:sendToHost(宿主在 WebMessageReceived 里分发);
 * 宿主 → web:宿主 PostWebMessageAsJson,这里 onHostMessage 接收。
 * 浏览器环境(无 chrome.webview)下全部安全降级。
 */

interface WebView2Host {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

const getHost = (): WebView2Host | null =>
  (window as unknown as { chrome?: { webview?: WebView2Host } }).chrome?.webview ?? null;

/** 是否运行在 Duty-Agent 桌面客户端 WebView2 内 */
export function hasHost(): boolean {
  return getHost() !== null;
}

/** 向宿主发消息;不在客户端环境或发送失败时返回 false */
export function sendToHost(message: Record<string, unknown>): boolean {
  const host = getHost();
  if (!host) return false;
  try {
    host.postMessage(message);
    return true;
  } catch {
    return false;
  }
}

/** 监听宿主回传的消息,返回取消监听函数 */
export function onHostMessage(handler: (payload: Record<string, unknown>) => void): () => void {
  const host = getHost();
  if (!host) return () => {};
  const listener = (event: { data: unknown }) => {
    if (event.data && typeof event.data === 'object') {
      handler(event.data as Record<string, unknown>);
    }
  };
  host.addEventListener('message', listener);
  return () => host.removeEventListener('message', listener);
}
