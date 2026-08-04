<script setup lang="ts">
import { ref } from 'vue';
import {
  Modal, Steps, Step, Input, Button, Space, message, Alert, Table,
} from 'ant-design-vue';
import { api, getToken } from '@/api/http';
import type { RosterPerson } from '@/types';
import { API_BASE_URL } from '@/api/baseUrl';
import { useScheduleSSE } from '@/composables/useScheduleSSE';

const props = defineProps<{ open: boolean }>();
const emit = defineEmits<{ (e: 'update:open', v: boolean): void; (e: 'completed'): void }>();

const current = ref(0);

// Step 1: roster
const rosterText = ref('');
const rosterSaving = ref(false);
const rosterSaved = ref(false);

const parseRoster = (): RosterPerson[] => rosterText.value
  .split(/[\r\n,，]+/)
  .map((s) => s.trim())
  .filter(Boolean)
  .map((name, i) => ({ id: i + 1, name, active: true } as RosterPerson));

const saveRoster = async () => {
  const roster = parseRoster();
  if (roster.length === 0) {
    message.error('请至少输入一个名字');
    return;
  }
  rosterSaving.value = true;
  try {
    await api.updateRoster(roster);
    rosterSaved.value = true;
    message.success(`已保存 ${roster.length} 人`);
    current.value = 1;
  } catch {
    message.error('保存花名册失败');
  } finally {
    rosterSaving.value = false;
  }
};

// Step 2: model
const MODEL_PRESETS = [
  { label: 'LM Studio (本地)', base_url: 'http://localhost:1234/v1', needsKey: false },
  { label: 'Ollama (本地)', base_url: 'http://localhost:11434/v1', needsKey: false },
  { label: '云端 (OpenAI 兼容)', base_url: '', needsKey: true },
];
const baseUrl = ref('http://localhost:1234/v1');
const modelName = ref('');
const apiKey = ref('');
const probing = ref(false);
const probeResult = ref<{ ok: boolean; detail: string; fix: string } | null>(null);
const modelSaving = ref(false);

const applyPreset = (p: typeof MODEL_PRESETS[number]) => {
  baseUrl.value = p.base_url || baseUrl.value;
  probeResult.value = null;
};

const testConnection = async () => {
  if (!baseUrl.value.trim() || !modelName.value.trim()) {
    message.error('请填写模型服务地址与模型名称');
    return;
  }
  probing.value = true;
  probeResult.value = null;
  try {
    const r = await api.probeModel({ base_url: baseUrl.value.trim(), model: modelName.value.trim(), api_key: apiKey.value });
    probeResult.value = { ok: r.ok, detail: r.detail, fix: r.fix };
    if (r.ok) message.success('连接成功'); else message.warning('连接未通过');
  } catch {
    probeResult.value = { ok: false, detail: '探测请求失败', fix: '确认后端在运行' };
  } finally {
    probing.value = false;
  }
};

const saveModel = async () => {
  if (!baseUrl.value.trim() || !modelName.value.trim()) {
    message.error('请填写模型服务地址与模型名称');
    return;
  }
  modelSaving.value = true;
  try {
    const config = await api.getConfig();
    const presets = Array.isArray(config.plan_presets) ? config.plan_presets : [];
    const selectedId = config.selected_plan_id || 'standard';
    const target = presets.find((p: any) => p.id === selectedId) || presets[0];
    if (!target) {
      message.error('未找到可写入的方案');
      return;
    }
    target.base_url = baseUrl.value.trim();
    target.model = modelName.value.trim();
    target.api_key = apiKey.value;
    await api.updateConfig({
      expected_version: config.version,
      selected_plan_id: selectedId,
      plan_presets: presets,
    });
    message.success('模型配置已保存');
    current.value = 2;
  } catch (e: any) {
    if (e?.response?.status === 409) message.warning('配置已被更新，请重试');
    else message.error('保存模型配置失败');
  } finally {
    modelSaving.value = false;
  }
};

// Step 3: trial run
const sse = useScheduleSSE();
const trialRows = ref<Array<{ date: string; area: string; people: string }>>([]);
const trialError = ref('');

