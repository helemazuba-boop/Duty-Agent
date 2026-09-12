<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';
import { Tooltip } from 'ant-design-vue';
import {
  BulbFilled,
  BulbOutlined,
  CalendarOutlined,
  DashboardOutlined,
  DesktopOutlined,
  SettingOutlined,
  TeamOutlined,
  ThunderboltOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
} from '@ant-design/icons-vue';
import StatusDot from '@/components/ui/StatusDot.vue';
import { useTheme, type ThemeMode } from '@/theme/useTheme';
import { safeStorage } from '@/utils/safeStorage';
import { useBackendConnection } from '@/composables/useBackendConnection';
import { useHostChrome, sendWindowCommand } from '@/composables/useHostChrome';
import { sendToHost } from '@/utils/hostBridge';
import appLogo from '@/assets/icon.png';

const { mode, resolved, cycleMode } = useTheme();
const { customChrome, maximized } = useHostChrome();

/** 标题条按下:非三键区域(左键)交给宿主发起原生拖拽;双击最大化在 dblclick 里处理 */
const onTitlebarPointerDown = (e: PointerEvent) => {
  if (e.button !== 0) return;
  if ((e.target as HTMLElement).closest('.wtc-btn')) return;
  sendToHost({ type: 'drag-window' });
};

/** 边缘缩放:宿主用 WM_NCLBUTTONDOWN + 边缘 HT 值拉起原生缩放循环 */
const EDGE_HIT: Record<string, number> = { top: 12, bottom: 15, left: 10, right: 11, nw: 13, ne: 14, sw: 16, se: 17 };
const onEdgePointerDown = (e: PointerEvent, edge: string) => {
  if (e.button !== 0) return;
  sendToHost({ type: 'resize-window', hit: EDGE_HIT[edge] });
};
const route = useRoute();
const activeKey = computed(() => route.path.split('/')[1] || 'dashboard');

/** 侧栏形态:收起 56px icon rail / 展开 200px;默认收起 */
const RAIL_KEY = 'duty_sidebar_expanded';
const expanded = ref(safeStorage.get(RAIL_KEY) === '1');
const toggleExpanded = () => {
  expanded.value = !expanded.value;
  safeStorage.set(RAIL_KEY, expanded.value ? '1' : '0');
};

const navItems = [
  { key: 'dashboard', icon: DashboardOutlined, label: '仪表盘', to: '/dashboard' },
  { key: 'arrangement', icon: CalendarOutlined, label: '排班安排', to: '/arrangement' },
  { key: 'roster', icon: TeamOutlined, label: '花名册', to: '/roster' },
  { key: 'schedule', icon: ThunderboltOutlined, label: 'AI 排班', to: '/schedule' },
  { key: 'settings', icon: SettingOutlined, label: '设置', to: '/settings' },
];

// ======== 连接状态:常驻侧栏底部,断线变红;点击=重新检测 ========
// 数据源统一走 useBackendConnection 单例（20s 轮询在 composable 内）。
const { status: connStatus, checking, errorKind, checkNow, start, stop } = useBackendConnection();

/* pending 只在首次未知时出现,轮询期间不让状态点闪灰 */
const connectionStatus = computed<'ok' | 'error' | 'pending'>(() => connStatus.value);

const connectionLabel = computed(() => {
  if (connectionStatus.value === 'ok') return '已连接';
  if (connectionStatus.value === 'error') {
    return errorKind.value === 'unauthorized' ? '登录失效' : '未连接';
  }
  return '检测中';
});

const connectionTooltip = computed(() => {
  if (connectionStatus.value === 'error') {
    if (errorKind.value === 'unauthorized') {
      return '登录状态已失效 · 点击重新检测(也可重启 Duty-Agent 客户端)';
    }
    return '后端未连接 · 点击重新检测(也可到「设置」排查)';
  }
  return `后端${connectionLabel.value} · 点击重新检测`;
});

// ======== 主题三态:浅 BulbOutlined / 深 BulbFilled / 跟随系统 DesktopOutlined ========
const themeIcon = computed(() => {
  if (mode.value === 'light') return BulbOutlined;
  if (mode.value === 'dark') return BulbFilled;
  return DesktopOutlined;
});
const themeTooltip = computed(() => {
  const labels: Record<ThemeMode, string> = { light: '浅色', dark: '深色', auto: '跟随系统' };
  return `主题:${labels[mode.value]}(点击切换)`;
});

onMounted(() => {
  start();
});

onBeforeUnmount(() => {
  stop();
});
</script>

