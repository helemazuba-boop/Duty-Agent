/**
 * 把 design tokens 映射成 Ant Design Vue ConfigProvider 的 theme 对象。
 *
 * antd-vue 4 的组件定制机制(genComponentStyleHook):`components.X` 里的字段
 * 会 merge 进该组件样式函数的 token,而不是 antd 5.7+ 的语义化组件 token。
 * 所以:Menu/Segmented/Button/Table/Form 要覆盖它们用到的**全局 token 别名**
 * (colorItemBg / colorBgLayout / controlOutline / padding …),
 * 语义化名字(itemSelectedBg / trackBg / primaryShadow / headerBg…)会被静默忽略。
 * Card/Drawer/Modal/Tag 恰好暴露了同名 ComponentToken,可以直接用。
 */
import { theme as antdTheme } from 'ant-design-vue';
import {
  DARK,
  FONT_FAMILY,
  LIGHT,
  RADII,
  SHADOW_OVERLAY,
  SHADOW_OVERLAY_DARK,
  SHADOW_SUBTLE,
  TYPE_SCALE,
} from './tokens';

export type ResolvedTheme = 'light' | 'dark';

export function makeAntdTheme(resolved: ResolvedTheme) {
  const dark = resolved === 'dark';
  const p = dark ? DARK : LIGHT;

  return {
    algorithm: dark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
    token: {
      // seed:让算法从我们的表面色/文字色派生整套 fill/text/disabled 阶梯,
      // 否则 dark 下 placeholder、Tooltip、mask、split 等仍按 #000/#fff 派生
      colorBgBase: p.surface,
      colorTextBase: p.text,
      colorPrimary: p.primary,
      colorInfo: p.live,
      colorSuccess: p.success,
      colorWarning: p.warning,
      colorError: p.danger,
      colorBgLayout: p.bg,
      colorBgContainer: p.surface,
      colorBgElevated: p.overlay,
      colorBgSpotlight: p.spotlight,
      colorBorder: p.borderStrong,
      colorBorderSecondary: p.border,
      colorTextSecondary: p.text2,
      colorTextTertiary: p.text3,
      colorFillAlter: p.surface2,
      fontFamily: FONT_FAMILY,
      fontSize: TYPE_SCALE.body,
      fontSizeHeading4: TYPE_SCALE.pageTitle,
      fontSizeHeading5: TYPE_SCALE.cardTitle,
      borderRadius: RADII.control,
      borderRadiusLG: RADII.panel,
      borderRadiusSM: 4,
      controlHeight: 32,
      lineWidth: 1,
      // boxShadow 被多个小件(Segmented 选中块/Slider 等)读取,保持轻;
      // 真正的悬浮层阴影走 boxShadowSecondary(Dropdown/Select/Popover/Tooltip)
      boxShadow: SHADOW_SUBTLE,
      boxShadowSecondary: dark ? SHADOW_OVERLAY_DARK : SHADOW_OVERLAY,
      motionDurationMid: '0.15s',
    },
    components: {
      // 注意:AppLayout 的侧栏是自绘 icon rail,不走 a-menu;
      // 这组配置预留给 Drawer/Popover 内未来使用 Menu 的场景。
      Menu: {
        colorItemBg: 'transparent',
        colorItemBgHover: p.hover,
        colorItemBgSelected: p.primarySoft,
        colorItemTextSelected: p.primary,
        radiusItem: RADII.control,
        itemMarginInline: 8,
        colorActiveBarBorderSize: 0,
      },
      Table: {
        fontSize: TYPE_SCALE.tableBody,
        padding: 12,
        paddingContentVerticalLG: 8,
        colorTextHeading: p.text2,
        borderRadiusLG: 0,
      },
      Segmented: {
        colorBgLayout: dark ? p.bg : '#F3F4F6',
        colorBgElevated: dark ? p.overlay : p.surface,
        colorFillSecondary: p.hover,
        boxShadow: dark ? 'none' : SHADOW_SUBTLE,
      },
      Button: {
        controlOutline: 'transparent',
        controlTmpOutline: 'transparent',
        colorErrorOutline: 'transparent',
      },
      Form: {
        colorTextHeading: dark ? p.text2 : '#374151',
        paddingXS: 6,
        marginLG: 20,
      },
      Card: { paddingLG: 20, borderRadiusLG: RADII.panel },
      Drawer: { paddingLG: 20 },
      Modal: { borderRadiusLG: RADII.overlay },
      Tag: { borderRadiusSM: 4 },
    },
  };
}
