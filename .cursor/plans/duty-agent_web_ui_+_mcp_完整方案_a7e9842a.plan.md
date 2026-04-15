---
name: Duty-Agent Web UI + MCP 完整方案（Vue 3 版）
overview: 使用 @ant-design/vue + Vue 3 + TypeScript 构建独立的排班管理 Web UI 页面，通过 MCP 协议（Primary）和 REST API（Fallback）双通道与后端通信，替换现有内嵌式 C# 设置页。
todos:
  - id: install-deps
    content: 安装 @ant-design/vue、pinia、vue-router、axios、@modelcontextprotocol/sdk
    status: completed
  - id: create-mcp-client
    content: 创建 MCP 协议客户端（api/mcp.ts）
    status: pending
  - id: create-http-client
    content: 创建 HTTP REST 客户端（api/http.ts）作为 Fallback
    status: pending
  - id: create-types
    content: 设计 TypeScript 类型定义（types/index.ts）
    status: pending
  - id: config-router
    content: 配置路由（vue-router）
    status: pending
  - id: config-pinia
    content: 配置状态管理（Pinia）
    status: pending
  - id: config-theme
    content: 配置 @ant-design/vue 主题和中文国际化
    status: pending
  - id: build-layout
    content: 构建布局组件（AppLayout + SideMenu）
    status: pending
  - id: build-arrangement-page
    content: 构建排班安排独立页面（ArrangementPage）
    status: pending
  - id: build-arrangement-editor
    content: 构建排班编辑抽屉（ArrangementEditor）
    status: pending
  - id: build-roster-page
    content: 构建花名册管理页面（RosterPage）
    status: pending
  - id: build-dashboard
    content: 构建首页仪表盘（Dashboard）
    status: pending
  - id: create-ai-tools-types
    content: 创建 AI 工具类型定义（types/ai-tools.ts）
    status: pending
  - id: create-ai-tools-store
    content: 创建 AI 工具状态管理（stores/aiToolsStore.ts）
    status: pending
  - id: build-tool-server-list
    content: 构建工具服务器列表组件（ToolServerList + ToolServerForm）
    status: pending
  - id: build-tool-preview
    content: 构建可用工具预览组件（ToolPreview + ToolTestDialog）
    status: pending
  - id: build-ai-tools-panel
    content: 构建 AI 工具主面板（AiToolsPanel）
    status: pending
  - id: integrate-settings-page
    content: 将 AI 工具面板集成到设置页面
    status: pending
  - id: test-mcp
    content: 测试 MCP 连接和工具调用
    status: pending
isProject: false
---

## 技术选型

- **前端框架**：Vue 3.5 + TypeScript + Vite（现有项目）
- **UI 库**：`ant-design-vue`（搭配 @ant-design/icons）
- **MCP 客户端**：`@modelcontextprotocol/sdk`（支持 HTTP Streamable，框架无关）
- **HTTP 客户端**：Axios（Fallback）
- **路由**：`vue-router`
- **状态管理**：Pinia

## 项目结构

```
duty-agent-ui/src/
├── api/
│   ├── mcp.ts                 # MCP 协议客户端（连接后端 MCP Server）
│   │   ├── createMcpClient() # 初始化 MCP 客户端
│   │   ├── tools.ts          # MCP 工具调用封装
│   │   └── types.ts          # MCP 类型定义
│   ├── http.ts               # HTTP REST 客户端（Fallback）
│   └── ai-tools-api.ts       # AI 工具配置 API（前端存储 + 后端同步）
│
├── components/
│   ├── layout/
│   │   ├── AppLayout.vue     # 侧边栏 + 内容区布局
│   │   └── SideMenu.vue      # 导航菜单
│   │
│   ├── schedule/
│   │   ├── SchedulePage.vue  # ⭐ 排班安排独立页面
│   │   ├── ScheduleCalendar.vue  # 日历视图
│   │   ├── ScheduleTable.vue     # 列表视图
│   │   ├── ArrangementEditor.vue # 排班编辑抽屉
│   │   └── ScheduleCard.vue     # 单日排班卡片
│   │
│   ├── roster/
│   │   ├── RosterPage.vue    # 花名册管理页面
│   │   ├── RosterTable.vue   # 花名册表格
│   │   └── RosterImport.vue  # 导入组件
│   │
│   ├── settings/
│   │   ├── SettingsPage.vue  # 设置页面（Tab 容器）
│   │   ├── AIGenerator.vue   # AI 排班配置
│   │   ├── AutoRun.vue      # 自动运行配置
│   │   ├── Notification.vue  # 通知设置
│   │   └── AccessControl.vue  # 访问鉴权
│   │
│   └── ai-tools/             # ⭐ AI 工具 MCP 配置面板
│       ├── AiToolsPanel.vue  # AI 工具主面板
│       ├── ToolServerList.vue    # 工具服务器列表
│       ├── ToolServerForm.vue    # 添加/编辑服务器表单
│       ├── ToolPreview.vue        # 可用工具预览
│       └── ToolTestDialog.vue    # 工具测试对话框
│
├── composables/
│   ├── useMcpClient.ts       # MCP 客户端连接管理
│   ├── useSchedule.ts        # 排班数据 Composables
│   ├── useRoster.ts          # 花名册 Composables
│   └── useAiTools.ts         # ⭐ AI 工具 Composables
│
├── pages/
│   ├── Dashboard.vue         # 首页仪表盘
│   ├── RosterPage.vue       # 花名册页面
│   ├── SchedulePage.vue      # 排班页面
│   ├── ArrangementPage.vue   # ⭐ 排班安排独立页面
│   └── SettingsPage.vue     # 设置页面（Tab 容器）
│
├── stores/
│   └── aiToolsStore.ts       # ⭐ AI 工具配置状态管理（Pinia）
│
├── types/
│   ├── index.ts              # 通用类型定义
│   └── ai-tools.ts           # ⭐ AI 工具相关类型
│
├── router/
│   └── index.ts              # Vue Router 配置
│
├── App.vue                   # 主应用入口
└── main.ts                   # 入口文件
```

