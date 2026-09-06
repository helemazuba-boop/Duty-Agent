<script setup lang="ts">
import { computed, ref, onMounted } from 'vue';
import { Table, Drawer, Input, Switch, Space, message, Popconfirm, Tooltip, Empty } from 'ant-design-vue';
import {
  DeleteOutlined,
  EditOutlined,
  UserAddOutlined,
  ReloadOutlined,
  UserSwitchOutlined,
  CopyOutlined,
} from '@ant-design/icons-vue';
import { api } from '@/api/http';
import type { RosterPerson, ScheduleEntry } from '@/types';
import PageHeader from '@/components/ui/PageHeader.vue';
import Panel from '@/components/ui/Panel.vue';
import PersonChip from '@/components/ui/PersonChip.vue';

const roster = ref<RosterPerson[]>([]);
const pool = ref<ScheduleEntry[]>([]);
const loading = ref(false);
const updatedAt = ref(0);

const fetchAll = async () => {
  loading.value = true;
  try {
    const snapshot = await api.getSnapshot();
    roster.value = snapshot.roster ?? [];
    pool.value = snapshot.state?.schedule_pool ?? [];
    updatedAt.value = Date.now();
  } catch (e) {
    console.error('[RosterPage] fetch failed:', e);
  } finally {
    loading.value = false;
  }
};

onMounted(fetchAll);

const todayIso = (() => {
  const n = new Date();
  return `${n.getFullYear()}-${String(n.getMonth() + 1).padStart(2, '0')}-${String(n.getDate()).padStart(2, '0')}`;
})();

const monthPrefix = todayIso.slice(0, 7);

/** 本月值班次数 / 上次值班日期:花名册的核心价值是公平性 */
const dutyCountOfMonth = (name: string): number =>
  pool.value.filter(
    (entry) => entry.date.startsWith(monthPrefix)
      && Object.values(entry.area_assignments ?? {}).flat().includes(name),
  ).length;

const lastDutyDate = (name: string): string | null => {
  let last: string | null = null;
  for (const entry of pool.value) {
    if (Object.values(entry.area_assignments ?? {}).flat().includes(name)) {
      if (!last || entry.date > last) last = entry.date;
    }
  }
  return last;
};

const contactOf = (person: RosterPerson) =>
  String(person.contact ?? person.phone ?? person.contact_info ?? '').trim();

const activeCount = computed(() => roster.value.filter((p) => p.active).length);
const inactiveCount = computed(() => roster.value.length - activeCount.value);

const dataSource = computed(() =>
  roster.value.map((r) => ({
    ...r,
    key: r.name,
    dutyCount: dutyCountOfMonth(r.name),
    lastDuty: lastDutyDate(r.name),
  })),
);

const columns = [
  { title: '成员', dataIndex: 'name', key: 'name', minWidth: 180 },
  { title: '状态', dataIndex: 'active', width: 90, align: 'center' as const },
  { title: '本月值班', dataIndex: 'dutyCount', width: 100, align: 'center' as const, key: 'dutyCount' },
  { title: '上次值班', dataIndex: 'lastDuty', width: 120, key: 'lastDuty' },
  { title: '联系方式', key: 'contact', minWidth: 160 },
  { title: '操作', width: 130, align: 'center' as const, key: 'action' },
];

// ======== 编辑 Drawer ========
const drawerOpen = ref(false);
const editingOriginal = ref<string | null>(null);
const formName = ref('');
const formActive = ref(true);

const handleAdd = () => {
  editingOriginal.value = null;
  formName.value = '';
  formActive.value = true;
  drawerOpen.value = true;
};

const handleEdit = (person: RosterPerson) => {
  editingOriginal.value = person.name;
  formName.value = person.name;
  formActive.value = person.active;
  drawerOpen.value = true;
};

