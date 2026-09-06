/**
 * 人员 → 颜色:按姓名 hash 从固定色板取色,
 * 日历 chip、头像、花名册、值班卡全站一致。
 * 色板是显式三元组(fg/bg/solid/onSolid),不用 color-mix ——
 * 凡是要和 AntD 组件并排出现的浅底色都用显式 token,避免两套混色算法的色偏。
 */
import { PERSON_PALETTE, PERSON_PALETTE_DARK, type PersonColor } from '@/theme/tokens';
import type { ResolvedTheme } from '@/theme/antd';

function hashName(name: string): number {
  let h = 0;
  for (let i = 0; i < name.length; i += 1) {
    h = (h * 31 + name.charCodeAt(i)) >>> 0;
  }
  return h;
}

export function personColor(name: string, resolved: ResolvedTheme = 'light'): PersonColor {
  const palette = resolved === 'dark' ? PERSON_PALETTE_DARK : PERSON_PALETTE;
  if (!name) return palette[0]!;
  return palette[hashName(name) % palette.length]!;
}

/** 取姓名首字作为头像字符(中文取姓,英文取首字母大写) */
export function personInitial(name: string): string {
  const trimmed = (name || '').trim();
  return trimmed ? trimmed[0]!.toUpperCase() : '?';
}

/**
 * chip / 头像的四个 CSS 变量:
 * --person-fg / --person-bg / --person-solid / --person-on-solid
 */
export function personChipStyle(name: string, resolved: ResolvedTheme = 'light') {
  const c = personColor(name, resolved);
  return {
    '--person-fg': c.fg,
    '--person-bg': c.bg,
    '--person-solid': c.solid,
    '--person-on-solid': c.onSolid,
  } as Record<string, string>;
}
