/**
 * 主题切换:light / dark / auto(跟随系统)。
 * 首帧由 index.html 的预置脚本写 data-theme(避免深色用户看到一帧白),
 * 这里负责运行时切换、持久化,并生成 ConfigProvider 的 theme 对象。
 * CSS 变量由 styles/tokens.generated.css 的 [data-theme] 块提供。
 */
import { computed, ref, watchEffect } from 'vue';
import { makeAntdTheme, type ResolvedTheme } from './antd';
import { safeStorage } from '@/utils/safeStorage';
import { sendToHost } from '@/utils/hostBridge';

export type ThemeMode = 'light' | 'dark' | 'auto';

const STORAGE_KEY = 'duty_theme_mode';
const systemPrefersDark =
  typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)')
    : null;

function readStoredMode(): ThemeMode {
  const raw = safeStorage.get(STORAGE_KEY);
  if (raw === 'light' || raw === 'dark' || raw === 'auto') return raw;
  // 桌面内部工具,跟随系统是最不打扰的默认
  return 'auto';
}

const mode = ref<ThemeMode>(readStoredMode());

const systemDark = ref(systemPrefersDark?.matches ?? false);
systemPrefersDark?.addEventListener?.('change', (e) => {
  systemDark.value = e.matches;
});

const resolved = computed<ResolvedTheme>(() => {
  if (mode.value === 'auto') return systemDark.value ? 'dark' : 'light';
  return mode.value;
});

watchEffect(() => {
  safeStorage.set(STORAGE_KEY, mode.value);
});

watchEffect(() => {
  if (typeof document === 'undefined') return;
  const theme = resolved.value;
  document.documentElement.dataset.theme = theme;

  // 同步给 WinForms 宿主:DWM 标题栏/窗口底色需要具体色值,而不是"深/浅"开关
  const bg = getComputedStyle(document.documentElement).getPropertyValue('--dt-bg').trim();
  sendToHost({ type: 'theme-sync', dark: theme === 'dark', bg });
});

export function useTheme() {
  const setMode = (next: ThemeMode) => {
    mode.value = next;
  };

  /** 循环切换 light → dark → auto */
  const cycleMode = () => {
    mode.value = mode.value === 'light' ? 'dark' : mode.value === 'dark' ? 'auto' : 'light';
  };

  return {
    mode,
    resolved,
    antdTheme: computed(() => makeAntdTheme(resolved.value)),
    setMode,
    cycleMode,
  };
}
