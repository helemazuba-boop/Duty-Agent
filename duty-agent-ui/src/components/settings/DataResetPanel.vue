<script setup lang="ts">
/**
 * 数据管理（班级换届 / 学期重置）。
 *
 * 破坏性操作，所以刻意偏离仓库里 Popconfirm 的惯例：删一名成员和清掉全班数据
 * 不是一个量级，这里用 Modal + 键入档位名才放行，且 Modal 里始终并列展示
 * "将删除 / 将保留" 两列——模型密钥与令牌不参与任何一档，必须让人看见。
 */
import { computed, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { Button, Input, Modal, Spin, message } from 'ant-design-vue';
import { DeleteOutlined, ExclamationCircleOutlined, SafetyCertificateOutlined } from '@ant-design/icons-vue';
import Panel from '@/components/ui/Panel.vue';
import { api, type DataResetScope, type DataResetResult, type ResetArchives } from '@/api/http';
import { queryClient } from '@/queries/client';
import {
  snapshotKey, rosterKey, readinessKey, bridgeStatusKey, configKey, notificationSettingsKey,
} from '@/queries/keys';

// 后端 state_ops.RESET_SCOPE_ITEMS 的前端镜像：确认前就要能看见每一档删什么、
// 留什么，不能等删完才知道。文案与后端一致，改一侧记得改另一侧。
interface ScopeDef {
  scope: DataResetScope;
  label: string;
  title: string;
  desc: string;
  removed: string[];
  kept: string[];
  /** 是否需要弹"同时清空值日区域与班级规则"选项 */
  allowClearAreas: boolean;
  /** 成功后是否要重新走首次使用向导 */
  restartOnboarding: boolean;
}

const SCOPES: ScopeDef[] = [
  {
    scope: 'schedule',
    label: '清空排班',
    title: '清空排班记录',
    desc: '保留花名册与全部配置，只把这一轮的排班历史、账本和指针归零。适合学期中重整或试跑太多想重来。',
    removed: ['排班记录', '欠班/抵扣账本', '值日指针', '请假记录', '排班备注', '区域人数覆盖'],
    kept: ['花名册', '模型与方案配置', '值日区域与班级规则', '宿主配置'],
    allowClearAreas: true,
    restartOnboarding: false,
  },
  {
    scope: 'handover',
    label: '换届重置',
    title: '班级换届重置',
    desc: '清掉上一届的名单与全部排班数据，但保留模型配置——新一届沿用同一套模型，不必重新填密钥。',
    removed: ['排班记录', '欠班/抵扣账本', '值日指针', '请假记录', '排班备注', '区域人数覆盖', '花名册'],
    kept: ['模型与方案配置', '值日区域与班级规则', '宿主配置'],
    allowClearAreas: true,
    restartOnboarding: true,
  },
  {
    scope: 'factory',
    label: '恢复出厂',
    title: '恢复出厂设置',
    desc: '连模型配置一起清掉，回到首次安装状态。换班级或把软件交给别人时用。',
    removed: ['排班记录', '欠班/抵扣账本', '值日指针', '请假记录', '排班备注', '区域人数覆盖', '花名册', '模型与方案配置'],
    kept: ['宿主配置'],
    allowClearAreas: false,
    restartOnboarding: true,
  },
];

const router = useRouter();
const WIZARD_DISMISS_KEY = 'duty_first_run_dismissed';

const archives = ref<ResetArchives | null>(null);
const archivesLoading = ref(false);
/** 归档明细默认收起,只留一行状态;需要翻旧账时再展开 */
const archivesOpen = ref(false);

const archiveItems = computed(() => archives.value?.archives ?? []);
const hasArchives = computed(() => archiveItems.value.length > 0);
const archiveTotalBytes = computed(() =>
  archiveItems.value.reduce((sum, item) => sum + item.size_bytes, 0),
);

const formatSize = (bytes: number) => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
};

const archiveSummary = computed(() => {
  if (archivesLoading.value && !archives.value) return '读取中…';
  if (!archives.value) return '未读取';
  if (!archiveItems.value.length) return '暂无归档,每次重置前自动生成,仅保留最近 10 份';
  return `最近 ${archiveItems.value.length} 份 · ${formatSize(archiveTotalBytes.value)}`;
});

const active = ref<ScopeDef | null>(null);
const confirmText = ref('');
const clearAreas = ref(false);
const submitting = ref(false);
const result = ref<DataResetResult | null>(null);

const confirmMatched = computed(() => confirmText.value.trim() === (active.value?.label ?? ''));

const loadArchives = async () => {
  archivesLoading.value = true;
  try {
    archives.value = await api.getResetArchives();
  } catch {
    archives.value = null;
  } finally {
    archivesLoading.value = false;
  }
};

// 状态行要能说"最近 N 份",所以进面板时先读一次;失败就退回"未读取",不影响操作。
onMounted(loadArchives);

