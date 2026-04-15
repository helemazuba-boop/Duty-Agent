<script setup lang="ts">
import { ref, watch, computed } from 'vue';
import { Drawer, Button, DatePicker, Input, Select, Tag, Divider } from 'ant-design-vue';
import { DeleteOutlined } from '@ant-design/icons';
import type { ScheduleEntry, RosterPerson } from '@/types';

interface Props {
  open: boolean;
  date?: string | null;
  schedulePool?: ScheduleEntry[];
  roster?: RosterPerson[];
}

interface Emits {
  (e: 'save', entry: any): void;
  (e: 'update:open', value: boolean): void;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const assignments = ref<Record<string, string[]>>({});
const note = ref('');
const selectedDate = ref<string | undefined>(undefined);

watch(
  () => [props.open, props.date, props.schedulePool] as const,
  ([isOpen, date, pool]) => {
    const _isOpen = isOpen as boolean;
    const _date = date as string | null | undefined;
    const _pool = pool as ScheduleEntry[] | null | undefined;
    if (_isOpen && _date && _pool) {
      selectedDate.value = _date;
      const existing = _pool.find((s) => s.date === _date);
      if (existing) {
        assignments.value = existing.area_assignments || {};
        note.value = existing.note || '';
      } else {
        assignments.value = {};
        note.value = '';
      }
    } else if (_isOpen) {
      selectedDate.value = undefined;
      assignments.value = {};
      note.value = '';
    }
  },
  { immediate: true },
);

const rosterOptions = computed(() =>
  (props.roster || [])
    .filter((r) => r.active)
    .map((r) => ({ label: r.name, value: r.name })),
);

const addArea = () => {
  const key = `区域${Object.keys(assignments.value).length + 1}`;
  assignments.value = { ...assignments.value, [key]: [] };
};

const onPersonSelect = (area: string, val: string | number | Record<string, unknown>) => {
  addPersonToArea(area, String(val));
};

const addPersonToArea = (area: string, person: string) => {
  assignments.value = {
    ...assignments.value,
    [area]: [...(assignments.value[area] || []), person],
  };
};

const removePerson = (area: string, index: number) => {
  const newList = [...(assignments.value[area] || [])];
  newList.splice(index, 1);
  assignments.value = { ...assignments.value, [area]: newList };
};

const removeArea = (area: string) => {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { [area]: _, ...rest } = assignments.value;
  assignments.value = rest;
};

const handleClose = () => {
  emit('update:open', false);
};

const handleSave = () => {
  emit('save', {
    date: props.date,
    note: note.value,
    area_assignments: assignments.value,
  });
};
</script>

<template>
  <Drawer
    :open="open"
    :title="date ? '编辑排班' : '新建排班'"
    width="520"
    @close="handleClose"
  >
    <template #extra>
      <div style="display: flex; gap: 8px">
        <Button @click="handleClose">取消</Button>
        <Button type="primary" @click="handleSave">保存</Button>
      </div>
    </template>

    <div style="display: flex; flex-direction: column; gap: 16px; width: 100%">
      <div>
        <div style="margin-bottom: 8px; font-weight: 500">日期</div>
        <DatePicker
          v-model:value="selectedDate"
          style="width: 100%"
          :disabled="!!date"
        />
      </div>

      <div>
        <div style="margin-bottom: 8px; font-weight: 500">备注</div>
        <Input.TextArea v-model:value="note" :rows="2" placeholder="附加说明..." />
      </div>

      <Divider>安排区域</Divider>

      <div v-for="(persons, area) in assignments" :key="area" style="margin-bottom: 16px">
        <div style="display: flex; justify-content: space-between; margin-bottom: 8px">
          <strong>{{ area }}</strong>
          <Button
            type="text"
            danger
            size="small"
            @click="removeArea(area as string)"
          >
            <template #icon><DeleteOutlined /></template>
          </Button>
        </div>

        <div style="display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 8px">
          <Tag
            v-for="(person, idx) in persons"
            :key="idx"
            closable
            @close="removePerson(area as string, idx)"
          >
            {{ person }}
          </Tag>
        </div>

        <Select
          placeholder="添加人员"
          style="width: 100%"
          :options="rosterOptions"
          @select="((val: any) => onPersonSelect(area as string, val)) as any"
        />
      </div>

      <Button type="dashed" block @click="addArea">
        添加区域
      </Button>
    </div>
  </Drawer>
</template>
