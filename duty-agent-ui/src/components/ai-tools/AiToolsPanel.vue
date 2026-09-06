<script setup lang="ts">
import { ref } from 'vue';
import { Switch, Button, Tag, Tooltip, InputNumber, Empty, message } from 'ant-design-vue';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons-vue';
import { useAiToolsStore } from '@/stores/aiToolsStore';
import ToolServerForm from './ToolServerForm.vue';
import ToolPreview from './ToolPreview.vue';
import Panel from '@/components/ui/Panel.vue';
import StatusDot from '@/components/ui/StatusDot.vue';

const store = useAiToolsStore();
const formOpen = ref(false);
const editingId = ref<string | null>(null);

const statusOf = (status: string): 'ok' | 'error' | 'pending' | 'idle' => {
  if (status === 'connected') return 'ok';
  if (status === 'error') return 'error';
  if (status === 'connecting') return 'pending';
  return 'idle';
};

const urlOf = (s: any) => s.url || s.httpUrl || (s.type === 'python' ? `python://${s.functionName ?? ''}` : '');

const handleConnect = async (id: string) => {
  try {
    await store.connectServer(id);
    message.success('连接成功');
  } catch {
    message.error('连接失败');
  }
};

const openCreate = () => {
  editingId.value = null;
  formOpen.value = true;
};
</script>

<template>
  <div class="ai-tools-panel">
    <!-- 启用开关 -->
    <Panel class="mb-16">
      <div class="enable-row">
        <Switch v-model:checked="store.config.enabled" />
        <span class="enable-row__label">启用 AI 工具调用</span>
        <Tag :color="store.config.enabled ? 'purple' : 'default'" class="enable-row__tag">
          {{ store.config.enabled ? '已启用' : '已禁用' }}
        </Tag>
      </div>
    </Panel>

    <!-- 工具服务器:卡片列表 -->
    <Panel class="mb-16" title="工具服务器" :padded="false">
      <template #actions>
        <Button type="primary" size="small" @click="openCreate">
          <template #icon><PlusOutlined /></template>
          添加服务器
        </Button>
      </template>

      <div v-if="store.config.toolServers.length" class="server-list">
        <div v-for="server in store.config.toolServers" :key="server.id" class="server-card">
          <div class="server-card__main">
            <div class="server-card__title-row">
              <StatusDot :status="statusOf(server.status)" />
              <span class="server-card__name">{{ server.name }}</span>
              <Tag class="server-card__type">{{ server.type.toUpperCase() }}</Tag>
              <Tooltip v-if="server.status === 'error' && server.errorMessage" :title="server.errorMessage">
                <span class="server-card__err">!</span>
              </Tooltip>
            </div>
            <div class="server-card__url">{{ urlOf(server) || '—' }}</div>
          </div>
          <div class="server-card__actions">
            <template v-if="server.type === 'mcp'">
              <Button
                v-if="server.status === 'connected'"
                size="small"
                @click="store.disconnectServer(server.id)"
              >
                断开
              </Button>
              <Button v-else size="small" type="primary" @click="handleConnect(server.id)">
                连接
              </Button>
            </template>
            <Button size="small" danger @click="store.removeServer(server.id)">
              <template #icon><DeleteOutlined /></template>
            </Button>
          </div>
        </div>
      </div>

      <div v-else class="server-empty">
        <Empty description="暂无工具服务器" :image="Empty.PRESENTED_IMAGE_SIMPLE" />
      </div>
    </Panel>

    <!-- 可用工具预览 -->
    <div class="mb-16">
      <ToolPreview />
    </div>

    <!-- 调用设置 -->
    <Panel title="调用设置">
      <div class="call-settings">
        <div class="call-settings__item">
          <span class="call-settings__label">每轮最大调用次数</span>
          <InputNumber
            :min="1"
            :max="50"
            v-model:value="store.config.maxToolCallsPerTurn"
            style="width: 80px"
          />
        </div>
        <div class="call-settings__item">
          <span class="call-settings__label">超时时间(秒)</span>
          <InputNumber
            :min="5"
            :max="120"
            v-model:value="store.config.toolCallTimeoutSeconds"
            style="width: 80px"
          />
        </div>
      </div>
    </Panel>

    <!-- 添加/编辑服务器表单 -->
    <ToolServerForm
      v-model:open="formOpen"
      :editing-id="editingId"
      @close="formOpen = false; editingId = null"
    />
  </div>
</template>

<style scoped>
.ai-tools-panel {
  min-width: 0;
}

.mb-16 {
  margin-bottom: 16px;
}

.enable-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.enable-row__label {
  font-size: 14px;
  font-weight: 500;
  color: var(--dt-text);
}

.enable-row__tag {
  margin: 0;
}

/* ---- 服务器卡片列表 ---- */
.server-list {
  display: flex;
  flex-direction: column;
}

.server-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 20px;
  border-bottom: 1px solid var(--dt-border-2);
  transition: background 0.15s ease;
}

.server-card:last-child {
  border-bottom: 0;
}

.server-card:hover {
  background: var(--dt-surface-2);
}

.server-card__main {
  min-width: 0;
}

.server-card__title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.server-card__name {
  font-size: 14px;
  font-weight: 500;
  color: var(--dt-text);
}

.server-card__type {
  margin: 0;
  font-size: 10px;
  line-height: 16px;
  padding: 0 6px;
  color: var(--dt-text-2);
}

.server-card__err {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--dt-danger) 14%, transparent);
  color: var(--dt-danger);
  font-size: 11px;
  font-weight: 700;
  cursor: help;
}

.server-card__url {
  margin-top: 2px;
  font-size: 12px;
  color: var(--dt-text-2);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.server-card__actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.server-empty {
  padding: 8px;
}

/* ---- 调用设置 ---- */
.call-settings {
  display: flex;
  gap: 32px;
  flex-wrap: wrap;
}

.call-settings__item {
  display: flex;
  align-items: center;
  gap: 10px;
}

.call-settings__label {
  font-size: 13px;
  color: var(--dt-text-2);
}
</style>