const open = (def: ScopeDef) => {
  active.value = def;
  confirmText.value = '';
  clearAreas.value = false;
  result.value = null;
};

const close = () => {
  active.value = null;
  confirmText.value = '';
  clearAreas.value = false;
  result.value = null;
};

const invalidateEverything = () => {
  // 重置后所有派生数据都变了：快照/名单/就绪状态/桥接状态/配置/通知设置全部作废。
  queryClient.invalidateQueries({ queryKey: snapshotKey });
  queryClient.invalidateQueries({ queryKey: rosterKey });
  queryClient.invalidateQueries({ queryKey: readinessKey() });
  queryClient.invalidateQueries({ queryKey: bridgeStatusKey });
  queryClient.invalidateQueries({ queryKey: configKey });
  queryClient.invalidateQueries({ queryKey: notificationSettingsKey });
};

const submit = async () => {
  const def = active.value;
  if (!def || !confirmMatched.value) return;
  submitting.value = true;
  try {
    const res = await api.resetData({
      scope: def.scope,
      clear_areas_and_rule: def.allowClearAreas ? clearAreas.value : false,
      confirm: def.label,
    });
    result.value = res;
    invalidateEverything();
    if (def.restartOnboarding) {
      // 就绪判定在后端，但首次向导还有个本地"已看过"标记；不清掉它，
      // 重置后会停在空仪表盘而不是重新引导录入名单。
      localStorage.removeItem(WIZARD_DISMISS_KEY);
    }
    await loadArchives();
    message.success(`${res.label}完成，原数据已归档`);
  } catch (e: any) {
    const status = e?.response?.status;
    if (status === 409) {
      message.warning(String(e?.response?.data?.detail || '正在排班中或配置已被其它入口更新，请稍后重试'));
    } else if (status === 400) {
      message.error(String(e?.response?.data?.detail || '确认文字不匹配'));
    } else {
      message.error('重置失败，数据未改动');
    }
  } finally {
    submitting.value = false;
  }
};

const goOnboarding = () => {
  close();
  router.push('/dashboard');
};
</script>

<template>
  <Panel
    class="da-gap-bottom"
    title="数据管理"
    subtitle="班级换届或学期重置时清空旧数据。删除前会自动归档到数据目录的 archive 文件夹，随时可查"
  >
    <ul class="reset-list">
      <li v-for="def in SCOPES" :key="def.scope" class="reset-row">
        <div class="reset-row__text">
          <div class="reset-row__title">{{ def.title }}</div>
          <div class="reset-row__desc">{{ def.desc }}</div>
        </div>
        <Button class="reset-row__action" danger size="small" @click="open(def)">
          <template #icon><DeleteOutlined /></template>
          {{ def.label }}
        </Button>
      </li>
    </ul>

    <div class="reset-archives">
      <div class="reset-archives__head">
        <SafetyCertificateOutlined class="reset-archives__icon" />
        <span class="reset-archives__title">自动归档</span>
        <span class="reset-archives__summary">{{ archiveSummary }}</span>
        <div class="reset-archives__actions">
          <Button
            v-if="hasArchives"
            type="link"
            size="small"
            :aria-expanded="archivesOpen"
            @click="archivesOpen = !archivesOpen"
          >
            {{ archivesOpen ? '收起' : '展开' }}
          </Button>
          <Button type="link" size="small" :loading="archivesLoading" @click="loadArchives">刷新</Button>
        </div>
      </div>

      <div v-show="archivesOpen && hasArchives" class="reset-archives__list">
        <Spin :spinning="archivesLoading" size="small">
          <div v-for="item in archiveItems" :key="item.name" class="reset-archives__item">
            <span class="reset-archives__name">{{ item.name }}</span>
            <span class="reset-archives__meta">{{ item.files.join(' · ') }} · {{ formatSize(item.size_bytes) }}</span>
          </div>
          <div class="reset-archives__path">
            归档位置：<code>{{ archives?.data_dir }}\archive</code>
          </div>
        </Spin>
      </div>
    </div>
  </Panel>

  <Modal
    :open="!!active"
    :title="active ? `确认${active.title}` : ''"
    :confirm-loading="submitting"
    :ok-button-props="{ disabled: !confirmMatched || !!result, danger: true }"
    ok-text="确认删除"
    cancel-text="取消"
    :width="560"
    @ok="submit"
    @cancel="close"
  >
    <template v-if="!result">
      <a-alert
        type="warning"
        show-icon
        :message="`此操作会删除本班${active?.removed.length ?? 0} 类数据，且不可直接在界面撤销`"
        description="删除前会自动归档，可从数据目录的 archive 文件夹取回。"
      />
      <div class="reset-modal__cols">
        <div class="reset-modal__col">
          <div class="reset-modal__col-title reset-modal__col-title--del">将删除</div>
          <ul>
            <li v-for="item in active?.removed" :key="item">{{ item }}</li>
          </ul>
        </div>
        <div class="reset-modal__col">
          <div class="reset-modal__col-title reset-modal__col-title--keep">将保留</div>
          <ul>
            <li v-for="item in active?.kept" :key="item">{{ item }}</li>
          </ul>
        </div>
      </div>
      <div v-if="active?.allowClearAreas" class="reset-modal__areas">
        <label class="reset-modal__checkbox">
          <input v-model="clearAreas" type="checkbox" />
          <span>同时清空值日区域与班级规则（同教室沿用建议不要勾选）</span>
        </label>
      </div>
      <div class="reset-modal__confirm">
        <div class="reset-modal__confirm-label">
          <ExclamationCircleOutlined />
          请准确输入「<b>{{ active?.label }}</b>」以确认
        </div>
        <Input v-model:value="confirmText" :placeholder="active?.label" allow-clear />
      </div>
    </template>

    <template v-else>
      <a-result status="success" :title="`${result.label}完成`" :sub-title="`原数据已归档到 ${result.archive.name}`">
        <template #extra>
          <div class="reset-modal__done">
            <div class="reset-modal__done-path">
              <code>{{ result.archive.path }}</code>
            </div>
            <Button v-if="active?.restartOnboarding" type="primary" @click="goOnboarding">
              去录入新一届名单
            </Button>
            <Button v-else @click="close">完成</Button>
          </div>
        </template>
      </a-result>
    </template>
  </Modal>
