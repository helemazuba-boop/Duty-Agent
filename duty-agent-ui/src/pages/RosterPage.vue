<script setup lang="ts">
/**
 * RosterPage 排序规则（触屏优先）：
 * - 手动顺序（orderMap，后端数组顺序）是唯一持久化语义：上/下移按钮 + 手柄拖拽都写它。
 * - 列 sorter 是纯视图排序：激活时禁用拖拽与上下移（避免“看到的顺序”与“存的顺序”打架），清空后回到手动顺序。
 * - persist 失败回滚：refetch 快照，用后端真源重建 roster/orderMap。
 */
import { computed, ref, watch, nextTick, onBeforeUnmount, onMounted } from 'vue';
import Sortable from 'sortablejs';
import { Table, Drawer, Input, Switch, Space, message, Popconfirm, Tooltip, Empty } from 'ant-design-vue';
import {
  DeleteOutlined,
  EditOutlined,
  HolderOutlined,
  UserAddOutlined,
  ReloadOutlined,
  UserSwitchOutlined,
  ArrowUpOutlined,
  ArrowDownOutlined,
} from '@ant-design/icons-vue';
import { api } from '@/api/http';
import { useSnapshotQuery } from '@/queries/useSnapshot';
import { queryClient } from '@/queries/client';
import { snapshotKey } from '@/queries/keys';
import type { RosterPerson, ScheduleEntry } from '@/types';
import PageHeader from '@/components/ui/PageHeader.vue';
import Panel from '@/components/ui/Panel.vue';
import PersonChip from '@/components/ui/PersonChip.vue';

const snapshotQuery = useSnapshotQuery();
const roster = ref<RosterPerson[]>([]);
const pool = ref<ScheduleEntry[]>([]);
const loading = ref(false);
const updatedAt = ref(0);
const pageSize = ref(15);
const currentPage = ref(1);

/** 列 sorter 是纯视图排序：激活期间禁用一切手动调序入口 */
const sorterState = ref<{ columnKey?: string; order?: 'ascend' | 'descend' | null }>({});
const isSortedView = computed(() => sorterState.value.order === 'ascend' || sorterState.value.order === 'descend');

/** 前端私有的显示顺序（后端 API schema extra="forbid"，不能传 order 字段） */
const orderMap = ref<Map<number, number>>(new Map());

/** 从后端返回的数组顺序同步 orderMap */
const syncOrderFromBackend = () => {
  const map = new Map<number, number>();
  roster.value.forEach((p, idx) => {
    map.set(p.id, idx * 10);
  });
  orderMap.value = map;
};

watch(
  () => snapshotQuery.data.value,
  (ws) => {
    if (ws) {
      roster.value = ws.roster ?? [];
      pool.value = ws.state?.schedule_pool ?? [];
      updatedAt.value = Date.now();
      syncOrderFromBackend();
    }
  },
  { immediate: true },
);
watch(
  () => snapshotQuery.isPending.value || snapshotQuery.isFetching.value,
  (v) => {
    loading.value = v;
  },
  { immediate: true },
);

const fetchAll = async () => {
  await snapshotQuery.refetch();
};

/** 当前页可见行：与 Table 的 current/pageSize 切片保持一致 */
const pagedRows = computed(() => {
  const start = (currentPage.value - 1) * pageSize.value;
  return dataSource.value.slice(start, start + pageSize.value);
});

/** 手柄拖拽（SortableJS pointer/touch）：只读写 orderMap，落点复用按钮通道的 persist */
const tableWrapRef = ref<HTMLElement | null>(null);
let sortable: Sortable | null = null;

const destroySortable = () => {
  sortable?.destroy();
  sortable = null;
};

const handleDragEnd = async (evt: { oldIndex?: number; newIndex?: number }) => {
  if (isSortedView.value) return;
  const { oldIndex, newIndex } = evt;
  if (oldIndex === undefined || newIndex === undefined || oldIndex === newIndex) return;
  const pageIds = pagedRows.value.map((r) => r.id);
  if (oldIndex < 0 || oldIndex >= pageIds.length || newIndex < 0 || newIndex >= pageIds.length) return;
  const [moved] = pageIds.splice(oldIndex, 1);
  pageIds.splice(newIndex, 0, moved);
  const full = orderedIds();
  const pageSet = new Set(pagedRows.value.map((r) => r.id));
  const rest = full.filter((id) => !pageSet.has(id));
  const anchor = full.findIndex((id) => pageSet.has(id));
  rest.splice(anchor < 0 ? rest.length : anchor, 0, ...pageIds);
  await setOrderByIds(rest);
};