<template>
  <div class="app-shell" :class="{ 'app-shell--expanded': expanded }">
    <!-- ======== 窗口边缘缩放热区:按下交给宿主拉起原生缩放循环(最大化时隐藏) ======== -->
    <template v-if="customChrome && !maximized">
      <div class="win-edge win-edge--top" @pointerdown="onEdgePointerDown($event, 'top')" />
      <div class="win-edge win-edge--bottom" @pointerdown="onEdgePointerDown($event, 'bottom')" />
      <div class="win-edge win-edge--left" @pointerdown="onEdgePointerDown($event, 'left')" />
      <div class="win-edge win-edge--right" @pointerdown="onEdgePointerDown($event, 'right')" />
      <div class="win-edge win-edge--nw" @pointerdown="onEdgePointerDown($event, 'nw')" />
      <div class="win-edge win-edge--ne" @pointerdown="onEdgePointerDown($event, 'ne')" />
      <div class="win-edge win-edge--sw" @pointerdown="onEdgePointerDown($event, 'sw')" />
      <div class="win-edge win-edge--se" @pointerdown="onEdgePointerDown($event, 'se')" />
    </template>

    <!-- ======== 全高侧栏(icon rail) ======== -->
    <aside class="app-sidebar">
      <!-- 品牌:收起只留 logo,展开显示名称 -->
      <div class="app-brand" title="Duty-Agent">
        <img class="app-brand__logo" :src="appLogo" alt="Duty-Agent" />
        <span class="app-brand__name">Duty-Agent</span>
      </div>

      <nav aria-label="主导航" class="app-nav">
        <Tooltip v-for="item in navItems" :key="item.key" :title="expanded ? '' : item.label" placement="right">
          <RouterLink
            :to="item.to"
            class="app-nav-item"
            :class="{ 'app-nav-item--active': activeKey === item.key }"
            :aria-label="item.label"
            :aria-current="activeKey === item.key ? 'page' : undefined"
          >
            <component :is="item.icon" class="app-nav-item__icon" />
            <span class="app-nav-item__label">{{ item.label }}</span>
          </RouterLink>
        </Tooltip>
      </nav>

      <div class="app-sidebar__spacer" />

      <div class="app-sidebar__footer">
        <Tooltip :title="connectionTooltip" placement="right">
          <button type="button" class="app-conn" :aria-label="`后端${connectionLabel},点击重新检测`" @click="checkNow">
            <StatusDot :status="connectionStatus" :pulse="connectionStatus === 'pending' || checking" />
            <span class="app-conn__label">{{ connectionLabel }}</span>
          </button>
        </Tooltip>

        <Tooltip :title="themeTooltip" placement="right">
          <button type="button" class="app-sidebar__btn" :aria-label="themeTooltip" @click="cycleMode">
            <component :is="themeIcon" class="app-sidebar__btn-icon" />
            <span class="app-sidebar__btn-label">
              {{ mode === 'auto' ? '跟随系统' : resolved === 'dark' ? '深色' : '浅色' }}
            </span>
            <span v-if="mode === 'auto'" class="app-sidebar__auto-dot" />
          </button>
        </Tooltip>

        <Tooltip :title="expanded ? '收起侧栏' : '展开侧栏'" placement="right">
          <button type="button" class="app-sidebar__btn" :aria-label="expanded ? '收起侧栏' : '展开侧栏'" @click="toggleExpanded">
            <MenuUnfoldOutlined v-if="!expanded" />
            <MenuFoldOutlined v-else />
          </button>
        </Tooltip>
      </div>
    </aside>

    <!-- ======== 右列:标题条 + 内容区 ======== -->
    <div class="app-right">
      <!-- 自定义标题条:整条是宿主的原生拖拽区,双击最大化;三键走窗口命令 -->
      <div
        v-if="customChrome"
        class="app-titlebar"
        @pointerdown="onTitlebarPointerDown"
        @dblclick="sendWindowCommand('maximize-toggle')"
      >
        <div class="app-titlebar__controls">
          <button type="button" class="wtc-btn" aria-label="最小化" @click="sendWindowCommand('minimize')">
            <svg viewBox="0 0 10 10" stroke="currentColor" stroke-width="1" fill="none"><path d="M0 5.5h10" /></svg>
          </button>
          <button
            type="button"
            class="wtc-btn"
            :aria-label="maximized ? '还原' : '最大化'"
            @click="sendWindowCommand('maximize-toggle')"
          >
            <svg v-if="!maximized" viewBox="0 0 10 10" stroke="currentColor" stroke-width="1" fill="none">
              <rect x="0.5" y="0.5" width="9" height="9" />
            </svg>
            <svg v-else viewBox="0 0 10 10" stroke="currentColor" stroke-width="1" fill="none">
              <path d="M2.5 2.5v-2h7v7h-2" />
              <rect x="0.5" y="2.5" width="7" height="7" />
            </svg>
          </button>
          <button type="button" class="wtc-btn wtc-btn--close" aria-label="关闭" @click="sendWindowCommand('close')">
            <svg viewBox="0 0 10 10" stroke="currentColor" stroke-width="1" fill="none">
              <path d="M0 0l10 10M10 0L0 10" />
            </svg>
          </button>
        </div>
      </div>

      <!-- 内容区:滚动发生在 main,不在 body -->
      <main class="app-main">
        <!-- 画布氛围:光晕随内容滚动(页头滚动态的底色才能与画布无缝同色) -->
        <div class="app-canvas" aria-hidden="true" />
        <div class="app-content">
          <RouterView />
        </div>
      </main>
    </div>
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  height: 100vh;
  background: var(--dt-bg);
}

