<script setup lang="ts">
import { ref, watch, computed } from 'vue';
import { Drawer, Button, DatePicker, Input, Select, Divider } from 'ant-design-vue';
import { DeleteOutlined } from '@ant-design/icons-vue';
import type { ScheduleEntry, RosterPerson } from '@/types';
import PersonChip from '@/components/ui/PersonChip.vue';

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

/** 值班人选择器:头像 + 最近值班日期(辅助公平判断) */
const lastDutyOf = (name: string): string | null => {
  const pool = props.schedulePool ?? [];
  const before = selectedDate.value ?? '';
  let last: string | null = null;
  for (const entry of pool) {
    if (before && entry.date >= before) continue;
    const persons = Object.values(entry.area_assignments ?? {}).flat();
    if (persons.includes(name) && (!last || entry.date > last)) last = entry.date;
  }
  return last;
};

const rosterOptions = computed(() =>
  (props.roster || [])
    .filter((r) => r.active)
    .map((r) => {
      const last = lastDutyOf(r.name);
      return {
        label: last ? `${r.name}　·　上次值班 ${last}` : `${r.name}　·　暂无值班记录`,
        value: r.name,
      };
    }),
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

const renameArea = (area: string, evt: Event) => {
  const next = (evt.target as HTMLInputElement).value.trim() || area;
  if (next === area) return;
  const entries = Object.entries(assignments.value).map(([k, v]) => (k === area ? [next, v] : [k, v]));
  assignments.value = Object.fromEntries(entries);
};

const handleClose = () => {
  emit('update:open', false);
};

const handleSave = () => {
  emit('save', {
    date: selectedDate.value ?? props.date ?? '',
    note: note.value,
    area_assignments: assignments.value,
  });
};
</script>

<template>
  <Drawer
    :open="open"
    :title="date ? '编辑排班' : '新建排班'"
    width="420"
    @close="handleClose"
  >
    <template #extra>
      <div class="editor-actions">
        <Button @click="handleClose">取消</Button>
        <Button type="primary" @click="handleSave">保存</Button>
      </div>
    </template>

    <div class="editor">
      <div class="editor__field">
        <label class="editor__label">日期</label>
        <DatePicker v-model:value="selectedDate" style="width: 100%" :disabled="!!date" />
      </div>

      <div class="editor__field">
        <label class="editor__label">备注</label>
        <Input.TextArea v-model:value="note" :rows="2" placeholder="附加说明..." />
      </div>

      <Divider class="editor__divider">安排区域</Divider>

      <div v-for="(persons, area) in assignments" :key="area" class="editor__area">
        <div class="editor__area-head">
          <input
            class="editor__area-name"
            :value="area"
            @change="renameArea(area as string, $event)"
          />
          <Button type="text" danger size="small" @click="removeArea(area as string)">
            <template #icon><DeleteOutlined /></template>
          </Button>
        </div>

        <div class="editor__chips">
          <PersonChip
            v-for="(person, idx) in persons"
            :key="`${area}-${person}-${idx}`"
            :name="person"
            avatar
            closable
            @close="removePerson(area as string, idx)"
          />
          <span v-if="!persons.length" class="editor__empty">尚未添加人员</span>
        </div>

        <Select
          placeholder="添加人员(显示上次值班日期)"
          style="width: 100%"
          :options="rosterOptions"
          :show-search="true"
          option-filter-prop="label"
          @select="((val: any) => onPersonSelect(area as string, val)) as any"
        />
      </div>

      <Button type="dashed" block @click="addArea">添加区域</Button>
    </div>
  </Drawer>
</template>

<style scoped>
.editor-actions {
  display: flex;
  gap: 8px;
}

.editor {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.editor__field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.editor__label {
  font-size: 13px;
  font-weight: 600;
  color: var(--dt-text);
}

.editor__divider {
  margin: 4px 0;
}

.editor__area {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--dt-border-2);
  border-radius: var(--dt-radius);
  background: var(--dt-surface-2);
}

.editor__area-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.editor__area-name {
  flex: 1;
  min-width: 0;
  border: 1px solid transparent;
  border-radius: var(--dt-radius);
  background: transparent;
  font-size: 13px;
  font-weight: 600;
  color: var(--dt-text);
  padding: 2px 6px;
}

.editor__area-name:hover,
.editor__area-name:focus {
  border-color: var(--dt-border);
  background: var(--dt-surface);
  outline: none;
}

.editor__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.editor__empty {
  font-size: 12px;
  color: var(--dt-text-3);
}
</style>
