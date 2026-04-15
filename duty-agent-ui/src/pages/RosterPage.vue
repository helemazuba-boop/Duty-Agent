<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { Table, Tag, Space, Modal, Input, message, Popconfirm, Tooltip } from 'ant-design-vue';
import { EditOutlined, DeleteOutlined, UserAddOutlined, ReloadOutlined, UserSwitchOutlined } from '@ant-design/icons';
import type { RosterPerson } from '@/types';
import { useRoster } from '@/composables/useRoster';

const { roster, loading, fetch, update } = useRoster();
const editingPerson = ref<{ name: string; active: boolean } | null>(null);
const editModalOpen = ref(false);
const newName = ref('');

onMounted(() => {
  fetch();
});

const columns = [
  {
    title: '序号',
    dataIndex: 'index',
    width: 70,
    align: 'center' as const,
  },
  { title: '姓名', dataIndex: 'name', minWidth: 120 },
  {
    title: '状态',
    dataIndex: 'active',
    width: 100,
    align: 'center' as const,
  },
  { title: '操作', width: 200, align: 'center' as const, key: 'action' },
];

const showData = ref<RosterPerson[]>([]);

const fetchAndSync = async () => {
  await fetch();
  syncTable();
};

const syncTable = () => {
  const rosterVal = roster.value;
  showData.value = rosterVal.map((r, i) => ({ ...r, index: i + 1 }));
};

roster; // touch for reactivity

const handleAdd = () => {
  newName.value = '';
  editingPerson.value = null;
  editModalOpen.value = true;
};

const handleEdit = (person: RosterPerson) => {
  newName.value = person.name;
  editingPerson.value = { name: person.name, active: person.active };
  editModalOpen.value = true;
};

const handleDelete = async (name: string) => {
  const updated = roster.value.filter((r) => r.name !== name);
  await update(updated);
  message.success('删除成功');
  syncTable();
};

const handleSaveEdit = async () => {
  if (!newName.value.trim()) {
    message.error('请输入姓名');
    return;
  }

  let updated: RosterPerson[];
  if (editingPerson.value) {
    updated = roster.value.map((r) =>
      r.name === editingPerson.value!.name
        ? { ...r, name: newName.value.trim() }
        : r,
    );
  } else {
    // Prevent duplicates
    if (roster.value.some((r) => r.name === newName.value.trim())) {
      message.error('该姓名已存在');
      return;
    }
    updated = [...roster.value, { name: newName.value.trim(), active: true }];
  }

  await update(updated);
  editModalOpen.value = false;
  message.success(editingPerson.value ? '修改成功' : '添加成功');
  syncTable();
};

const toggleActive = async (person: RosterPerson) => {
  const updated = roster.value.map((r) =>
    r.name === person.name ? { ...r, active: !r.active } : r,
  );
  await update(updated);
  message.success(updated.find((r) => r.name === person.name)!.active ? '已设为在职' : '已设为离职');
  syncTable();
};

const activeCount = ref(0);
const inactiveCount = ref(0);
</script>

<template>
  <div class="page-container animate-fade-in">
    <!-- Page header -->
    <div class="page-header">
      <div>
        <h1 class="page-title">花名册管理</h1>
        <p class="page-subtitle">
          共 {{ roster.length }} 人
          <template v-if="roster.length > 0">
            ，<span style="color: #52c41a">{{ activeCount }}</span> 在职 /
            <span style="color: #bfbfbf">{{ inactiveCount }}</span> 离职
          </template>
        </p>
      </div>
      <Space>
        <a-button @click="fetchAndSync" :loading="loading">
          <template #icon><ReloadOutlined /></template>
          刷新
        </a-button>
        <a-button type="primary" @click="handleAdd">
          <template #icon><UserAddOutlined /></template>
          添加人员
        </a-button>
      </Space>
    </div>

    <!-- Roster Table Card -->
    <a-card :bordered="false" class="animate-fade-in-up">
      <Table
        :columns="columns"
        :data-source="showData"
        :loading="loading"
        :pagination="{ pageSize: 15, showSizeChanger: true, pageSizeOptions: ['10', '15', '30'], showTotal: (total: number) => `共 ${total} 人` }"
        row-key="name"
        size="middle"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.dataIndex === 'index'">
            <span style="color: var(--da-text-muted); font-size: 12px">{{ record.index }}</span>
          </template>
          <template v-else-if="column.dataIndex === 'active'">
            <Tag :color="record.active ? 'success' : 'default'" style="border-radius: 12px; padding: 0 8px">
              {{ record.active ? '在职' : '离职' }}
            </Tag>
          </template>
          <template v-else-if="column.key === 'action'">
            <Space size="small" style="justify-content: center; display: flex">
              <Tooltip title="编辑">
                <a-button size="small" type="text" @click="handleEdit(record as RosterPerson)">
                  <template #icon><EditOutlined /></template>
                </a-button>
              </Tooltip>
              <Tooltip :title="record.active ? '设为离职' : '设为在职'">
                <a-button size="small" type="text" @click="toggleActive(record as RosterPerson)">
                  <template #icon><UserSwitchOutlined /></template>
                </a-button>
              </Tooltip>
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
      </Table>

      <div v-if="!roster.length && !loading" class="empty-state">
        <div class="empty-state-icon">👥</div>
        <div class="empty-state-title">暂无人员</div>
        <div class="empty-state-desc">点击右上角「添加人员」开始管理花名册</div>
      </div>
    </a-card>

    <!-- Add/Edit Modal -->
    <Modal
      v-model:open="editModalOpen"
      :title="editingPerson ? '编辑人员' : '添加人员'"
      ok-text="保存"
      cancel-text="取消"
      @ok="handleSaveEdit"
    >
      <div style="display: flex; align-items: center; gap: 12px; padding: 8px 0">
        <div style="width: 60px; font-weight: 500">姓名</div>
        <Input
          v-model:value="newName"
          :placeholder="editingPerson ? '修改姓名' : '输入新人员姓名'"
          autofocus
          @keyup.enter="handleSaveEdit"
        />
      </div>
    </Modal>
  </div>
</template>

<style scoped>
/* Table centered columns */
:deep(.ant-table-cell) {
  text-align: center !important;
}

:deep(.ant-table-cell:nth-child(2)) {
  text-align: left !important;
}
</style>