## MCP 协议集成设计

### MCP 工具映射

| 后端 MCP Tool | 前端调用 | 用途 |
|-------------|---------|------|
| `inspect_workspace` | 获取完整工作区快照（配置+名单+排班） | 页面初始化时调用 |
| `update_scheduler_config` | 修改排班配置（方案预设、AI规则等） | 设置保存 |
| `update_roster` | 更新花名册 | 花名册增删改 |
| `run_schedule` | 触发 AI 排班 | 执行排班 |
| `save_schedule_entry` | 保存排班记录 | 编辑单条排班 |

### MCP 客户端实现

```typescript
// api/mcp.ts
import { Client } from '@modelcontextprotocol/sdk/client/stdio.js';

let mcpClient: Client | null = null;

export async function createMcpClient(mcpUrl: string): Promise<Client> {
  mcpClient = new Client({
    name: 'duty-agent-ui',
    version: '1.0.0',
  });
  
  await mcpClient.connect({
    transportType: 'streamable-http',
    url: mcpUrl,
  });
  
  return mcpClient;
}

// 调用 MCP 工具
export async function callTool<T = any>(
  toolName: string, 
  args?: Record<string, any>
): Promise<T> {
  if (!mcpClient) {
    throw new Error('MCP client not initialized');
  }
  
  const response = await mcpClient.callTool({
    name: toolName,
    arguments: args,
  });
  
  return response.content[0].text as T;
}

// 工具封装
export const mcpTools = {
  inspectWorkspace: () => callTool('inspect_workspace'),
  updateConfig: (config: any) => callTool('update_scheduler_config', config),
  updateRoster: (roster: any[]) => callTool('update_roster', { roster }),
  runSchedule: (instruction?: string) => callTool('run_schedule', { instruction }),
  saveScheduleEntry: (entry: any) => callTool('save_schedule_entry', entry),
};
```

### MCP Composable（Vue 3 封装）

```typescript
// composables/useMcpClient.ts
import { ref, onMounted, onUnmounted } from 'vue';
import { createMcpClient, mcpTools } from '@/api/mcp';

export function useMcpClient(mcpUrl: string) {
  const connected = ref(false);
  const loading = ref(false);
  const error = ref<string | null>(null);
  
  const connect = async () => {
    loading.value = true;
    error.value = null;
    try {
      await createMcpClient(mcpUrl);
      connected.value = true;
    } catch (e) {
      error.value = String(e);
      connected.value = false;
    } finally {
      loading.value = false;
    }
  };
  
  const disconnect = () => {
    connected.value = false;
  };
  
  return {
    connected,
    loading,
    error,
    connect,
    disconnect,
    tools: mcpTools,
  };
}
```

### 连接策略

```
┌─────────────────────────────────────────────────────┐
│                   Web UI (Vue 3)                     │
└─────────────────────┬───────────────────────────────┘
                      │
        ┌─────────────┴──────────────┐
        │                            │
   MCP Protocol              HTTP REST API
   (Primary)                 (Fallback)
        │                            │
        ▼                            ▼
┌───────────────────────────────────────────────────────┐
│         FastAPI Backend (MCP Server)                  │
│  - MCP Tools: inspect_workspace, run_schedule 等      │
│  - REST: /api/config, /api/roster, /api/snapshot      │
│  - Port: 随机或固定（用户在设置页配置）                │
└───────────────────────────────────────────────────────┘
```

## 排班安排独立页面设计

### ArrangementPage.vue

