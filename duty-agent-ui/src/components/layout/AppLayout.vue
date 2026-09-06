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
import { api } from '@/api/http';
import { useTheme, type ThemeMode } from '@/theme/useTheme';
import { safeStorage } from '@/utils/safeStorage';

const { mode, resolved, cycleMode } = useTheme();
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
const backendOk = ref<boolean | null>(null);
const checking = ref(false);
let timer: number | undefined;

const checkConnection = async () => {
  checking.value = true;
  try {
    const readiness = await api.getReadiness(false);
    backendOk.value = readiness.ready;
  } catch {
    backendOk.value = false;
  } finally {
    checking.value = false;
  }
};

/* pending 只在首次未知时出现,轮询期间不让状态点闪灰 */
const connectionStatus = computed<'ok' | 'error' | 'pending'>(() => {
  if (backendOk.value == null) return 'pending';
  return backendOk.value ? 'ok' : 'error';
});

const connectionLabel = computed(() => {
  if (connectionStatus.value === 'ok') return '已连接';
  if (connectionStatus.value === 'error') return '未连接';
  return '检测中';
});

const connectionTooltip = computed(() => {
  if (connectionStatus.value === 'error') return '后端未连接 · 点击重新检测(也可到「设置」排查)';
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
  checkConnection();
  timer = window.setInterval(checkConnection, 20000);
});

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer);
});
</script>

<template>
  <div class="app-shell" :class="{ 'app-shell--expanded': expanded }">
    <!-- ======== 白色窄侧栏(icon rail) ======== -->
    <aside class="app-sidebar">
      <div class="app-brand">
        <div class="app-brand__mark">值</div>
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
          <button type="button" class="app-conn" :aria-label="`后端${connectionLabel},点击重新检测`" @click="checkConnection">
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

    <!-- ======== 内容区:滚动发生在 main,不在 body ======== -->
    <main class="app-main">
      <div class="app-content">
        <RouterView />
      </div>
    </main>
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  background: var(--dt-bg);
}

/* ---- 侧栏:白底 + 右边框,不再用深色侧栏 ---- */
.app-sidebar {
  position: fixed;
  inset: 0 auto 0 0;
  z-index: 10;
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
  padding: 0 4px;
  margin-bottom: 12px;
  flex-shrink: 0;
}

.app-brand__mark {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: var(--dt-primary);
  color: #fff;
  font-size: 16px;
  font-weight: 600;
  flex-shrink: 0;
}

.app-brand__name {
  font-size: 15px;
  font-weight: 600;
  color: var(--dt-text);
  white-space: nowrap;
  overflow: hidden;
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

/* ---- 内容区:24 内边距 + 1440 居中;高度由 shell 持有 ---- */
.app-main {
  flex: 1;
  min-width: 0;
  margin-left: 56px;
  height: 100vh;
  overflow: auto;
  transition: margin-left 0.18s ease;
}

.app-shell--expanded .app-main {
  margin-left: 200px;
}

.app-content {
  max-width: 1440px;
  margin: 0 auto;
  padding: 20px 24px 24px;
  min-height: 100%;
}
</style>
