import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import VirtualRosterList from './VirtualRosterList.vue';

const rows = [
  { id: 1, name: '甲', active: true, dutyCount: 3, lastDuty: '2026-09-20' },
  { id: 2, name: '乙', active: false, dutyCount: 0, lastDuty: null },
  { id: 3, name: '丙', active: true, dutyCount: 1, lastDuty: '2026-09-10' },
];

describe('VirtualRosterList', () => {
  it('渲染行并透出上下移事件', async () => {
    const wrapper = mount(VirtualRosterList, {
      props: { rows, firstId: 1, lastId: 3 },
    });
    expect(wrapper.text()).toContain('甲');
    expect(wrapper.text()).toContain('从未值班');

    const downBtns = wrapper.findAll('button[aria-label="下移"]');
    await downBtns[0]!.trigger('click');
    expect(wrapper.emitted('move-down')).toEqual([[1]]);
  });

  it('首位上移/末位下移禁用', () => {
    const wrapper = mount(VirtualRosterList, {
      props: { rows, firstId: 1, lastId: 3 },
    });
    const upBtns = wrapper.findAll('button[aria-label="上移"]');
    const downBtns = wrapper.findAll('button[aria-label="下移"]');
    expect((upBtns[0]!.element as HTMLButtonElement).disabled).toBe(true);
    expect((downBtns[2]!.element as HTMLButtonElement).disabled).toBe(true);
  });

  it('disabled 时全部按钮禁用', () => {
    const wrapper = mount(VirtualRosterList, {
      props: { rows, disabled: true, firstId: 1, lastId: 3 },
    });
    for (const b of wrapper.findAll('button')) {
      expect((b.element as HTMLButtonElement).disabled).toBe(true);
    }
  });
});