```vue
<!-- pages/ArrangementPage.vue -->
<script setup lang="ts">
import { ref, computed } from 'vue';
import { Segmented, Button, Card, Space, message } from 'ant-design-vue';
import ScheduleCalendar from '@/components/schedule/ScheduleCalendar.vue';
import ScheduleTable from '@/components/schedule/ScheduleTable.vue';
import ArrangementEditor from '@/components/schedule/ArrangementEditor.vue';
import { useMcpWorkspace } from '@/composables/useMcpClient';

const viewMode = ref<'calendar' | 'table'>('calendar');
const editorOpen = ref(false);
const selectedDate = ref<string | null>(null);

const { data: workspace, loading, refresh } = useMcpWorkspace();

const handleDateSelect = (date: string) => {
  selectedDate.value = date;
  editorOpen.value = true;
};

const handleNewSchedule = () => {
  selectedDate.value = null;
  editorOpen.value = true;
};

const handleEditorSave = async (entry: any) => {
  try {
    await mcpTools.saveScheduleEntry(entry);
    await refresh();
    editorOpen.value = false;
    message.success('保存成功');
  } catch (e) {
    message.error('保存失败');
  }
};
</script>

<template>
  <div style="padding: 24px;">
    <Space direction="vertical" style="width: 100%" :size="16">
      <!-- 页面标题 + 操作栏 -->
      <Card>
        <Space style="width: 100%; justify-content: space-between">
          <Segmented
            v-model:value="viewMode"
            :options="[
              { label: '日历视图', value: 'calendar' },
              { label: '列表视图', value: 'table' },
            ]"
          />
          <Button type="primary" @click="handleNewSchedule">
            新建排班
          </Button>
        </Space>
      </Card>

      <!-- 视图区域 -->
      <template v-if="viewMode === 'calendar'">
        <ScheduleCalendar
          v-if="workspace"
          :schedule-pool="workspace.state?.schedule_pool"
          @date-select="handleDateSelect"
        />
      </template>
      <template v-else>
        <ScheduleTable
          v-if="workspace"
          :schedule-pool="workspace.state?.schedule_pool"
          @row-edit="handleDateSelect"
        />
      </template>
    </Space>

    <!-- 排班编辑抽屉 -->
    <ArrangementEditor
      v-model:open="editorOpen"
      :date="selectedDate"
      :schedule-pool="workspace?.state?.schedule_pool"
      :roster="workspace?.roster"
      @save="handleEditorSave"
    />
  </div>
</template>
```

### ArrangementEditor.vue（排班编辑抽屉）

```vue
<!-- components/schedule/ArrangementEditor.vue -->
<script setup lang="ts">
import { ref, watch, computed } from 'vue';
import { Drawer, Form, DatePicker, Select, Input, Space, Divider, Tag, Button } from 'ant-design-vue';
import { PlusOutlined, DeleteOutlined } from '@ant-design/icons';

interface ArrangementEditorProps {
  open: boolean;
  date?: string | null;
  schedulePool?: any[];
  roster?: any[];
}

interface ArrangementEditorEmits {
  (e: 'save', entry: any): void;
  (e: 'update:open', value: boolean): void;
}

const props = defineProps<ArrangementEditorProps>();
const emit = defineEmits<ArrangementEditorEmits>();

const formRef = ref();
const assignments = ref<Record<string, string[]>>({});

const formState = computed({
  get: () => ({
    date: props.date,
  }),
  set: () => {},
});

watch(
  () => [props.open, props.date, props.schedulePool],
  ([isOpen, date, pool]) => {
    if (isOpen && date && pool) {
      const existing = pool.find((s: any) => s.date === date);
      if (existing) {
        assignments.value = existing.area_assignments || {};
      }
    } else if (isOpen) {
      assignments.value = {};
    }
  },
  { immediate: true },
);

const addArea = () => {
  const key = `区域${Object.keys(assignments.value).length + 1}`;
  assignments.value = { ...assignments.value, [key]: [] };
};

const addPersonToArea = (area: string, person: string) => {
  assignments.value = {
    ...assignments.value,
    [area]: [...(assignments.value[area] || []), person],
  };
};

const removePerson = (area: string, index: number) => {
  const newList = [...(assignments.value[area] || [])];
  newList.splice(index, 1);
  assignments.value = { ...assignments.value, [area]: newList };
};

const removeArea = (area: string) => {
  const { [area]: _, ...rest } = assignments.value;
  assignments.value = rest;
};

const handleClose = () => {
  emit('update:open', false);
};

const handleSave = async () => {
  try {
    await formRef.value?.validate();
    emit('save', { date: props.date, assignments: assignments.value });
  } catch {}
};

const rosterOptions = computed(() =>
  (props.roster || [])
    .filter((r: any) => r.active)
    .map((r: any) => ({ label: r.name, value: r.name })),
);
</script>

<template>
  <Drawer
    :open="open"
    :title="date ? '编辑排班' : '新建排班'"
    width="520"
    @close="handleClose"
  >
    <template #extra>
      <Space>
        <Button @click="handleClose">取消</Button>
        <Button type="primary" @click="handleSave">保存</Button>
      </Space>
    </template>

    <Form ref="formRef" layout="vertical" :model="formState">
      <Form.Item label="日期">
        <DatePicker v-model:value="formState.date" style="width: 100%" />
      </Form.Item>

      <Form.Item label="备注">
        <Input.TextArea :rows="2" placeholder="附加说明..." />
      </Form.Item>

      <Divider>安排区域</Divider>

      <div v-for="(persons, area) in assignments" :key="area" style="margin-bottom: 16px">
        <div style="display: flex; justify-content: space-between; margin-bottom: 8px">
          <strong>{{ area }}</strong>
          <Button
            type="text"
            danger
            size="small"
            :icon="h(DeleteOutlined)"
            @click="removeArea(area as string)"
          />
        </div>

        <div style="display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 8px">
          <Tag
            v-for="(person, idx) in persons"
            :key="idx"
            closable
            @close="removePerson(area as string, idx)"
          >
            {{ person }}
          </Tag>
        </div>

        <Select
          placeholder="添加人员"
          style="width: 100%"
          :options="rosterOptions"
          @select="(person: string) => addPersonToArea(area as string, person)"
        />
      </div>

      <Button type="dashed" :icon="h(PlusOutlined)" block @click="addArea">
        添加区域
      </Button>
    </Form>
  </Drawer>
</template>
```