const runTrial = async () => {
  trialError.value = '';
  trialRows.value = [];
  const result = await sse.runSchedule({
    instruction: '给接下来 5 天排值日，每天一人',
    baseUrl: API_BASE_URL,
    token: getToken(),
  });
  if (result.status === 'success' || result.status === 'ok') {
    const pool = (result as any)?.snapshot?.state?.schedule_pool ?? [];
    trialRows.value = pool.flatMap((entry: any) => Object.entries(entry.area_assignments || {}).map(
      ([area, people]: [string, any]) => ({ date: entry.date, area, people: (people || []).join('、') }),
    ));
    message.success('试跑成功');
  } else {
    trialError.value = (result as any).message || '试跑失败';
  }
};

const trialColumns = [
  { title: '日期', dataIndex: 'date', key: 'date' },
  { title: '区域', dataIndex: 'area', key: 'area' },
  { title: '值日', dataIndex: 'people', key: 'people' },
];

const finish = () => {
  emit('update:open', false);
  emit('completed');
};
</script>

<template>
  <Modal
    :open="props.open"
    title="首次使用向导"
    :footer="null"
    :mask-closable="false"
    width="640px"
    @update:open="(v: boolean) => emit('update:open', v)"
  >
    <Steps :current="current" size="small" style="margin-bottom: 20px">
      <Step title="导入名单" />
      <Step title="选择模型" />
      <Step title="试跑一次" />
    </Steps>

    <!-- Step 1 -->
    <div v-if="current === 0">
      <p class="wizard-hint">每行一个名字（也可用逗号分隔）。ID 会自动分配。</p>
      <Input.TextArea v-model:value="rosterText" :rows="6" placeholder="张三&#10;李四&#10;王五" />
      <div class="wizard-actions">
        <Button type="primary" :loading="rosterSaving" @click="saveRoster">保存并继续</Button>
      </div>
    </div>

    <!-- Step 2 -->
    <div v-else-if="current === 1">
      <p class="wizard-hint">选择一个模型来源，填写模型名称后测试连接。</p>
      <Space wrap style="margin-bottom: 12px">
        <Button v-for="p in MODEL_PRESETS" :key="p.label" @click="applyPreset(p)">{{ p.label }}</Button>
      </Space>
      <div class="wizard-field">
        <label>模型服务地址 (base_url)</label>
        <Input v-model:value="baseUrl" placeholder="http://localhost:1234/v1" />
      </div>
      <div class="wizard-field">
        <label>模型名称 (model)</label>
        <Input v-model:value="modelName" placeholder="qwen3.6-35b-a3b-imatrix" />
      </div>
      <div class="wizard-field">
        <label>API Key（本地模型可留空）</label>
        <Input.Password v-model:value="apiKey" placeholder="留空表示无需鉴权" />
      </div>
      <Alert
        v-if="probeResult"
        :type="probeResult.ok ? 'success' : 'warning'"
        :message="probeResult.detail"
        :description="probeResult.ok ? '' : probeResult.fix"
        show-icon
        style="margin: 8px 0"
      />
      <div class="wizard-actions">
        <Space>
          <Button :loading="probing" @click="testConnection">测试连接</Button>
          <Button type="primary" :loading="modelSaving" @click="saveModel">保存并继续</Button>
        </Space>
      </div>
    </div>

    <!-- Step 3 -->
    <div v-else>
      <p class="wizard-hint">点“试跑一次”生成接下来 5 天的值日，确认一切正常。</p>
      <Table
        v-if="trialRows.length"
        :columns="trialColumns"
        :data-source="trialRows"
        size="small"
        :pagination="false"
        row-key="date"
        style="margin-bottom: 12px"
      />
      <Alert v-if="trialError" type="error" :message="trialError" show-icon style="margin-bottom: 12px" />
      <div class="wizard-actions">
        <Space>
          <Button :loading="sse.isRunning.value" @click="runTrial">
            {{ sse.isRunning.value ? sse.currentPhase.value || '排班中...' : '试跑一次' }}
          </Button>
          <Button type="primary" @click="finish">完成</Button>
        </Space>
      </div>
    </div>
  </Modal>
</template>

<style scoped>
.wizard-hint {
  font-size: 13px;
  color: var(--da-text-secondary);
  margin-bottom: 12px;
}

.wizard-field {
  margin-bottom: 12px;
}

.wizard-field label {
  display: block;
  font-size: 13px;
  font-weight: 600;
  margin-bottom: 4px;
}

.wizard-actions {
  margin-top: 16px;
  text-align: right;
}
</style>
