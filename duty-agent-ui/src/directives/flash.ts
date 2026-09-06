/**
 * v-flash — 数据经 WS/轮询更新时给元素一次高亮闪烁。
 * 用法:v-flash="updatedAt"(值变化即触发一次,首次挂载不闪)。
 */
import type { Directive } from 'vue';

const initialized = new WeakSet<Element>();

export const vFlash: Directive<HTMLElement, number | string | undefined | null> = {
  mounted(_el, _binding) {
    initialized.add(_el);
  },
  updated(el, binding) {
    if (binding.value == null || binding.value === binding.oldValue) return;
    if (initialized.has(el)) {
      initialized.delete(el);
      return;
    }
    el.classList.remove('da-flash');
    // 强制 reflow 以重启动画
    void el.offsetWidth;
    el.classList.add('da-flash');
  },
};