## 路由设计

```typescript
// router/index.ts
import { createRouter, createWebHistory } from 'vue-router';

const routes = [
  {
    path: '/',
    component: () => import('@/components/layout/AppLayout.vue'),
    children: [
      { path: '', redirect: '/dashboard' },
      { path: 'dashboard', name: 'Dashboard', component: () => import('@/pages/Dashboard.vue') },
      { path: 'arrangement', name: 'Arrangement', component: () => import('@/pages/ArrangementPage.vue') },
      { path: 'roster', name: 'Roster', component: () => import('@/pages/RosterPage.vue') },
      { path: 'schedule', name: 'Schedule', component: () => import('@/pages/SchedulePage.vue') },
      { path: 'settings', name: 'Settings', component: () => import('@/pages/SettingsPage.vue') },
    ],
  },
];

export const router = createRouter({
  history: createWebHistory(),
  routes,
});
```

```vue
<!-- App.vue -->
<script setup lang="ts">
import { RouterView } from 'vue-router';
import { ConfigProvider } from 'ant-design-vue';
import zhCN from 'ant-design-vue/es/locale/zh_CN';
</script>

<template>
  <ConfigProvider :locale="zhCN">
    <RouterView />
  </ConfigProvider>
</template>
```

```typescript
// main.ts
import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import { router } from './router';

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.mount('#app');
```

## 菜单结构

```
├── 首页仪表盘
├── 排班安排      ← 独立页面，MCP 驱动
├── 花名册管理
├── 排班方案      ← AI 排班触发 + 方案管理
└── 设置
    ├── AI 配置
    ├── 自动运行
    ├── 通知设置
    └── 访问鉴权
```

## MCP 连接配置

用户可在设置页配置 MCP 连接：

```typescript
// MCP 连接配置
interface McpConfig {
  mode: 'streamable-http' | 'websocket' | 'stdio';
  endpoint: string;  // 例如 http://localhost:8765/mcp
  token?: string;    // Bearer token
}
```

## 后端 API 端点（Fallback）

```
GET  /api/snapshot     → 获取完整快照
GET  /api/config       → 获取配置
POST /api/config       → 更新配置
GET  /api/roster       → 获取花名册
POST /api/roster       → 更新花名册
POST /api/schedule/run → 触发排班
POST /api/schedule/save → 保存排班记录
```

## AI 工具 MCP 配置面板（前端实现）

> **注意**：本节为前端实现，后端 Tool Executor 暂不落地。前端仅负责配置管理和预览。

### 概述

```
┌─────────────────────────────────────────────────────────────────┐
│                      AI 工具 MCP 配置                            │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  [x] 启用 AI 工具调用                                     │   │
│  │                                                          │   │
│  │  工具服务器列表                                           │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ [MCP]  holiday_api    ● 已连接    http://...      │  │   │
│  │  │ [HTTP] weather        ● 已连接    https://...     │  │   │
│  │  │ [MCP]  school_calendar ○ 未连接   localhost:9000   │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  │                                                          │   │
│  │  [+ 添加服务器]                                          │   │
│  │                                                          │   │
│  │  可用工具预览                                            │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ 🔧 mcp.holiday_api.check_holiday                │  │   │
│  │  │    检查指定日期是否为节假日                        │  │   │
│  │  │    参数: date (string)                           │  │   │
│  │  ├───────────────────────────────────────────────────┤  │   │
│  │  │ 🔧 http.weather.get_forecast                    │  │   │
│  │  │    获取天气预报                                   │  │   │
│  │  │    参数: city (string), date (string)           │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  │                                                          │   │
│  │  调用设置                                                │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ 每轮最大调用次数: [10]   超时时间(秒): [30]       │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### 类型定义

```typescript
// types/ai-tools.ts

/** 工具服务器类型 */
export type ToolServerType = 'mcp' | 'http' | 'python';

/** 工具服务器配置 */
export interface ToolServerConfig {
  id: string;                    // 唯一标识
  name: string;                  // 显示名称（用于命名空间）
  type: ToolServerType;          // 服务器类型
  enabled: boolean;              // 是否启用
  // MCP 类型
  url?: string;                  // MCP Server HTTP 端点
  token?: string;                // 可选的认证 token
  // HTTP 类型
  httpUrl?: string;              // HTTP API 地址
  httpMethod?: 'GET' | 'POST';   // 请求方法
  httpHeaders?: Record<string, string>; // 请求头
  // Python 类型
  functionName?: string;         // 函数名
  // 连接状态
  status: 'connected' | 'disconnected' | 'connecting' | 'error';
  errorMessage?: string;
  lastConnectedAt?: string;
}