const initSortable = () => {
  destroySortable();
  // 列排序视图下看到的顺序≠存的顺序：禁用拖拽，避免“拖的”与“存的”打架
  if (isSortedView.value) return;
  const tbody = tableWrapRef.value?.querySelector('.ant-table-tbody');
  if (!tbody || pagedRows.value.length === 0) return;
  sortable = new Sortable(tbody as HTMLElement, {
    handle: '.roster-drag-handle',
    draggable: 'tr.ant-table-row',
    animation: 150,
    delay: 120,
    delayOnTouchOnly: true,
    touchStartThreshold: 4,
    onEnd: (evt) => void handleDragEnd(evt),
  });
};

watch(
  [() => pagedRows.value.map((r) => r.key).join(','), currentPage, pageSize, isSortedView],
  () => {
    void nextTick(() => initSortable());
  },
  { immediate: false },
);

onBeforeUnmount(destroySortable);

onMounted(() => {
  void nextTick(() => initSortable());
});

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

const activeCount = computed(() => roster.value.filter((p) => p.active).length);
const inactiveCount = computed(() => roster.value.length - activeCount.value);

interface RosterRow extends RosterPerson {
  key: number | string;
  dutyCount: number;
  lastDuty: string | null;
}

const dataSource = computed<RosterRow[]>(() =>
  roster.value
    .slice()
    .sort((a, b) => (orderMap.value.get(a.id) ?? 9999) - (orderMap.value.get(b.id) ?? 9999))
    .map((r) => ({
      ...r,
      key: r.id,
      dutyCount: dutyCountOfMonth(r.name),
      lastDuty: lastDutyDate(r.name),
    })),
);

const columns = [
  {
    title: '',
    key: 'drag',
    width: 44,
    customCell: () => ({ class: 'roster-drag-handle' }),
  },
  {
    title: '顺序',
    key: 'sort',
    width: 100,
    align: 'center' as const,
  },
  { title: 'ID', dataIndex: 'id', key: 'id', width: 80 },
  { title: '成员', dataIndex: 'name', key: 'name', minWidth: 180, sorter: (a: any, b: any) => a.name.localeCompare(b.name) },
  { title: '状态', dataIndex: 'active', width: 90, align: 'center' as const, sorter: (a: any, b: any) => Number(b.active) - Number(a.active) },
  { title: '本月值班', dataIndex: 'dutyCount', width: 100, align: 'center' as const, key: 'dutyCount', sorter: (a: any, b: any) => a.dutyCount - b.dutyCount },
  { title: '上次值班', dataIndex: 'lastDuty', width: 120, key: 'lastDuty', sorter: (a: any, b: any) => (a.lastDuty || '').localeCompare(b.lastDuty || '') },
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
  try {
    await persist(updated);
  } catch {
    return;
  }
  syncOrderFromBackend();
  drawerOpen.value = false;
  message.success(editingOriginal.value ? '修改成功' : '添加成功');
};

const handleDelete = async (name: string) => {
  try {
    await persist(roster.value.filter((r) => r.name !== name));
  } catch {
    return;
  }
  message.success('删除成功');
};

const toggleActive = async (person: RosterPerson) => {
  const updated = roster.value.map((r) =>
    r.name === person.name ? { ...r, active: !r.active } : r,
  );
  try {
    await persist(updated);
  } catch {
    return;
  }
  message.success(updated.find((r) => r.name === person.name)?.active ? '已设为在职' : '已设为离职');
};

const persist = async (updated: RosterPerson[]) => {
  try {
    const payload = updated.map(({ id, name, active }) => ({ id, name, active }));
    roster.value = await api.updateRoster(payload);
    await queryClient.invalidateQueries({ queryKey: snapshotKey });
  } catch (e) {
    message.error('保存失败，已恢复为服务器顺序');
    await snapshotQuery.refetch();
    throw e;
  }
};