/* ---- 画布氛围:左上主色光晕 + 右上 live 色辅助光晕,叠一层细噪点防色带 ----
   绝对定位挂在滚动容器顶部,随内容一起滚走:滚动态页头的底色才能与画布无缝同色 ---- */
.app-canvas {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 640px;
  pointer-events: none;
  background:
    radial-gradient(1400px 560px at 22% -80px, color-mix(in srgb, var(--dt-primary) 11%, transparent), transparent 72%),
    radial-gradient(900px 460px at 90% -140px, color-mix(in srgb, var(--dt-live) 7%, transparent), transparent 74%);
}

.app-canvas::after {
  content: '';
  position: absolute;
  inset: 0;
  opacity: 0.025;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.85' numOctaves='2' stitchTiles='stitch'/%3E%3CfeColorMatrix type='saturate' values='0'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E");
}

/* 深色底上光晕要更亮一档才可见,噪点同样略加强 */
html[data-theme='dark'] .app-canvas {
  background:
    radial-gradient(1100px 460px at 20% -120px, color-mix(in srgb, var(--dt-primary) 17%, transparent), transparent 70%),
    radial-gradient(820px 400px at 88% -150px, color-mix(in srgb, var(--dt-live) 8%, transparent), transparent 72%);
}

/* 标题条延续层:与深色画布同参数(1100/820 体系),中心上移 44px 衔接 */
html[data-theme='dark'] .app-titlebar {
  background:
    radial-gradient(1100px 460px at 20% -164px, color-mix(in srgb, var(--dt-primary) 17%, transparent), transparent 70%),
    radial-gradient(820px 400px at 88% -194px, color-mix(in srgb, var(--dt-live) 8%, transparent), transparent 72%);
}

html[data-theme='dark'] .app-canvas::after {
  opacity: 0.04;
}

/* ---- 全高侧栏:flex 子项,与右列并排,直通窗口顶 ---- */
.app-sidebar {
  position: relative;
  z-index: 1;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  width: 56px;
  padding: 12px 8px;
  background: var(--dt-surface);
  border-right: 1px solid var(--dt-border);
  transition: width 0.18s ease;
  overflow: hidden;
}

.app-shell--expanded .app-sidebar {
  width: 200px;
}

.app-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 44px;
  padding: 0 7px;
  margin-bottom: 12px;
  flex-shrink: 0;
}

.app-brand__logo {
  display: block;
  width: 26px;
  height: 25px;
}

.app-brand__name {
  font-size: 14px;
  font-weight: 600;
  color: var(--dt-text);
  white-space: nowrap;
}

/* label 淡入:等宽度撑开再出现,避免文字被裁切的错位感 */
.app-brand__name,
.app-nav-item__label,
.app-conn__label,
.app-sidebar__btn-label {
  opacity: 0;
  transition: opacity 0.12s ease;
}

.app-shell--expanded :is(.app-brand__name, .app-nav-item__label, .app-conn__label, .app-sidebar__btn-label) {
  opacity: 1;
  transition-delay: 0.08s;
}

/* ---- 导航项 ---- */
.app-nav {
  display: flex;
  flex-direction: column;
}

.app-nav-item {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 40px;
  padding: 0 11px;
  margin-bottom: 2px;
  border-radius: 8px;
  color: var(--dt-text-2);
  text-decoration: none;
  transition: background 0.15s ease, color 0.15s ease;
  white-space: nowrap;
}

.app-nav-item:hover {
  background: var(--dt-hover);
  color: var(--dt-text);
}

