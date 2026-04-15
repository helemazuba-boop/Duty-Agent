<script setup lang="ts">
import { ref, watch } from 'vue';
import { Modal, Input, Select, Switch, Divider } from 'ant-design-vue';
import { useAiToolsStore } from '@/stores/aiToolsStore';
import type { ToolServerConfig, ToolServerType } from '@/types/ai-tools';

interface Props {
  open: boolean;
  editingId?: string | null;
}

interface Emits {
  (e: 'close'): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const store = useAiToolsStore();
const serverType = ref<ToolServerType>('mcp');
const formState = ref({
  name: '',
  mcpUrl: '',
  token: '',
  httpUrl: '',
  httpMethod: 'POST' as 'GET' | 'POST',
  functionName: '',
  enabled: true,
});

watch(
  () => props.open,
  (isOpen) => {
    if (isOpen) {
      const editing = props.editingId
        ? store.config.toolServers.find((s) => s.id === props.editingId)
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
        formState.value = {
          name: '',
          mcpUrl: '',
          token: '',
          httpUrl: '',
          httpMethod: 'POST',
          functionName: '',
          enabled: true,
        };
      }
    }
  },
);

const handleSubmit = () => {
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
    <div style="display: flex; flex-direction: column; gap: 12px; width: 100%">
      <div>
        <div style="margin-bottom: 8px">服务器名称</div>
        <Input v-model:value="formState.name" placeholder="用于区分不同服务器，如 holiday_api" />
      </div>

      <div>
        <div style="margin-bottom: 8px">服务器类型</div>
        <Select
          v-model:value="serverType"
          :options="[
            { label: 'MCP Server', value: 'mcp' },
            { label: 'HTTP API', value: 'http' },
            { label: 'Python 函数', value: 'python' },
          ]"
          style="width: 100%"
        />
      </div>

      <Divider style="margin: 8px 0" />

      <template v-if="serverType === 'mcp'">
        <div>
          <div style="margin-bottom: 8px">MCP Server 地址</div>
          <Input v-model:value="formState.mcpUrl" placeholder="http://localhost:8765/mcp" />
        </div>
        <div>
          <div style="margin-bottom: 8px">认证 Token（可选）</div>
          <Input.Password v-model:value="formState.token" placeholder="Bearer Token" />
        </div>
      </template>

      <template v-else-if="serverType === 'http'">
        <div>
          <div style="margin-bottom: 8px">HTTP API 地址</div>
          <Input v-model:value="formState.httpUrl" placeholder="https://api.example.com/v1/tools" />
        </div>
        <div>
          <div style="margin-bottom: 8px">请求方法</div>
          <Select
            v-model:value="formState.httpMethod"
            :options="[
              { label: 'POST', value: 'POST' },
              { label: 'GET', value: 'GET' },
            ]"
            style="width: 100%"
          />
        </div>
      </template>

      <template v-else>
        <div>
          <div style="margin-bottom: 8px">函数名称</div>
          <Input v-model:value="formState.functionName" placeholder="check_holiday" />
        </div>
      </template>

      <Divider style="margin: 8px 0" />

      <div>
        <div style="display: flex; align-items: center; gap: 8px">
          <Switch v-model:checked="formState.enabled" />
          <span>启用状态</span>
        </div>
      </div>
    </div>
  </Modal>
</template>
