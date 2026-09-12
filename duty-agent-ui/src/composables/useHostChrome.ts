/**
 * 自定义窗口铬的状态与几何:web 画标题条和窗口三键,
 * 拖拽/缩放/贴靠仍由宿主 WM_NCHITTEST 完成,几何经消息桥同步(CSS px)。
 */
import { ref } from 'vue';
import { onHostMessage, sendToHost } from '@/utils/hostBridge';

/** 标题条高度,须与 AppLayout 的 .app-titlebar 保持一致 */
export const TITLEBAR_HEIGHT = 44;
/** 窗口三键总宽:3 × 46px */
export const CONTROLS_WIDTH = 138;

const customChrome = ref(false);
const maximized = ref(false);
let initialized = false;

function sendGeometry() {
  if (customChrome.value) {
    sendToHost({
      type: 'chrome-geometry',
      titlebarHeight: TITLEBAR_HEIGHT,
      controlsWidth: CONTROLS_WIDTH,
    });
  }
}

function handleMessage(msg: Record<string, unknown>) {
  if (msg.type === 'host-info') {
    customChrome.value = msg.customChrome === true;
    sendGeometry();
  } else if (msg.type === 'window-state') {
    maximized.value = msg.maximized === true;
  }
}

function ensureInit() {
  if (initialized) return;
  initialized = true;
  onHostMessage(handleMessage);
  // DPI/窗口尺寸变化时宿主重算命中区(CSS px 不随窗口尺寸变,DPR 变了要重报)
  window.addEventListener('resize', sendGeometry);
  // 宿主推送可能早于本模块初始化,主动拉一次兜底
  sendToHost({ type: 'get-host-info' });
}

export function useHostChrome() {
  ensureInit();
  return { customChrome, maximized };
}

export type WindowCommand = 'minimize' | 'maximize-toggle' | 'close';

export function sendWindowCommand(command: WindowCommand) {
  sendToHost({ type: 'window-command', command });
}
