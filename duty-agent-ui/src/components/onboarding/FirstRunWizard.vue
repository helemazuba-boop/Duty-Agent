<script setup lang="ts">
import { ref } from 'vue';
import {
  Modal, Steps, Step, Input, Button, Space, message, Alert, Table, Select, Checkbox,
} from 'ant-design-vue';
import { api, getToken } from '@/api/http';
import type { RosterPerson } from '@/types';
import { API_BASE_URL } from '@/api/baseUrl';
import { useScheduleSSE } from '@/composables/useScheduleSSE';
import { useModelList } from '@/composables/useModelList';

const props = defineProps<{ open: boolean }>();
const emit = defineEmits<{ (e: 'update:open', v: boolean): void; (e: 'completed'): void }>();

const current = ref(0);

// Step 1: roster
const rosterText = ref('');
const rosterSaving = ref(false);
const rosterSaved = ref(false);
// 自启必须由用户明确选择（商店合规：不能因为装完就默认写登录项）。
// 向导里默认勾选并给出说明，用户可以取消；结果写入 host-config，客户端即时生效。
const enableAutoStart = ref(true);

const persistAutoStart = async () => {
  try {
    await api.updateNotificationSettings({ client_auto_start: enableAutoStart.value });
  } catch {
    // 自启是便利项，写失败不该挡住首次使用；设置页里还能再改。
    message.warning('开机自启设置未保存，可稍后在「设置 → 系统」里调整');
  }
};

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
    await persistAutoStart();
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

const { models: wizardModels, loading: wizardModelsLoading, fetchModels: fetchWizardModels, reset: resetWizardModels } = useModelList();

const applyPreset = (p: typeof MODEL_PRESETS[number]) => {
  baseUrl.value = p.base_url || baseUrl.value;
  probeResult.value = null;
  resetWizardModels();
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

// 没有模型也能用：切到「离线算法」方案（纯本地确定性排班，不联网、不需要 Key）。
// 这条路径同时是商店审核员的默认可测路径——不能要求审核方自备付费 API Key。
const offlineSaving = ref(false);
const useOfflineMode = async () => {
  offlineSaving.value = true;
  try {
    const config = await api.getConfig();
    const presets = Array.isArray(config.plan_presets) ? config.plan_presets : [];
    const offline = presets.find((p: any) => p.mode_id === 'offline');
    if (!offline) {
      message.error('未找到离线算法方案，请稍后在「设置 → AI 排班」里手动切换');
      return;
    }
    await api.updateConfig({
      expected_version: config.version,
      selected_plan_id: offline.id,
      plan_presets: presets,
    });
    message.success('已切换到离线算法：无需模型、不联网');
    current.value = 2;
  } catch (e: any) {
    if (e?.response?.status === 409) message.warning('配置已被更新，请重试');
    else message.error('切换离线算法失败');
  } finally {
    offlineSaving.value = false;
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
    :width="560"
    @update:open="(v: boolean) => emit('update:open', v)"
  >
    <Steps :current="current" size="small" class="wizard-steps">
      <Step title="导入名单" />
      <Step title="选择模型" />
      <Step title="试跑一次" />
    </Steps>

    <!-- Step 1 -->
    <div v-if="current === 0">
      <p class="wizard-hint">每行一个名字（也可用逗号分隔）。ID 会自动分配。</p>
      <Input.TextArea v-model:value="rosterText" :rows="6" placeholder="张三&#10;李四&#10;王五" />
      <Checkbox v-model:checked="enableAutoStart" class="wizard-check">
        登录 Windows 后自动在后台运行（开机自启，可随时在设置里关闭）
      </Checkbox>
      <div class="wizard-actions">
        <Button type="primary" :loading="rosterSaving" @click="saveRoster">保存并继续</Button>
      </div>
    </div>

    <!-- Step 2 -->
    <div v-else-if="current === 1">
      <p class="wizard-hint">选择一个模型来源，填写模型名称后测试连接。</p>
      <Alert
        type="info"
        show-icon
        class="wizard-consent"
        message="使用在线模型时，会把哪些数据发出去？"
        description="发送到你填写的模型服务地址的是：人员编号（不是姓名）、日期、值日区域与人数、以及你输入的排班指令。姓名不会离开本机。该模型服务如何处理数据由它自己的隐私政策约束；不想发送任何数据，请用下面的离线算法。"
      />
      <Space wrap style="margin-bottom: 12px">
        <Button v-for="p in MODEL_PRESETS" :key="p.label" @click="applyPreset(p)">{{ p.label }}</Button>
      </Space>
      <div class="wizard-field">
        <label>模型服务地址 (base_url)</label>
        <Input v-model:value="baseUrl" placeholder="http://localhost:1234/v1" />
      </div>
      <div class="wizard-field">
        <label>模型名称 (model)</label>
        <Input v-model:value="modelName" placeholder="点击下方按钮从服务端获取" />
        <Button
          size="small"
          :loading="wizardModelsLoading"
          @click="fetchWizardModels(baseUrl, apiKey)"
          style="margin-top: 8px"
        >
          获取模型列表
        </Button>
        <Select
          v-if="wizardModels.length"
          v-model:value="modelName"
          :options="wizardModels.map(m => ({ value: m.id, label: m.id }))"
          placeholder="选择模型"
          style="width: 100%; margin-top: 8px"
        />
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
      <div class="wizard-offline">
        <span>没有模型服务？</span>
        <Button type="link" size="small" :loading="offlineSaving" @click="useOfflineMode">
          使用离线算法（无需 API Key，不联网）
        </Button>
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
.wizard-steps {
  margin-bottom: 24px;
}

.wizard-hint {
  font-size: 13px;
  color: var(--dt-text-2);
  margin-bottom: 12px;
}

.wizard-field {
  margin-bottom: 14px;
}

.wizard-field label {
  display: block;
  font-size: 13px;
  font-weight: 600;
  color: var(--dt-text);
  margin-bottom: 6px;
}

.wizard-actions {
  margin-top: 20px;
  text-align: right;
}

.wizard-check {
  display: block;
  margin-top: 12px;
  font-size: 13px;
  color: var(--dt-text-2);
}

.wizard-offline {
  margin-top: 12px;
  font-size: 13px;
  color: var(--dt-text-2);
  text-align: right;
}

.wizard-consent {
  margin-bottom: 12px;
}
</style>