/** 手动顺序唯一真源：按 id 序列重写 orderMap 并持久化 */
const setOrderByIds = async (ids: number[]) => {
  const byId = new Map(roster.value.map((r) => [r.id, r]));
  const ordered: RosterPerson[] = [];
  for (const id of ids) {
    const p = byId.get(id);
    if (p) ordered.push(p);
  }
  for (const r of roster.value) {
    if (!ids.includes(r.id)) ordered.push(r);
  }
  const map = new Map<number, number>();
  ordered.forEach((p, idx) => map.set(p.id, idx * 10));
  orderMap.value = map;
  try {
    await persist(ordered.map(({ id, name, active }) => ({ id, name, active })));
  } catch {
    /* persist 内已回滚 + toast，这里吞掉避免按钮点击产生未处理 rejection */
  }
};

const orderedIds = () => dataSource.value.map((r) => r.id);

const moveUp = async (id: number) => {
  if (isSortedView.value) return;
  const ids = orderedIds();
  const i = ids.indexOf(id);
  if (i <= 0) return;
  [ids[i - 1], ids[i]] = [ids[i], ids[i - 1]];
  await setOrderByIds(ids);
};

const moveDown = async (id: number) => {
  if (isSortedView.value) return;
  const ids = orderedIds();
  const i = ids.indexOf(id);
  if (i < 0 || i >= ids.length - 1) return;
  [ids[i], ids[i + 1]] = [ids[i + 1], ids[i]];
  await setOrderByIds(ids);
};

const isFirst = (id: number) => orderedIds()[0] === id;
const isLast = (id: number) => orderedIds()[orderedIds().length - 1] === id;

const handleTableChange = (
  pagination?: { current?: number; pageSize?: number },
  _filters?: unknown,
  sorter?: { columnKey?: string | number; order?: 'ascend' | 'descend' | null } | Array<{ columnKey?: string | number; order?: 'ascend' | 'descend' | null }>,
) => {
  if (pagination?.current) currentPage.value = pagination.current;
  if (pagination?.pageSize) pageSize.value = pagination.pageSize;
  const s = Array.isArray(sorter) ? sorter[0] : sorter;
  sorterState.value = s ? { columnKey: s.columnKey !== undefined ? String(s.columnKey) : undefined, order: s.order ?? null } : {};
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
      <div ref="tableWrapRef">
      <Table
        :columns="columns"
        :data-source="dataSource"
        :loading="loading"
        :pagination="{ current: currentPage, showSizeChanger: true, pageSizeOptions: ['10', '15', '30'], showTotal: (total: number) => `共 ${total} 人` }"
        v-model:pageSize="pageSize"
        row-key="id"
        @change="handleTableChange"
        size="middle"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'drag'">
            <span
              class="roster-drag-handle"
              :class="{ 'roster-drag-handle--disabled': isSortedView }"
              :title="isSortedView ? '列排序时不可拖拽，清空排序后恢复' : '按住拖拽排序'"
            >
              <HolderOutlined />
            </span>
          </template>

          <template v-else-if="column.key === 'sort'">
            <Space size="small" style="justify-content: center; display: flex">
              <Tooltip :title="isSortedView ? '列排序时不可调序' : '上移'">
                <a-button
                  type="text"
                  :disabled="isSortedView || isFirst((record as RosterPerson).id)"
                  @click="moveUp((record as RosterPerson).id)"
                  aria-label="上移"
                >
                  <template #icon><ArrowUpOutlined /></template>
                </a-button>
              </Tooltip>
              <Tooltip :title="isSortedView ? '列排序时不可调序' : '下移'">
                <a-button
                  type="text"
                  :disabled="isSortedView || isLast((record as RosterPerson).id)"
                  @click="moveDown((record as RosterPerson).id)"
                  aria-label="下移"
                >
                  <template #icon><ArrowDownOutlined /></template>
                </a-button>
              </Tooltip>
            </Space>
          </template>
          <template v-else-if="column.key === 'name'">
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
      </div>
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

.roster-drag-handle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  color: var(--dt-text-3);
  cursor: grab;
  font-size: 16px;
  line-height: 1;
  /* 手柄是拖拽起点：不抢滚动，只有手柄禁掉触屏滚动 */
  touch-action: none;
  user-select: none;
  -webkit-user-select: none;
}

.roster-drag-handle:active {
  cursor: grabbing;
}

.roster-drag-handle--disabled {
  opacity: 0.35;
  cursor: not-allowed;
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