const handleSave = async () => {
  const name = formName.value.trim();
  if (!name) {
    message.error('请输入姓名');
    return;
  }
  let updated: RosterPerson[];
  if (editingOriginal.value) {
    if (editingOriginal.value !== name && roster.value.some((r) => r.name === name)) {
      message.error('该姓名已存在');
      return;
    }
    updated = roster.value.map((r) =>
      r.name === editingOriginal.value ? { ...r, name, active: formActive.value } : r,
    );
  } else {
    if (roster.value.some((r) => r.name === name)) {
      message.error('该姓名已存在');
      return;
    }
    updated = [...roster.value, { name, active: formActive.value }];
  }
  await persist(updated);
  drawerOpen.value = false;
  message.success(editingOriginal.value ? '修改成功' : '添加成功');
};

const handleDelete = async (name: string) => {
  await persist(roster.value.filter((r) => r.name !== name));
  message.success('删除成功');
};

const toggleActive = async (person: RosterPerson) => {
  const updated = roster.value.map((r) =>
    r.name === person.name ? { ...r, active: !r.active } : r,
  );
  await persist(updated);
  message.success(updated.find((r) => r.name === person.name)?.active ? '已设为在职' : '已设为离职');
};

const persist = async (updated: RosterPerson[]) => {
  roster.value = await api.updateRoster(updated);
};

const copyContact = async (person: RosterPerson) => {
  const contact = contactOf(person);
  if (!contact) return;
  try {
    await navigator.clipboard.writeText(contact);
    message.success('已复制联系方式');
  } catch {
    message.warning('复制失败,请手动复制');
  }
};
</script>

