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
  Object.fromEntries(store.config.toolServers.map((s) => [s.id, s.name])),
);

const columns = [
  { title: '工具', key: 'name', width: 200 },
  { title: '描述', dataIndex: 'description', ellipsis: true },
  { title: '参数', key: 'params' },
  { title: '操作', width: 80 },
];

const openTestModal = (tool: McpToolInfo) => {
  selectedTool.value = tool as McpToolInfo;
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
      Object.entries(testArgs.value).map(([k, v]: [string, string]) => [k, JSON.parse(v)]),
    );
    const result = await store.testTool(selectedTool.value.serverId, selectedTool.value.name, args);
    testResult.value = JSON.stringify(result, null, 2);
    message.success('测试完成');
  } catch (error: unknown) {
    testResult.value = `错误: ${error instanceof Error ? error.message : String(error)}`;
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
      :columns="columns"
      :data-source="store.connectedTools"
      :pagination="false"
      row-key="name"
      size="small"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'name'">
          <Space direction="vertical" :size="0">
            <Tag color="blue">{{ record.name }}</Tag>
            <span style="font-size: 12px; color: #666">{{ serverMap[record.serverId as string] }}</span>
          </Space>
        </template>
        <template v-else-if="column.key === 'params'">
          <Space direction="vertical" :size="0">
            <span
              v-for="(schema, key) in record.inputSchema.properties"
              :key="key"
              style="font-size: 12px"
            >
              <Tag size="small" :color="record.inputSchema.required?.includes(key as string) ? 'red' : 'default'">
                {{ key }}
              </Tag>
              <span style="color: #888">({{ (schema as any).type }})</span>
            </span>
          </Space>
        </template>
        <template v-else-if="column.key === 'action'">
          <Button size="small" @click="openTestModal(record as McpToolInfo)">测试</Button>
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
    <div style="display: flex; flex-direction: column; gap: 12px; width: 100%">
      <div>
        <strong>描述：</strong>{{ selectedTool?.description }}
      </div>

      <div>
        <div style="font-weight: 500; margin-bottom: 8px">输入参数：</div>
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
        <div v-if="!selectedTool?.inputSchema.properties" style="color: #999">
          该工具无参数
        </div>
      </div>

      <div>
        <div style="font-weight: 500; margin-bottom: 8px">执行结果：</div>
        <Input.TextArea
          v-model:value="testResult"
          readonly
          :rows="6"
          style="font-family: monospace"
          placeholder='点击"测试"按钮查看结果'
        />
      </div>
    </div>
  </Modal>
</template>