/** AI 工具配置 */
export interface AiToolsConfig {
  enabled: boolean;
  toolServers: ToolServerConfig[];
  maxToolCallsPerTurn: number;   // 每轮最大调用次数
  toolCallTimeoutSeconds: number; // 调用超时时间
}

/** MCP 工具元信息（从 MCP Server 列表获取）*/
export interface McpToolInfo {
  name: string;                  // 工具名称
  description: string;           // 工具描述
  inputSchema: {                // JSON Schema
    type: 'object';
    properties: Record<string, any>;
    required?: string[];
  };
  serverId: string;              // 所属服务器 ID
}

/** 工具测试结果 */
export interface ToolTestResult {
  success: boolean;
  output?: string;
  error?: string;
  duration: number;              // 耗时(ms)
}
```

### 状态管理（Pinia）

```typescript
// stores/aiToolsStore.ts

import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import type { AiToolsConfig, ToolServerConfig, McpToolInfo } from '@/types/ai-tools';

export const useAiToolsStore = defineStore('aiTools', () => {
  // 配置
  const config = ref<AiToolsConfig>({
    enabled: false,
    toolServers: [],
    maxToolCallsPerTurn: 10,
    toolCallTimeoutSeconds: 30,
  });

  // 连接状态
  const connectedTools = ref<McpToolInfo[]>([]);

  // 操作
  const setEnabled = (enabled: boolean) => {
    config.value.enabled = enabled;
  };

  const addServer = (server: ToolServerConfig) => {
    config.value.toolServers.push(server);
  };

  const updateServer = (id: string, updates: Partial<ToolServerConfig>) => {
    const idx = config.value.toolServers.findIndex(s => s.id === id);
    if (idx !== -1) {
      config.value.toolServers[idx] = { ...config.value.toolServers[idx], ...updates };
    }
  };

  const removeServer = (id: string) => {
    config.value.toolServers = config.value.toolServers.filter(s => s.id !== id);
    connectedTools.value = connectedTools.value.filter(t => t.serverId !== id);
  };

  const setMaxToolCalls = (max: number) => {
    config.value.maxToolCallsPerTurn = max;
  };

  const setTimeout = (seconds: number) => {
    config.value.toolCallTimeoutSeconds = seconds;
  };

  const connectServer = async (id: string) => {
    const server = config.value.toolServers.find(s => s.id === id);
    if (!server || server.type !== 'mcp') return;

    updateServer(id, { status: 'connecting' });

    try {
      const tools = await refreshTools(id);
      updateServer(id, {
        status: 'connected',
        lastConnectedAt: new Date().toISOString(),
      });
      // 更新已连接工具列表
      connectedTools.value = [
        ...connectedTools.value.filter(t => t.serverId !== id),
        ...tools,
      ];
    } catch (error) {
      updateServer(id, {
        status: 'error',
        errorMessage: String(error),
      });
    }
  };

  const disconnectServer = (id: string) => {
    updateServer(id, { status: 'disconnected' });
    connectedTools.value = connectedTools.value.filter(t => t.serverId !== id);
  };

  const refreshTools = async (id: string): Promise<McpToolInfo[]> => {
    const server = config.value.toolServers.find(s => s.id === id);
    if (!server?.url) return [];

    const response = await fetch(server.url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(server.token ? { 'Authorization': `Bearer ${server.token}` } : {}),
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/list',
        params: {},
        id: 1,
      }),
    });

    const result = await response.json();
    const tools: McpToolInfo[] = (result.tools || []).map((t: any) => ({
      name: t.name,
      description: t.description || '',
      inputSchema: t.inputSchema || { type: 'object', properties: {} },
      serverId: id,
    }));

    return tools;
  };

  const testTool = async (serverId: string, toolName: string, args: Record<string, any>) => {
    const server = config.value.toolServers.find(s => s.id === serverId);
    if (!server?.url) throw new Error('Server not configured');

    const response = await fetch(server.url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(server.token ? { 'Authorization': `Bearer ${server.token}` } : {}),
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/call',
        params: { name: toolName, arguments: args },
        id: Date.now(),
      }),
    });

    return await response.json();
  };

  return {
    config,
    connectedTools,
    setEnabled,
    addServer,
    updateServer,
    removeServer,
    setMaxToolCalls,
    setTimeout,
    connectServer,
    disconnectServer,
    refreshTools,
    testTool,
  };
});
```

### AI 工具主面板组件

```vue
<!-- components/ai-tools/AiToolsPanel.vue -->

<script setup lang="ts">
import { ref, computed } from 'vue';
import { Card, Switch, Table, Button, Space, Tag, Tooltip, message } from 'ant-design-vue';
import { PlusOutlined, SyncOutlined, DeleteOutlined, CheckCircleOutlined, CloseCircleOutlined } from '@ant-design/icons';
import { useAiToolsStore } from '@/stores/aiToolsStore';
import ToolServerForm from './ToolServerForm.vue';
import ToolPreview from './ToolPreview.vue';