.app-nav-item--active,
.app-nav-item--active:hover {
  background: var(--dt-primary-soft);
  color: var(--dt-primary);
}

.app-nav-item__icon {
  font-size: 18px;
  flex-shrink: 0;
  width: 18px;
  display: inline-flex;
  justify-content: center;
}

.app-nav-item__label {
  font-size: 14px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.app-sidebar__spacer {
  flex: 1;
}

/* ---- 底部:连接状态 + 主题 + 折叠 ---- */
.app-sidebar__footer {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex-shrink: 0;
}

.app-conn {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 36px;
  padding: 0 11px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  cursor: pointer;
  transition: background 0.15s ease;
}

.app-conn:hover {
  background: var(--dt-hover);
}

.app-conn__label {
  font-size: 12px;
  color: var(--dt-text-2);
  white-space: nowrap;
}

.app-sidebar__btn {
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  height: 36px;
  padding: 0 11px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--dt-text-2);
  font-size: 16px;
  cursor: pointer;
  transition: background 0.15s ease, color 0.15s ease;
}

.app-sidebar__btn:hover {
  background: var(--dt-hover);
  color: var(--dt-text);
}

.app-sidebar__btn-icon {
  width: 18px;
  display: inline-flex;
  justify-content: center;
  flex-shrink: 0;
}

.app-sidebar__btn-label {
  font-size: 12px;
  white-space: nowrap;
}

/* auto 模式指示点:贴在图标右下角 */
.app-sidebar__auto-dot {
  position: absolute;
  left: 24px;
  top: 24px;
  width: 5px;
  height: 5px;
  border-radius: 999px;
  background: var(--dt-live);
}

/* ---- 右列:自定义标题条 + 内容;整体抬过画布氛围层 ---- */
.app-right {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  position: relative;
  z-index: 1;
}

/* 标题条:除三键外都是宿主的原生拖拽区。背景是 .app-canvas 光晕的延续
   (同参数、中心上移一个标题条高度),让顶部与页面光晕无缝衔接 */
.app-titlebar {
  flex: 0 0 44px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  background:
    radial-gradient(1400px 560px at 22% -124px, color-mix(in srgb, var(--dt-primary) 11%, transparent), transparent 72%),
    radial-gradient(900px 460px at 90% -184px, color-mix(in srgb, var(--dt-live) 7%, transparent), transparent 74%);
}

.app-titlebar__controls {
  display: flex;
  align-self: stretch;
}

.wtc-btn {
  display: grid;
  place-items: center;
  width: 46px;
  border: 0;
  padding: 0;
  background: transparent;
  color: var(--dt-text-2);
  cursor: default;
  transition: background 0.12s ease, color 0.12s ease;
}

.wtc-btn svg {
  width: 10px;
  height: 10px;
}

.wtc-btn:hover {
  background: var(--dt-hover);
  color: var(--dt-text);
}

.wtc-btn--close:hover {
  background: #e81123;
  color: #fff;
}

/* 窗口边缘缩放热区:细条贴边,四角稍大;按下即把控制权交给宿主 */
.win-edge {
  position: fixed;
  z-index: 40;
}

.win-edge--top { top: 0; left: 0; right: 0; height: 5px; cursor: ns-resize; }
.win-edge--bottom { bottom: 0; left: 0; right: 0; height: 5px; cursor: ns-resize; }
.win-edge--left { left: 0; top: 0; bottom: 0; width: 5px; cursor: ew-resize; }
.win-edge--right { right: 0; top: 0; bottom: 0; width: 4px; cursor: ew-resize; }
.win-edge--nw { top: 0; left: 0; width: 12px; height: 12px; cursor: nwse-resize; }
.win-edge--ne { top: 0; right: 0; width: 12px; height: 12px; cursor: nesw-resize; }
.win-edge--sw { bottom: 0; left: 0; width: 12px; height: 12px; cursor: nesw-resize; }
.win-edge--se { bottom: 0; right: 0; width: 12px; height: 12px; cursor: nwse-resize; }

/* 内容区:24 内边距 + 1440 居中;高度由右列持有。
   z-index 抬过画布氛围层;scrollbar-gutter 钉住槽位,滚动条出现/消失不再引发整页 6px 重排 */
.app-main {
  flex: 1;
  min-width: 0;
  overflow: auto;
  scrollbar-gutter: stable;
  position: relative;
  z-index: 1;
}

.app-content {
  max-width: 1440px;
  margin: 0 auto;
  padding: 12px 24px 24px;
  min-height: 100%;
  position: relative;
  z-index: 1;
}
</style>
