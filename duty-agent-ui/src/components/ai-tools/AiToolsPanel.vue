<script setup lang="ts">
import { ref } from 'vue';
import { Card, Switch, Table, Button, Space, Tag, Tooltip, message } from 'ant-design-vue';
import { DeleteOutlined, CheckCircleOutlined, CloseCircleOutlined, PlusOutlined } from '@ant-design/icons-vue';
import { useAiToolsStore } from '@/stores/aiToolsStore';
import ToolServerForm from './ToolServerForm.vue';
import ToolPreview from './ToolPreview.vue';

const store = useAiToolsStore();
const formOpen = ref(false);
const editingId = ref<string | null>(null);

const statusMap: Record<string, { color: string; text: string }> = {
  connected: { color: '#52c41a', text: '已连接' },
  disconnected: { color: '#d9d9d9', text: '未连接' },
  connecting: { color: '#faad14', text: '连接中...' },
  error: { color: '#ff4d4f', text: '错误' },
};

const columns = [
  { title: '类型', dataIndex: 'type', width: 80 },
  { title: '名称', dataIndex: 'name' },
  { title: '地址', dataIndex: 'url', ellipsis: true },
  { title: '状态', dataIndex: 'status', width: 100 },
  { title: '操作', width: 160, key: 'action' },
];

const handleConnect = async (id: string) => {
  try {
    await store.connectServer(id);
    message.success('连接成功');
  } catch {
    message.error('连接失败');
  }
};
</script>

<template>
  <div class="ai-tools-panel">
    <!-- 启用开关 -->
    <Card size="small" style="margin-bottom: 16px">
      <Space>
        <Switch v-model:checked="store.config.enabled" />
        <span style="font-weight: 500">启用 AI 工具调用</span>
        <Tag :color="store.config.enabled ? 'green' : 'default'">
          {{ store.config.enabled ? '已启用' : '已禁用' }}
        </Tag>
      </Space>
    </Card>

    <!-- 工具服务器列表 -->
    <Card style="margin-bottom: 16px">
      <template #title>工具服务器</template>
      <template #extra>
        <Button type="primary" @click="formOpen = true">
          <template #icon><PlusOutlined /></template>
          添加服务器
        </Button>
      </template>

      <Table
        :columns="columns"
        :data-source="store.config.toolServers"
        :pagination="false"
        :scroll="{ x: 720 }"
        row-key="id"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.dataIndex === 'type'">
            <Tag :color="record.type === 'mcp' ? 'blue' : record.type === 'http' ? 'green' : 'orange'">
              {{ record.type.toUpperCase() }}
            </Tag>
          </template>
          <template v-else-if="column.dataIndex === 'name'">
            <Space>
              {{ record.name }}
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
              <Tooltip v-if="record.status === 'error' && record.errorMessage" :title="record.errorMessage">
                <CloseCircleOutlined style="color: #ff4d4f" />
              </Tooltip>
            </Space>
          </template>
          <template v-else-if="column.key === 'action'">
            <Space size="small">
              <template v-if="record.type === 'mcp'">
                <Button
                  v-if="record.status === 'connected'"
                  size="small"
                  @click="store.disconnectServer(record.id)"
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
                @click="store.removeServer(record.id)"
              >
                <template #icon><DeleteOutlined /></template>
              </Button>
            </Space>
          </template>
        </template>
      </Table>

      <div
        v-if="!store.config.toolServers.length"
        style="text-align: center; color: #999; padding: 24px"
      >
        暂无服务器，请点击右上角添加
      </div>
    </Card>

    <!-- 可用工具预览 -->
    <div style="margin-bottom: 16px">
      <ToolPreview />
    </div>

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

.server-url {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: bottom;
  white-space: nowrap;
}
</style>