const store = useAiToolsStore();
const formOpen = ref(false);
const editingServer = ref<string | null>(null);

const columns = computed(() => [
  {
    title: '类型',
    dataIndex: 'type',
    width: 80,
  },
  {
    title: '名称',
    dataIndex: 'name',
  },
  {
    title: '地址',
    dataIndex: 'url',
    ellipsis: true,
  },
  {
    title: '状态',
    dataIndex: 'status',
    width: 100,
  },
  {
    title: '操作',
    width: 160,
  },
]);

const statusMap = {
  connected: { color: '#52c41a', text: '已连接' },
  disconnected: { color: '#d9d9d9', text: '未连接' },
  connecting: { color: '#faad14', text: '连接中...' },
  error: { color: '#ff4d4f', text: '错误' },
};

const handleConnect = async (id: string) => {
  try {
    await store.connectServer(id);
    message.success('连接成功');
  } catch (e) {
    message.error('连接失败');
  }
};

const handleDisconnect = (id: string) => {
  store.disconnectServer(id);
};
</script>

<template>
  <div style="padding: 24px;">
    <Space direction="vertical" style="width: 100%" :size="16">
      <!-- 启用开关 -->
      <Card size="small">
        <Space>
          <Switch v-model:checked="store.config.enabled" />
          <span style="font-weight: 500">启用 AI 工具调用</span>
          <Tag :color="store.config.enabled ? 'green' : 'default'">
            {{ store.config.enabled ? '已启用' : '已禁用' }}
          </Tag>
        </Space>
      </Card>

      <!-- 工具服务器列表 -->
      <Card
        title="工具服务器"
        :tab-list="[]"
      >
        <template #extra>
          <Button type="primary" :icon="h(PlusOutlined)" @click="formOpen = true">
            添加服务器
          </Button>
        </template>
        <Table
          row-key="id"
          :columns="columns"
          :data-source="store.config.toolServers"
          :pagination="false"
          :locale="{ emptyText: '暂无服务器，请点击右上角添加' }"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.dataIndex === 'type'">
              <Tag :color="record.type === 'mcp' ? 'blue' : record.type === 'http' ? 'green' : 'orange'">
                {{ record.type.toUpperCase() }}
              </Tag>
            </template>
            <template v-else-if="column.dataIndex === 'name'">
              <Space>
                <span>{{ record.name }}</span>
                <CheckCircleOutlined v-if="record.status === 'connected'" style="color: #52c41a" />
              </Space>
            </template>
            <template v-else-if="column.dataIndex === 'url'">
              <Tooltip :title="record.url || record.httpUrl">
                <span>{{ record.url || record.httpUrl || '-' }}</span>
              </Tooltip>
            </template>
            <template v-else-if="column.dataIndex === 'status'">
              <Space>
                <span :style="{ color: statusMap[record.status]?.color }">
                  {{ statusMap[record.status]?.text }}
                </span>
                <CloseCircleOutlined
                  v-if="record.status === 'error' && record.errorMessage"
                  style="color: #ff4d4f"
                >
                  <template #title>{{ record.errorMessage }}</template>
                </CloseCircleOutlined>
              </Space>
            </template>
            <template v-else-if="column.key === 'action'">
              <Space size="small">
                <template v-if="record.type === 'mcp'">
                  <Button
                    v-if="record.status === 'connected'"
                    size="small"
                    @click="handleDisconnect(record.id)"
                  >
                    断开
                  </Button>
                  <Button
                    v-else
                    size="small"
                    type="primary"
                    @click="handleConnect(record.id)"
                  >
                    连接
                  </Button>
                </template>
                <Button
                  size="small"
                  danger
                  :icon="h(DeleteOutlined)"
                  @click="store.removeServer(record.id)"
                />
              </Space>
            </template>
          </template>
        </Table>
      </Card>

      <!-- 可用工具预览 -->
      <ToolPreview />

      <!-- 调用设置 -->
      <Card title="调用设置" size="small">
        <Space size="large">
          <Space>
            <span>每轮最大调用次数：</span>
            <a-input-number
              :min="1"
              :max="50"
              v-model:value="store.config.maxToolCallsPerTurn"
              style="width: 60px"
            />
          </Space>
          <Space>
            <span>超时时间（秒）：</span>
            <a-input-number
              :min="5"
              :max="120"
              v-model:value="store.config.toolCallTimeoutSeconds"
              style="width: 60px"
            />
          </Space>
        </Space>
      </Card>
    </Space>

    <!-- 添加/编辑服务器表单 -->
    <ToolServerForm
      v-model:open="formOpen"
      :editing-id="editingServer"
      @close="formOpen = false; editingServer = null"
    />
  </div>
</template>
```

### 添加服务器表单

```vue
<!-- components/ai-tools/ToolServerForm.vue -->

<script setup lang="ts">
import { ref, watch } from 'vue';
import { Modal, Form, Input, Select, Switch, Space, Divider } from 'ant-design-vue';
import type { ToolServerConfig, ToolServerType } from '@/types/ai-tools';
import { useAiToolsStore } from '@/stores/aiToolsStore';