</template>

<style scoped>
/* 无边框列表:靠行间分隔线和留白分层,不再套三个小卡片 */
.reset-list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.reset-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 14px 0;
  border-bottom: 1px solid var(--dt-border);
}

.reset-row:last-child {
  padding-bottom: 0;
  border-bottom: 0;
}

.reset-row__text {
  flex: 1 1 auto;
  min-width: 0;
}

.reset-row__title {
  font-size: 14px;
  font-weight: 600;
  line-height: 20px;
}

.reset-row__desc {
  margin-top: 2px;
  font-size: 12px;
  line-height: 18px;
  color: var(--dt-text-2);
}

.reset-row__action {
  flex: 0 0 auto;
}

/* 归档:一行状态,明细默认收起 */
.reset-archives {
  margin-top: 16px;
  padding-top: 14px;
  border-top: 1px solid var(--dt-border);
}

.reset-archives__head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 12px;
  line-height: 18px;
}

.reset-archives__icon {
  color: var(--dt-text-3);
}

.reset-archives__title {
  font-weight: 600;
  color: var(--dt-text);
}

.reset-archives__summary {
  min-width: 0;
  color: var(--dt-text-3);
}

.reset-archives__actions {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
}

.reset-archives__actions .ant-btn-link {
  padding: 0;
  height: auto;
}

.reset-archives__list {
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.reset-archives__item {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  font-size: 12px;
  line-height: 20px;
}

.reset-archives__name {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  word-break: break-all;
}

.reset-archives__meta {
  flex: 0 0 auto;
  color: var(--dt-text-3);
  text-align: right;
}

.reset-archives__path {
  margin-top: 8px;
  font-size: 12px;
  color: var(--dt-text-2);
  word-break: break-all;
}

.reset-archives__path code {
  padding: 1px 4px;
  border-radius: 4px;
  background: var(--dt-surface-2);
  font-size: 11px;
}

.reset-modal__cols {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  margin-top: 16px;
}

.reset-modal__col ul {
  margin: 6px 0 0;
  padding-left: 18px;
  font-size: 12px;
  line-height: 22px;
}

.reset-modal__col-title {
  font-size: 12px;
  font-weight: 600;
}

.reset-modal__col-title--del {
  color: var(--dt-danger);
}

.reset-modal__col-title--keep {
  color: var(--dt-success);
}

.reset-modal__areas {
  margin-top: 14px;
}

.reset-modal__checkbox {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 12px;
  line-height: 20px;
  cursor: pointer;
}

.reset-modal__confirm {
  margin-top: 16px;
}

.reset-modal__confirm-label {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
  font-size: 12px;
  color: var(--dt-text-2);
}

.reset-modal__done {
  display: flex;
  flex-direction: column;
  gap: 10px;
  align-items: center;
}

.reset-modal__done-path code {
  font-size: 11px;
  word-break: break-all;
}

/* 窄窗口:说明与操作按钮不抢同一行,归档元信息允许折行 */
@media (max-width: 640px) {
  .reset-row {
    flex-direction: column;
    align-items: stretch;
    gap: 8px;
  }

  .reset-row__action {
    align-self: flex-start;
  }

  .reset-archives__item {
    flex-wrap: wrap;
  }

  .reset-archives__meta {
    text-align: left;
  }
}

@media (max-width: 560px) {
  .reset-modal__cols {
    grid-template-columns: 1fr;
  }
}
</style>
