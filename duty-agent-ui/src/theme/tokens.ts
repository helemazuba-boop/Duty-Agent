/**
 * Duty-Agent UI — Design tokens(TS 侧)。
 * 单一真值是 tokens.json;本文件只做类型化导出。
 * 消费方:theme/antd.ts(AntD ConfigProvider)、utils/personColor.ts。
 * 命名映射:TS text2 ↔ CSS --dt-text-2 ↔ Tailwind ink-2 ↔ AntD colorTextSecondary。
 *
 * CSS 变量由 `node scripts/gen-tokens.mjs` 从 tokens.json 生成到
 * src/styles/tokens.generated.css(dev/build 前自动执行),不要手改生成文件。
 */
import data from './tokens.json';

export interface PaletteTokens {
  primary: string;
  primarySoft: string;
  live: string;
  liveSoft: string;
  bg: string;
  surface: string;
  surface2: string;
  overlay: string;
  border: string;
  borderStrong: string;
  hover: string;
  text: string;
  text2: string;
  text3: string;
  success: string;
  warning: string;
  danger: string;
  selection: string;
  scrollbar: string;
  spotlight: string;
}

/** 人员色板三元组:fg=chip 文字 / bg=chip 底 / solid=头像实底 / onSolid=头像字色 */
export interface PersonColor {
  fg: string;
  bg: string;
  solid: string;
  onSolid: string;
}

export const LIGHT = data.light as PaletteTokens;
export const DARK = data.dark as PaletteTokens;
export const PERSON_PALETTE = data.personLight as PersonColor[];
export const PERSON_PALETTE_DARK = data.personDark as PersonColor[];

export const FONT_FAMILY = data.meta.fontFamily;

export const RADII = {
  control: data.meta.radiusControl,
  panel: data.meta.radiusPanel,
  overlay: data.meta.radiusOverlay,
} as const;

/** 阴影:卡片 0 阴影;shadowSubtle 给 Segmented 等小件,overlay 只给悬浮层("有阴影 = 临时层") */
export const SHADOW_OVERLAY = data.meta.shadowOverlay;
export const SHADOW_OVERLAY_DARK = data.meta.shadowOverlayDark;
export const SHADOW_SUBTLE = data.meta.shadowSubtle;

/** 字号阶梯(px):AntD token 与业务组件共用 */
export const TYPE_SCALE = {
  tableBody: data.meta.fontSizeTableBody,
  body: data.meta.fontSizeBody,
  cardTitle: data.meta.fontSizeCardTitle,
  pageTitle: data.meta.fontSizePageTitle,
} as const;