interface ToolServerFormProps {
  open: boolean;
  editingId?: string | null;
}

interface ToolServerFormEmits {
  (e: 'close'): void;
}

const props = defineProps<ToolServerFormProps>();
const emit = defineEmits<ToolServerFormEmits>();

const formRef = ref();
const store = useAiToolsStore();
const serverType = ref<ToolServerType>('mcp');
const formState = ref({
  name: '',
  mcpUrl: '',
  token: '',
  httpUrl: '',
  httpMethod: 'POST' as const,
  functionName: '',
  enabled: true,
});

watch(
  () => props.open,
  (isOpen) => {
    if (isOpen) {
      const editing = props.editingId
        ? store.config.toolServers.find(s => s.id === props.editingId)
        : null;
      if (editing) {
        serverType.value = editing.type;
        formState.value = {
          name: editing.name,
          mcpUrl: editing.url || '',
          token: editing.token || '',
          httpUrl: editing.httpUrl || '',
          httpMethod: editing.httpMethod || 'POST',
          functionName: editing.functionName || '',
          enabled: editing.enabled,
        };
      } else {
        serverType.value = 'mcp';
        formState.value = { name: '', mcpUrl: '', token: '', httpUrl: '', httpMethod: 'POST', functionName: '', enabled: true };
      }
    }
  },
);

const handleSubmit = () => {
  formRef.value?.validate().then(() => {
    const server: ToolServerConfig = {
      id: props.editingId || `server_${Date.now()}`,
      name: formState.value.name,
      type: serverType.value,
      enabled: formState.value.enabled,
      status: 'disconnected',
      ...(serverType.value === 'mcp' && {
        url: formState.value.mcpUrl,
        token: formState.value.token,
      }),
      ...(serverType.value === 'http' && {
        httpUrl: formState.value.httpUrl,
        httpMethod: formState.value.httpMethod,
      }),
      ...(serverType.value === 'python' && {
        functionName: formState.value.functionName,
      }),
    };

    if (props.editingId) {
      store.updateServer(props.editingId, server);
    } else {
      store.addServer(server);
    }

    emit('close');
  });
};
</script>

<template>
  <Modal
    :open="open"
    :title="editingId ? '编辑服务器' : '添加工具服务器'"
    @cancel="emit('close')"
    @ok="handleSubmit"
    width="520"
  >
    <Form
      ref="formRef"
      layout="vertical"
      :model="formState"
    >
      <Form.Item
        name="name"
        label="服务器名称"
        :rules="[{ required: true, message: '请输入名称' }]"
      >
        <Input v-model:value="formState.name" placeholder="用于区分不同服务器，如 holiday_api" />
      </Form.Item>

      <Form.Item label="服务器类型">
        <Select
          v-model:value="serverType"
          :options="[
            { label: 'MCP Server', value: 'mcp' },
            { label: 'HTTP API', value: 'http' },
            { label: 'Python 函数', value: 'python' },
          ]"
        />
      </Form.Item>

      <Divider />

      <template v-if="serverType === 'mcp'">
        <Form.Item
          name="mcpUrl"
          label="MCP Server 地址"
          :rules="[{ required: true, message: '请输入 MCP Server 地址' }]"
        >
          <Input v-model:value="formState.mcpUrl" placeholder="http://localhost:8765/mcp" />
        </Form.Item>
        <Form.Item name="token" label="认证 Token（可选）">
          <Input.Password v-model:value="formState.token" placeholder="Bearer Token" />
        </Form.Item>
      </template>

      <template v-else-if="serverType === 'http'">
        <Form.Item
          name="httpUrl"
          label="HTTP API 地址"
          :rules="[{ required: true, message: '请输入 API 地址' }]"
        >
          <Input v-model:value="formState.httpUrl" placeholder="https://api.example.com/v1/tools" />
        </Form.Item>
        <Form.Item name="httpMethod" label="请求方法" :initial-value="'POST'">
          <Select
            v-model:value="formState.httpMethod"
            :options="[
              { label: 'POST', value: 'POST' },
              { label: 'GET', value: 'GET' },
            ]"
          />
        </Form.Item>
      </template>

      <template v-else>
        <Form.Item
          name="functionName"
          label="函数名称"
          :rules="[{ required: true, message: '请输入函数名' }]"
        >
          <Input v-model:value="formState.functionName" placeholder="check_holiday" />
        </Form.Item>
      </template>

      <Divider />

      <Form.Item name="enabled" label="启用状态">
        <Switch
          v-model:checked="formState.enabled"
          checked-children="启用"
          un-checked-children="禁用"
        />
      </Form.Item>
    </Form>
  </Modal>
</template>
```

### 可用工具预览 + 测试

```vue
<!-- components/ai-tools/ToolPreview.vue -->

<script setup lang="ts">
import { ref, computed } from 'vue';
import { Card, Table, Tag, Button, Space, Modal, Input, message } from 'ant-design-vue';
import { useAiToolsStore } from '@/stores/aiToolsStore';
import type { McpToolInfo } from '@/types/ai-tools';