<template>
  <div class="page-container animate-fade-in">
    <PageHeader title="花名册">
      <template #subtitle>
        共 {{ roster.length }} 人
        <template v-if="roster.length">
          · {{ activeCount }} 在职 / {{ inactiveCount }} 离职
        </template>
      </template>
      <template #actions>
        <Space>
          <a-button @click="fetchAll" :loading="loading">
            <template #icon><ReloadOutlined /></template>
          </a-button>
          <a-button type="primary" @click="handleAdd">
            <template #icon><UserAddOutlined /></template>
            添加人员
          </a-button>
        </Space>
      </template>
    </PageHeader>

    <Panel :padded="false" v-flash="updatedAt">
      <Table
        :columns="columns"
        :data-source="dataSource"
        :loading="loading"
        :pagination="{ pageSize: 15, showSizeChanger: true, pageSizeOptions: ['10', '15', '30'], showTotal: (total: number) => `共 ${total} 人` }"
        row-key="name"
        size="middle"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'name'">
            <PersonChip :name="record.name" avatar class="roster-name" />
          </template>

          <template v-else-if="column.key === 'active'">
            <span class="roster-status" :class="record.active ? 'roster-status--ok' : 'roster-status--off'">
              {{ record.active ? '在职' : '离职' }}
            </span>
          </template>

          <template v-else-if="column.key === 'dutyCount'">
            <span class="da-tnum roster-count">{{ record.dutyCount }}</span>
          </template>

          <template v-else-if="column.key === 'lastDuty'">
            <span v-if="record.lastDuty" class="da-tnum roster-lastduty">{{ record.lastDuty }}</span>
            <span v-else class="roster-lastduty roster-lastduty--none">从未值班</span>
          </template>

          <template v-else-if="column.key === 'contact'">
            <span v-if="contactOf(record as RosterPerson)" class="roster-contact">
              {{ contactOf(record as RosterPerson) }}
              <Tooltip title="复制">
                <a-button size="small" type="text" @click="copyContact(record as RosterPerson)">
                  <template #icon><CopyOutlined /></template>
                </a-button>
              </Tooltip>
            </span>
            <span v-else class="roster-contact roster-contact--none">—</span>
          </template>

          <template v-else-if="column.key === 'action'">
            <Space size="small" style="justify-content: center; display: flex">
              <Tooltip title="编辑">
                <a-button size="small" type="text" @click="handleEdit(record as RosterPerson)">
                  <template #icon><EditOutlined /></template>
                </a-button>
              </Tooltip>
              <Popconfirm
                :title="record.active ? `将「${record.name}」设为离职？` : `将「${record.name}」设为在职？`"
                ok-text="确认"
                cancel-text="取消"
                placement="topRight"
                @confirm="toggleActive(record as RosterPerson)"
              >
                <Tooltip :title="record.active ? '设为离职' : '设为在职'">
                  <a-button size="small" type="text">
                    <template #icon><UserSwitchOutlined /></template>
                  </a-button>
                </Tooltip>
              </Popconfirm>
              <Popconfirm
                :title="`确认删除「${record.name}」？`"
                ok-text="删除"
                cancel-text="取消"
                placement="topRight"
                @confirm="handleDelete((record as RosterPerson).name)"
              >
                <Tooltip title="删除">
                  <a-button size="small" type="text" danger>
                    <template #icon><DeleteOutlined /></template>
                  </a-button>
                </Tooltip>
              </Popconfirm>
            </Space>
          </template>
        </template>

        <template #emptyText>
          <Empty description="还没有成员" :image="Empty.PRESENTED_IMAGE_SIMPLE">
            <a-button type="primary" @click="handleAdd">添加第一位成员</a-button>
            <p class="roster-empty-hint">也可以在「AI 排班」里让 AI 帮你管理</p>
          </Empty>
        </template>
      </Table>
    </Panel>

    <!-- 添加/编辑 Drawer -->
    <Drawer
      v-model:open="drawerOpen"
      :title="editingOriginal ? '编辑人员' : '添加人员'"
      width="420"
    >
      <template #extra>
        <Space>
          <a-button @click="drawerOpen = false">取消</a-button>
          <a-button type="primary" @click="handleSave">保存</a-button>
        </Space>
      </template>

      <div class="roster-form">
        <div class="roster-form__field">
          <label class="roster-form__label">姓名</label>
          <Input
            v-model:value="formName"
            :placeholder="editingOriginal ? '修改姓名' : '输入新人员姓名'"
            @keyup.enter="handleSave"
          />
        </div>
        <div class="roster-form__field">
          <label class="roster-form__label">状态</label>
          <div class="roster-form__switch">
            <Switch v-model:checked="formActive" />
            <span>{{ formActive ? '在职,参与排班' : '离职,不参与排班' }}</span>
          </div>
        </div>
        <p v-if="editingOriginal" class="roster-form__meta">
          本月已值班 {{ dutyCountOfMonth(editingOriginal) }} 次
          <template v-if="lastDutyDate(editingOriginal)">
            · 上次 {{ lastDutyDate(editingOriginal) }}
          </template>
        </p>
      </div>
    </Drawer>
  </div>
</template>

<style scoped>
.roster-name {
  font-size: 13px;
  color: var(--dt-text);
}

.roster-status {
  font-size: 12px;
  padding: 1px 8px;
  border-radius: 999px;
}

.roster-status--ok {
  color: var(--dt-success);
  background: color-mix(in srgb, var(--dt-success) 12%, transparent);
}

.roster-status--off {
  color: var(--dt-text-3);
  background: var(--dt-surface-2);
}

.roster-count {
  font-weight: 600;
  color: var(--dt-text);
}

.roster-lastduty {
  font-size: 13px;
  color: var(--dt-text-2);
}

.roster-lastduty--none {
  color: var(--dt-text-3);
  font-size: 12px;
}

.roster-contact {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 13px;
  color: var(--dt-text-2);
}

.roster-contact--none {
  color: var(--dt-text-3);
}

.roster-empty-hint {
  margin: 8px 0 0;
  font-size: 12px;
  color: var(--dt-text-3);
}

.roster-form {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.roster-form__field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.roster-form__label {
  font-size: 13px;
  font-weight: 600;
  color: var(--dt-text);
}

.roster-form__switch {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
  color: var(--dt-text-2);
}

.roster-form__meta {
  margin: 0;
  font-size: 12px;
  color: var(--dt-text-3);
}
</style>