const store = useAiToolsStore();
const testModalOpen = ref(false);
const selectedTool = ref<McpToolInfo | null>(null);
const testArgs = ref<Record<string, string>>({});
const testResult = ref('');
const testing = ref(false);

const serverMap = computed(() =>
  Object.fromEntries(store.config.toolServers.map(s => [s.id, s.name])),
);

const columns = [
  { title: '工具', key: 'name' },
  { title: '描述', dataIndex: 'description', ellipsis: true },
  { title: '参数', key: 'params' },
  { title: '操作', width: 100 },
];

const openTestModal = (tool: McpToolInfo) => {
  selectedTool.value = tool;
  testArgs.value = {};
  testResult.value = '';
  testModalOpen.value = true;
};

const handleTest = async () => {
  if (!selectedTool.value) return;
  testing.value = true;
  testResult.value = '';
  try {
    const args = Object.fromEntries(
      Object.entries(testArgs.value).map(([k, v]) => [k, JSON.parse(v)]),
    );
    const result = await store.testTool(selectedTool.value.serverId, selectedTool.value.name, args);
    testResult.value = JSON.stringify(result, null, 2);
    message.success('测试完成');
  } catch (error: any) {
    testResult.value = `错误: ${error.message}`;
    message.error('测试失败');
  } finally {
    testing.value = false;
  }
};
</script>

<template>
  <Card title="可用工具预览">
    <Table
      v-if="store.connectedTools.length > 0"
      row-key="name"
      :columns="columns"
      :data-source="store.connectedTools"
      :pagination="false"
      size="small"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'name'">
          <Space direction="vertical" :size="0">
            <Tag color="blue">{{ record.name }}</Tag>
            <span style="font-size: 12px; color: #666">{{ serverMap[record.serverId] }}</span>
          </Space>
        </template>
        <template v-else-if="column.key === 'params'">
          <Space direction="vertical" :size="0">
            <span
              v-for="(schema, key) in record.inputSchema.properties"
              :key="key"
              style="font-size: 12px"
            >
              <Tag size="small" :color="record.inputSchema.required?.includes(key) ? 'red' : 'default'">
                {{ key }}
              </Tag>
              <span style="color: #888">({{ (schema as any).type }})</span>
            </span>
          </Space>
        </template>
        <template v-else-if="column.key === 'action'">
          <Button size="small" @click="openTestModal(record)">测试</Button>
        </template>
      </template>
    </Table>
    <div v-else style="text-align: center; color: #999; padding: 24px">
      暂无已连接的工具服务器。请先添加并连接服务器。
    </div>
  </Card>

  <Modal
    :open="testModalOpen"
    :title="`测试工具: ${selectedTool?.name}`"
    @cancel="testModalOpen = false"
    @ok="handleTest"
    :confirm-loading="testing"
    width="600"
  >
    <Space direction="vertical" style="width: 100%">
      <div><strong>描述：</strong>{{ selectedTool?.description }}</div>
      <div>
        <h4>输入参数：</h4>
        <div
          v-for="(schema, key) in selectedTool?.inputSchema.properties"
          :key="key"
          style="margin-bottom: 8px"
        >
          <span>{{ key }}</span>
          <Input
            :placeholder="`请输入 ${key} (${(schema as any).type})`"
            v-model:value="testArgs[key as string]"
            style="margin-top: 4px"
          />
        </div>
      </div>
      <div>
        <h4>执行结果：</h4>
        <Input.TextArea
          v-model:value="testResult"
          readonly
          :rows="6"
          style="font-family: monospace"
          placeholder="点击"测试"按钮查看结果"
        />
      </div>
    </Space>
  </Modal>
</template>
```

### 设置页面集成

```vue
<!-- pages/SettingsPage.vue -->

<script setup lang="ts">
import { Tabs } from 'ant-design-vue';
import AiToolsPanel from '@/components/ai-tools/AiToolsPanel.vue';
import AIGenerator from '@/components/settings/AIGenerator.vue';
import AutoRun from '@/components/settings/AutoRun.vue';
import Notification from '@/components/settings/Notification.vue';
import AccessControl from '@/components/settings/AccessControl.vue';

const items = [
  { key: 'ai-generator', label: 'AI 排班', children: h(AIGenerator) },
  { key: 'ai-tools', label: 'AI 工具', children: h(AiToolsPanel) },
  { key: 'auto-run', label: '自动运行', children: h(AutoRun) },
  { key: 'notification', label: '通知设置', children: h(Notification) },
  { key: 'access-control', label: '访问鉴权', children: h(AccessControl) },
];
</script>

<template>
  <Tabs :items="items" />
</template>
```

### 菜单结构（更新）

```
├── 首页仪表盘
├── 排班安排      ← 独立页面，MCP 驱动
├── 花名册管理
├── 排班方案      ← AI 排班触发 + 方案管理
└── 设置
    ├── AI 排班        ← 原 AI 配置
    ├── AI 工具        ← ⭐ 新增（AI 工具 MCP 配置）
    ├── 自动运行
    ├── 通知设置
    └── 访问鉴权
```
