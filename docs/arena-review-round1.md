# Duty-Agent UI · Calm Ops 第一轮实现评审

> 评审范围:11 个源文件(theme/*、styles/*、AppLayout、PersonChip、StatCard、Dashboard、ScheduleCalendar、SchedulePage)。
> 严重度:**P0** 会翻车/明显错误 · **P1** 影响可用性或视觉一致性,本轮必修 · **P2** 值得修 · **P3** 打磨。
> 结论先行:方向落地得很干净,五个基础组件和 icon rail 的骨架可以直接沿用。最大的坑在 **antd-vue 4 的组件 token 名**(一半配置根本没生效)和 **flash 动效的实现方式**;其次是几处会在真实使用中暴露的逻辑问题(IME 回车、跨午夜、周起点不一致)。

---

## 0. 事实核对(决定后面几条结论)

已对照 `vueComponent/ant-design-vue` main 分支源码:

| 组件 | `ComponentToken` 实际字段 | 你传的字段 | 结果 |
|---|---|---|---|
| Menu | `colorItemBg / colorItemBgHover / colorItemBgSelected / colorItemTextSelected / radiusItem / itemMarginInline / colorActiveBarBorderSize`(antd 5.0–5.5 命名) | `itemBg / itemSelectedBg / itemSelectedColor / itemHoverBg / itemBorderRadius / iconSize / activeBarBorderWidth` | **除 `itemMarginInline` 外全部被忽略** |
| Segmented | `{}`(空) | `itemSelectedBg / trackBg` | 忽略。内部取 `colorBgLayout`(轨道)、`colorBgElevated`(选中)、`colorFillSecondary`(hover)、**`boxShadow`(选中块阴影)** |
| Button | `{}`(空) | `primaryShadow / defaultShadow / fontWeight` | 忽略。阴影来自 `controlOutline / controlTmpOutline / colorErrorOutline`,`fontWeight: 400` 硬编码 |
| Table | `{}`(空) | `headerBg / headerColor / headerSplitColor / rowHoverBg / cellPadding* / cellFontSize / headerBorderRadius` | 忽略。派生自 `colorFillAlter`(表头/行 hover)、`colorTextHeading`(表头字色)、`colorBorderSecondary`(线 + 表头分隔)、`fontSize`、`padding`、`paddingContentVerticalLG` |
| Form | 无 | `labelColor / verticalLabelPadding / itemMarginBottom` | 忽略。label = `colorTextHeading`,垂直 label padding = `paddingXS`,item 间距 = `marginLG` |
| Card / Drawer / Modal / Tag | 无(用全局 token) | `paddingLG / borderRadiusLG / borderRadiusSM` | **生效**(见下一行原因);`headerBg / headerFontSize` 忽略 |

关键机制(`theme/util/genComponentStyleHook.ts`):

```ts
const mergedComponentToken = { ...defaultComponentToken, ...token.value[component] };
const mergedToken = mergeToken(proxyToken, { componentCls, ... }, mergedComponentToken);
styleFn(mergedToken, ...)
```

即 `components.Table = { fontSize: 13 }` 会让 Table 的样式函数里 `token.fontSize === 13`,而不影响其它组件。**这是 antd-vue 4 上做组件级定制的正确姿势**:覆盖该组件用到的全局 token,而不是 antd 5.7+ 的语义化组件 token。

---

## 1. Token 体系分层

**【P0】`src/theme/antd.ts` 全文 → 按 §0 重写 `components`,并补 seed token**

现状:`colorBgBase / colorTextBase` 没设,dark 模式下 AntD 仍按 `#000 / #fff` 派生 `colorTextQuaternary`(placeholder、disabled)、`colorBgContainerDisabled`、`colorFill*`、`colorBgSpotlight`(Tooltip)、`colorBgMask`、`colorSplit` 等一整层没被你覆盖的 map token,和 `#161920` 系列并不同源。light 模式同理:`colorTextBase=#000` 派生的 `rgba(0,0,0,.25)` 占位色和你的 `#9CA3AF` 不是一套灰。

```ts
// src/theme/antd.ts —— 替换 token / components 两段
token: {
  // seed:让算法从我们的表面色/文字色派生整套 fill/text 阶梯
  colorBgBase: p.surface,          // light #FFFFFF / dark #161920
  colorTextBase: p.text,           // light #111827 / dark #E6E8EB
  colorPrimary: p.primary,
  colorInfo: p.live,
  colorSuccess: p.success,
  colorWarning: p.warning,
  colorError: p.danger,
  colorBgLayout: p.bg,
  colorBgElevated: p.overlay,
  colorBgSpotlight: dark ? '#2A2F3A' : '#1F2937',   // Tooltip 底色
  colorBorder: p.borderStrong,     // 新增 token,见 §6-1(输入框/选择器描边)
  colorBorderSecondary: p.border,  // 分割线、表格线、卡片边
  colorTextSecondary: p.text2,
  colorTextTertiary: p.text3,
  colorFillAlter: p.surface2,      // 表头、斑马纹、行 hover
  // 删除 colorFillQuaternary 覆盖:它是半透明填充,给成不透明会在有色底上露白
  fontFamily: FONT_FAMILY,
  fontSize: TYPE_SCALE.body.size,
  fontSizeHeading4: TYPE_SCALE.pageTitle.size,
  fontSizeHeading5: TYPE_SCALE.cardTitle.size,
  borderRadius: RADII.control,
  borderRadiusLG: RADII.panel,
  borderRadiusSM: 4,
  controlHeight: 32,
  lineWidth: 1,
  boxShadow: dark ? SHADOW_OVERLAY_DARK : SHADOW_OVERLAY,
  boxShadowSecondary: dark ? SHADOW_OVERLAY_DARK : SHADOW_OVERLAY,
  motionDurationMid: '0.15s',
},
components: {
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
    fontSize: TYPE_SCALE.tableBody.size,   // 13
    padding: 12,                            // 水平 cell padding
    paddingContentVerticalLG: 8,            // 垂直 cell padding
    colorTextHeading: p.text2,              // 表头字色
    borderRadiusLG: 0,
  },
  Segmented: {
    colorBgLayout: dark ? p.bg : '#F3F4F6',         // 轨道
    colorBgElevated: dark ? p.overlay : p.surface,  // 选中块(dark 必须比轨道亮)
    colorFillSecondary: p.hover,
    boxShadow: dark ? 'none' : '0 1px 2px rgba(0,0,0,0.06)',  // ← 否则选中块会套上 24px 的悬浮层阴影
  },
  Button: {
    controlOutline: 'transparent',      // primary 底部 2px 阴影
    controlTmpOutline: 'transparent',   // default 底部阴影
    colorErrorOutline: 'transparent',
  },
  Form: {
    colorTextHeading: dark ? p.text2 : '#374151',
    paddingXS: 6,     // 垂直布局 label 下间距
    marginLG: 20,     // item 间距
  },
  Card: { paddingLG: 20, borderRadiusLG: RADII.panel },
  Drawer: { paddingLG: 20 },
  Modal: { borderRadiusLG: RADII.overlay },
  Tag: { borderRadiusSM: 4 },
},
```

配套:Button 字重没有 token,放到 base.css(注意 cssinjs 的 `<style>` 在运行时追加到 head 末尾,同特异度会赢,所以要叠一层类):

```css
.ant-btn.ant-btn { font-weight: 500; }
```

`PaletteTokens` 补两个字段并同步到 CSS:`hover`(现在 CSS 有 `--dt-hover`、TS 里 Menu 用 `#F3F4F6` 硬编码,两处不同源)、`borderStrong`(见 §6-1)。

**【P1】`tokens.ts` ↔ `tokens.css` 是两份手抄的真值**

"单一 token 源"目前只在文档里成立:16 个色值 × 2 主题 + 圆角 + 阴影全部在 CSS 里重抄了一遍,改一处必漏另一处(`--da-primary-dark: #4338ca` 就已经没有 TS 对应)。二选一:

- **A(推荐,零运行时成本)**:`scripts/gen-tokens.mjs` 读取 `tokens.ts` 生成 `src/styles/tokens.generated.css`(`:root` + `[data-theme='dark']` 两块),`package.json` 的 `dev/build` 前置 `node scripts/gen-tokens.mjs`,生成文件加 `/* AUTO-GENERATED */` 头并进 git。`tokens.css` 只保留 `@theme inline` 和 `--da-*` 别名。
- **B(最省事)**:`useTheme.ts` 在 `watchEffect` 里 `for (const [k,v] of Object.entries(palette)) root.style.setProperty('--dt-'+kebab(k), v)`。缺点是首帧靠 JS,要配合 §7-3 的 inline script。

**【P1】`tokens.css` 第 1 行 `@import 'tailwindcss'` 与 `base.css` 的分层关系**

Tailwind v4 会把自己的样式放进 `@layer theme/base/components/utilities`;`base.css` 若是 main.ts 单独 import 的,它是**未分层** CSS,规则上永远压过任何 layer 内的 utility,不看特异度。后果:`.stat-value { font-size: 28px }` 之类的过渡类,在同元素上 `text-sm` 永远无效,排查会很痛苦。改成单入口:

```css
/* src/styles/index.css(main.ts 只 import 这一个) */
@import 'tailwindcss';
@import './tokens.css';                    /* :root 变量 + @theme inline,保持未分层 */
@import './base.css' layer(components);    /* 过渡类、动画类进 components 层,utility 可覆盖 */
```

`tokens.css` 删除自己的 `@import 'tailwindcss'`。

**【P2】`@theme inline` 用法本身正确,但有两个副作用没处理**

- `inline` 意味着 **不会** 生成 `--color-primary`、`--font-sans` 这些变量到 `:root`。`--font-sans` 写在 `@theme inline` 里是个字面量,body 里又抄了一遍字体栈 → 把字体栈也变成 `--dt-font-sans` 放 `:root`,两处都 `var()` 它。
- 缺几个高频 token 没暴露:`--color-hover: var(--dt-hover)`、`--radius-control: var(--dt-radius)`、`--radius-overlay: var(--dt-radius-xl)`、`--shadow-overlay: var(--dt-shadow-overlay)`。否则业务里会出现 `bg-[var(--dt-hover)]` 这种逃逸写法。
- 命名三套并存:TS `text2` / CSS `--dt-text-2` / Tailwind `ink-2` / AntD `colorTextSecondary`。可以接受(`text-text-2` 确实难看),但请在 `tokens.ts` 顶部放一张映射表注释,否则三个月后没人记得 `ink` 是什么。

**【P2】`--da-*` 别名过渡方案的隐患**

- `--da-bg-sidebar: var(--dt-surface)`:旧侧栏是深色,旧页面里凡是"在 sidebar 底上写白字"的样式现在会白字白底。grep `--da-bg-sidebar` 确认没有残留消费者,有就直接删这条别名让它报错(比静默错更好)。
- `--da-purple: #7c3aed` 没有 dark 覆盖,在 `#161920` 上对比度 ≈3.1:1,加 `[data-theme='dark'] { --da-purple: #a78bfa; }`。
- `--da-shadow-sm: none`:`box-shadow: var(--da-shadow-sm)` 没问题,但如果旧代码有 `filter: drop-shadow(var(--da-shadow-sm))` 会整条声明失效。grep 一下。
- `--da-transition: all 0.15s ease`:`all` 会把 §4 的 flash 动画、以及以后任何 `color-mix` 计算色都拉进过渡,建议别名改成 `background-color 0.15s, color 0.15s, border-color 0.15s`。
- 把所有 `--da-*` 挪到独立的 `legacy-aliases.css`,加一条 CI/lint:`grep -rn -- '--da-' src --include=*.vue | wc -l` 输出到构建日志,数字归零那天删文件。

**【P3】`tokens.ts` 里 `TYPE_SCALE`、`RADII` 定义了但 `antd.ts` 全是魔法数(6/10/4、14/20/16)** → 用上(上面的示例已替换)。

---

## 2. AntD ConfigProvider 映射(补充 §1 的 P0)

**【P1】Segmented dark 模式选中态反了** — 你的意图是 `itemSelectedBg = surface(#161920)`、`trackBg = surface2(#1C2028)`,即使字段生效,选中块也会比轨道更暗,视觉上像"凹下去"。dark 应为轨道 `bg(#0F1115)` / 选中 `overlay(#1F242E)`。已含在 §1 代码里。

**【P1】全局 `boxShadow` 被设成悬浮层阴影 `0 8px 24px`** — 这个 token 在 AntD 内部不只给 Popover 用。已确认 Segmented 选中块直接读 `token.boxShadow`;其它读 `boxShadow` 的还有 Slider/ColorPicker 等小件。建议:全局 `boxShadow` 保留 AntD 默认(或设成较轻的 `0 1px 2px rgba(0,0,0,.06)`),把 `boxShadowSecondary`(Dropdown/Select/Popover/Tooltip 用的那个)设成你的 `SHADOW_OVERLAY`。

**【P2】`AppLayout` 根本没用 AntD Menu** — Menu 的 components 配置目前是死配置,不删也行(Dropdown 不走它),但注释一句"预留给以后可能用 Menu 的 Drawer/Popover 场景",否则下个人会以为侧栏是 Menu 渲染的。

**【P2】Table 的 `headerSplitColor: 'transparent'` 意图无法单独达成** — 表头分隔竖线和表格横线在 antd-vue 4 里同源(`colorBorderSecondary`)。要去掉竖线只能 CSS:

```css
.ant-table-thead > tr > th::before { display: none !important; }
```

**【P3】没有 `Layout` 组件的映射是对的**(你自己画了 shell),但如果任何旧页面还用 `<a-layout>`,它的 `colorBgHeader/Body` 会走默认值。grep `a-layout` 确认。

---

## 3. AppLayout icon rail

**【P1】`AppLayout.vue` 连接状态区:三处语义不一致**

1. Tooltip 写"点击重新检测",实际点击是 `RouterLink to="/settings"`。改成 `<button @click="checkConnection">`,error 态下再在 Tooltip 里放"前往设置"链接;或者保持跳转但把文案改对。
2. `connectionStatus` 在每 20s 轮询时 `checking=true` → 状态点每 20 秒闪一次灰。改为只在首次未知时显示 pending:

```ts
const connectionStatus = computed(() => {
  if (backendOk.value == null) return 'pending';
  return backendOk.value ? 'ok' : 'error';
});
```

3. `:pulse="connectionStatus === 'ok'"` —— 健康态常驻脉冲是噪音,pulse 应该表达"正在发生"(pending/重连中)或"刚刚变化"。改成 `:pulse="connectionStatus === 'pending'"`,ok 态用静止实心点。

**【P1】主题切换三态共用一个图标** — 折叠态下只有 hover 才知道现在是哪个模式。`@ant-design/icons-vue` 没有太阳/月亮,用 `BulbOutlined`(浅) / `BulbFilled`(深) / `DesktopOutlined`(跟随),或者直接内联三个 16px 的 lucide SVG。同时在 `auto` 模式下按钮右下角加 2px 的 `live` 色小点表示"自动"。

**【P2】折叠动效:label 用 `v-show` 瞬时出现,和 180ms 的宽度过渡打架**(文字会先被 `overflow:hidden` 裁掉右半再露出来)。

```css
.app-nav-item__label,
.app-brand__name,
.app-conn__label,
.app-sidebar__btn-label {
  opacity: 0;
  transition: opacity 0.12s ease;
}
.app-shell--expanded :is(.app-nav-item__label, .app-brand__name, .app-conn__label, .app-sidebar__btn-label) {
  opacity: 1;
  transition-delay: 0.08s;   /* 等宽度撑开再淡入 */
}
```

模板里把这四处的 `v-show="expanded"` 去掉(保留 DOM,靠 opacity),收起时因为 `overflow:hidden` 不会溢出。

**【P2】可访问性/语义**
- 五个 `RouterLink` 外包一层 `<nav aria-label="主导航">`;激活项加 `:aria-current="isActive ? 'page' : undefined"`。
- 折叠态下 `<a>` 里只剩图标,Tooltip 不是可访问名称 → 加 `:aria-label="item.label"`。
- `app-plan__toggle`(SchedulePage)是 `div` 加 click,同类问题见 §5。

**【P2】内容区高度模型没定** — `.app-content { padding: 0 24px 24px; min-height: 100vh }` 顶部 0 padding 靠页面自己撑,`SchedulePage` 于是出现了 `calc(100vh - 8px)` 这种魔法数,而且 + 24px 底 padding 后文档总高 > 100vh,AI 页永远带一条页面级滚动条。建议 shell 持有高度:

```css
.app-main    { height: 100vh; overflow: auto; }          /* 滚动发生在 main,不在 body */
.app-content { max-width: 1440px; margin: 0 auto; padding: 20px 24px 24px; min-height: 100%; }
```

需要全屏的页面(AI 排班)写 `height: calc(100vh - 44px)`(20 + 24)或者让 `.app-content` 是 flex column、页面 `flex:1; min-height:0`。

**【P3】`.app-sidebar` 56px 时 icon 视觉中心偏右 1px**(内宽 40,padding 10 + 18px icon)。`padding: 0 11px` 或者 icon 容器 `width: 20px; justify-content: center`。

**【P3】`localStorage.getItem(RAIL_KEY)` 裸调用** — 和 `useTheme` 里一样包 try。抽一个 `safeStorage` 工具,两个文件共用。

---

## 4. 仪表盘

**【P0】`base.css` `@keyframes daFlash` 会在动画结束时"闪一下白"**

`from: flash-from → to: transparent`,动画结束(`fill-mode` 默认 none)后背景回到 CSS 值 `var(--dt-surface)`,而 `transparent` 时露出的是页面底 `#F6F7F9`,所以 600ms 结束瞬间有一次 `#F6F7F9 → #FFFFFF` 的跳变。另外 `.kpi-grid` 是带 gap 的透明 grid,`v-flash` 打在它上面时,高亮只出现在四张卡**之间的缝隙**里,像一张青色网格。

改成"描边环"式 flash,不碰背景,任何元素上都成立:

```css
@keyframes daFlash {
  0%   { box-shadow: 0 0 0 1px var(--dt-live), 0 0 0 4px var(--dt-live-soft); }
  100% { box-shadow: 0 0 0 1px transparent,    0 0 0 4px transparent; }
}
.da-flash { animation: daFlash 0.8s ease-out; }
```

并把 `v-flash` 从 `.kpi-grid` 移到每张 `StatCard`(给 StatCard 加 `flashKey?: unknown` prop,内部 `v-flash="flashKey"`)。

**【P1】flash 的触发条件应是"数据变了",不是"刷新了"**

现在三个面板绑同一个 `updatedAt`,点一次"刷新"三处同时闪,而数据往往没变——flash 很快就会被当成噪音忽略,真正 WS 推送来的改动反而没人看。绑内容签名:

```ts
const todaySig = computed(() => todayPersons.value.join('|'));
const weekSig  = computed(() => weekStrip.value.map(d => d.persons.join(',')).join('|'));
// <Panel v-flash="todaySig"> / <Panel v-flash="weekSig"> / <StatCard :flash-key="gapCount">
```

首帧(从空到有数据)也会触发一次,正好当"加载完成"的反馈。

**【P1】"本周"有两个定义,KPI 和条带对不上**
- `weekStart` 用 `getDay()`(周日起),`weekStrip` 用 `(getDay()+6)%7`(周一起)。周日当天,KPI 的"本周已排"在数下周,条带在显示上周。
- `gapCount` 判 `!poolByDate.has(iso)`,而 SchedulePage / 条带判 `persons.length === 0`。一条 `area_assignments` 为空的记录会让仪表盘说"无缺口"、AI 页说"有缺口"。

统一抽到 `src/utils/date.ts`:`startOfWeek(d, { weekStartsOn: 1 })`、`isWorkday(d)`、`hasDuty(entry) => personsOf(entry).length > 0`,三个页面共用。

**【P1】`today` 在 setup 里算一次就固定了** — 这是一个会开一整天(乃至几天)不关的桌面工具,过了午夜 Hero 还在显示昨天。`ScheduleCalendar.todayIso`、`SchedulePage.todayIso` 同病。做一个全局 `useToday()`:

```ts
// src/composables/useToday.ts
const today = ref(fmtDate(new Date()));
const tick = () => { const t = fmtDate(new Date()); if (t !== today.value) today.value = t; };
setInterval(tick, 60_000);
document.addEventListener('visibilitychange', () => document.visibilityState === 'visible' && tick());
export const useToday = () => today;
```

**【P1】KPI 选取:四个里有两个信息量低**

| 现在 | 问题 | 建议 |
|---|---|---|
| 本周已排 `N`(hint "本周共 7 天") | 分母含周末,而缺口只算工作日;`3/7` 让人误以为缺 4 天 | **未来 7 天覆盖** `5/5 工作日`,tone 按缺口走 |
| 本周缺口 | ✔ 最有行动价值,保留;但应可点击跳到 `/arrangement` | 给 StatCard 加 `to` prop,渲染成 `RouterLink` |
| 本月人均值班 `1.3` | 均值看不出公平性——排班工具真正关心的是**最多和最少差几次** | **本月负载差** `max − min = 2 次`,hint "最多 王五 4 次 · 最少 李四 2 次",差 ≥3 tone=warning |
| 在职人数 | 变化频率极低,占一格 KPI 有点浪费 | 换成 **上次 AI 排班** `2 小时前`(hint 指令摘要),或保留但去掉 icon(四张卡只有这张带 icon,不一致) |

**【P2】Hero 细节**
- 日期 `2026-09-04 周五` → Hero 里用 `9 月 4 日 · 周五`,ISO 留给表格。
- 周末且无人值班时显示 "今天无人值班 + 红色补排按钮" 是误报,除非你们周末也值班。加 `isWorkday(today)` 分支:周末显示 "今天休息",按钮降级为 default。
- `roster.filter(r => r.name === name && contactOf(r))` 写在模板 `v-for` 里,每次渲染做 O(n×m)。建一个 `contactByName = computed(() => new Map(...))`。
- `PageHeader` 副标题的 `StatusDot status="ok"` 永远绿,`error` 时应该变红并把文案换成"加载失败"。

**【P2】接下来 3 天:`missed` 只标第一个缺口** — 第二个缺口显示 `—`,和周末的 `—` 无法区分。所有工作日缺口都标"未排",周末标"休":

```ts
const missed = persons.length === 0 && isWorkday(cursor);
const rest   = persons.length === 0 && !isWorkday(cursor);
```

**【P2】`+3 人` / `+5` 走 `PersonChip` 会被 hash 上一个随机人员色** — 给 PersonChip 加 `neutral?: boolean`(灰底灰字,不 hash),Dashboard 两处、Calendar 一处、SchedulePage 都用它。

**【P3】`week-strip__col--past { opacity: .55 }`** — 整列透明会把里面 chip 的对比度也压到 ≈2.5:1。只降日期/星期文字颜色到 `text3`,chip 用 `filter: saturate(.55)`。

---

## 5. AI 排班页

**【P0】`SchedulePage.vue handleKeyDown`:中文输入法回车会直接发送**

`e.key === 'Enter'` 在 IME 选词确认时同样触发,用户还没打完就把半句话发出去了。这是中文用户第一次用就会踩的坑:

```ts
function handleKeyDown(e: KeyboardEvent) {
  if (e.isComposing || e.keyCode === 229) return;   // IME 组合中
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleRun(); }
}
```

**【P1】对话状态放在页面组件里,切走即丢** — 排班一次几十秒,用户很自然会切去"排班安排"看一眼再回来,回来发现对话流空了、run card 没了(而 WS 还在跑)。把 `messages / activeRun / planUpdatedAt` 提到 `src/composables/useScheduleChat.ts` 的模块级单例(和 `useTheme` 同款写法),页面只消费。顺带 `hasHistory` 可以删掉(= `messages.length > 0`)。

**【P1】run card 的步骤呈现**
1. `.ai-run__steps { max-height: 220px; overflow: auto }` 但没有内部自动滚动 → 超过约 10 步后最新一步(带光标的那条)被藏在下面,外层 `scrollToBottom` 救不了它。
2. `phase · message` 拼成一行字,阶段信息被稀释。`onProgress` 已经拿到 `p.phase`,应按 phase 分组:phase 作为小节标题(13px 半粗),下面缩进的 message 12px;同 phase 连续 message 折叠只保留最后一条 + 计数。
3. 每步有 `ts` 却没用——右对齐显示相对耗时 `+1.2s`(`text3`,tabular),既是节奏感也是排障信息。
4. 完成后 card 应折叠成一行 `✓ 排班完成 · 7 步 · 12.4s [展开]`,把结果摘要(`ai_response`)渲染在**同一张 card 的正文区**,而不是再 push 一条独立 assistant 消息——现在一次提问会生成两个 AI 气泡。
5. 失败态 card 里加 `重试` 按钮(重发最后一条 user 指令)和一个 `复制错误` 小按钮。

实现要点:running 时只渲染最后 4 步(前面的用 `显示全部 N 步` 展开),天然不需要内部滚动。

**【P1】方案面板不展示"变化"** — 这是 AI 排班页最值钱的信息,当前只是把 14 天列表重新拉一遍,用户得逐行和记忆对比。做法:

```ts
let before = new Map<string, string>();
// handleRun 开始前:
before = new Map(miniDays.value.map(d => [d.iso, d.persons.join('|')]));
// fetchPlan 之后:
const changed = computed(() => new Set(miniDays.value.filter(d => before.get(d.iso) !== d.persons.join('|')).map(d => d.iso)));
```

`ai-plan__day--changed { box-shadow: inset 2px 0 0 var(--dt-live); }`,面板副标题追加 `· 本次更新 3 天`,行内被替换的人名用 `PersonChip danger` 显示旧值加删除线(可选)。`v-flash` 也应从 `.ai-plan__body`(透明容器,Panel 盖住后什么都看不见)移到 Panel 上。

**【P2】快捷 chips**
- `handleQuick` 直接发送,用户没有机会在"排下周"后面补约束。改为**填入输入框并聚焦**,`Shift + 点击` 才直发;或 chip 右侧留一个 `↵` 小图标区分两种动作。
- "谁值班最多"是查询不是操作,和另外三个混在一起。分两组或给查询类加 `?` 前缀图标。
- 运行中 `disabled` 是对的,但 TextArea 也一并 `disabled` 了 → 用户想提前打下一句都不行。TextArea 保持可用,只锁发送。

**【P2】右栏折叠体验**
- `ai-plan__toggle` 是 `div` + click:改 `<button type="button" aria-expanded>`。
- 折叠态图标 `EditOutlined` 语义不对,用 `LayoutOutlined` / `CalendarOutlined`。
- `@media (max-width:1280px) .ai-plan { display:none }` 把 toggle 也一起藏了,窄窗口下面板**永远打不开**。窄屏改为 `PageHeader #actions` 里放"查看方案"按钮,用 `a-drawer` 呈现同一个 Panel。

**【P2】发送按钮状态** — 运行中按钮显示 loading 但仍可点(`handleRun` 内部 return)。如果 `useScheduleWebSocket` 能 cancel,运行中把按钮换成 `停止`(danger ghost);不能就 `:disabled="isRunning"`,别让用户点一个没反应的按钮。

**【P3】`import { getToken } from '@/api/http'; import { api } from '@/api/http';`** 合并成一行。`showTimeDivider(0)` 永远 true,首条消息上方那条时间线可以去掉(空状态刚消失,时间没有意义)。

---

## 6. 深色模式

**【P1】边框 `rgba(255,255,255,0.08)` 对输入框太淡** — 在 `#161920` 上等效 `#282B31`,对比 ≈1.25:1,Input/Select 看起来像没有描边。但把它整体调亮又会让面板分割线变吵。拆成两档:

```ts
// tokens.ts DARK
border:       'rgba(255,255,255,0.10)',   // 面板、分割线、表格线  → colorBorderSecondary
borderStrong: 'rgba(255,255,255,0.18)',   // 输入框、选择器、按钮描边 → colorBorder
// LIGHT
border: '#E5E7EB', borderStrong: '#D1D5DB',
```

同步 CSS `--dt-border-strong`,`--da-border-hover` 别名指向它。

**【P1】12px 文字用 `text3` 不达标(两种主题都是)**
- light `#9CA3AF` on `#FFF` ≈2.5:1;dark `#6B7280` on `#161920` ≈3.6:1。AA 小字要求 4.5:1。
- 现在用 text3 的可读文字:StatCard hint、`roster-overview__meta`、`ai-run__count`、`ai-plan__rest`、时间分隔线。
- 规则:`text3` 只给 placeholder / disabled / 纯装饰;所有需要读的 12px 用 `text2`。同时把 dark `text3` 提到 `#7B8290`(≈4.6:1),light 提到 `#8B93A1`(≈3.3:1,仍不给正文用)。

**【P1】人员色板在两个场景对比度崩**
- light 文字色用 600 档:`#CA8A04`(yellow) ≈2.9:1、`#65A30D`(lime) ≈3.1:1,12px chip 文字不可读。
- dark 头像:`hero-duty__avatar` 和 `da-person__avatar` 用 `color:#fff` 压在 400 档实底上,`#FACC15` 上的白字 ≈1.5:1。

把色板从"一个色 + color-mix"改成显式的 fg/bg/onSolid 三元组:

```ts
export const PERSON_PALETTE = [
  { fg: '#6D28D9', bg: '#EDE9FE', solid: '#7C3AED', onSolid: '#FFF' },   // violet 700/100/600
  { fg: '#BE185D', bg: '#FCE7F3', solid: '#DB2777', onSolid: '#FFF' },
  { fg: '#C2410C', bg: '#FFEDD5', solid: '#EA580C', onSolid: '#FFF' },
  { fg: '#A16207', bg: '#FEF9C3', solid: '#CA8A04', onSolid: '#1F1300' },  // yellow 用深字
  { fg: '#4D7C0F', bg: '#ECFCCB', solid: '#65A30D', onSolid: '#0F1F00' },
  { fg: '#0F766E', bg: '#CCFBF1', solid: '#0D9488', onSolid: '#FFF' },
  { fg: '#1D4ED8', bg: '#DBEAFE', solid: '#2563EB', onSolid: '#FFF' },
  { fg: '#7E22CE', bg: '#F3E8FF', solid: '#9333EA', onSolid: '#FFF' },
];
export const PERSON_PALETTE_DARK = [
  { fg: '#C4B5FD', bg: 'rgba(167,139,250,.16)', solid: '#A78BFA', onSolid: '#14102A' },
  // … 300 档做 fg,400 档做 solid,onSolid 一律深色
];
```

`personChipStyle` 输出 `--person-fg / --person-bg / --person-solid / --person-on-solid` 四个变量,PersonChip / Hero 头像改用它们,不再 `color-mix`。

**【P1】`::selection { background: var(--dt-primary-soft) }`** — dark 下 14% 透明 indigo 几乎看不见选中。单独一个 `--dt-selection`:light `#C7D2FE`,dark `rgba(129,140,248,.35)`。

**【P2】滚动条 thumb 用 `--dt-border`** — dark 下 0.08 白等于隐形。加 `--dt-scrollbar: rgba(255,255,255,.16)`(light `#D1D5DB`),hover 用 `text3`。

**【P2】缺 `color-scheme`** — 没有它,原生 `<select>` 下拉、日期控件、滚动条角落、`<input type=number>` 的 spinner 在 dark 下仍是白的。

```css
:root { color-scheme: light; }
[data-theme='dark'] { color-scheme: dark; }
```

**【P2】层次感** — `surface #161920 → surface2 #1C2028 → overlay #1F242E` 三级差在暗屏上只有 ~2% 亮度差,周末格与普通格几乎分不清。建议 `surface2 → #1D212A`,`overlay → #232834`,并给 overlay 保留阴影(dark 下"有阴影 = 临时层"这条规则反而更需要)。

**【P3】`week-strip__col--past` 的 `opacity` 在 dark 更伤**(见 §4 最后一条)。

---

## 7. 运行时风险

**【P2】`color-mix()`** — Chromium 111+,WebView2 evergreen 没问题。真正的风险是"AntD 的 JS 色 ↔ CSS 的 color-mix 色"两套算法:AntD 用 TinyColor 在 sRGB 里 alpha 叠加,Tailwind 的 `bg-primary/50` 走 `color-mix(in oklab, …)`,同一个"12% 主色底"在两边会有肉眼可辨的色偏。规则:**凡是要和 AntD 组件并排出现的浅底色,用显式 token(`primarySoft`、人员色板 `bg`),不用 color-mix;color-mix 只用于 hover/pressed 这类瞬态**。§6 的色板改造顺手解决了最大的一处。

**【P2】cssinjs 与 Tailwind utility 的优先级** — antd-vue 4 的样式是运行时追加的未分层 `<style>`,它压过 Tailwind 所有 utility(layer 内),且比你 build 出来的 CSS 更靠后。所以 `<a-button class="text-ink-2">` 不会生效,只能 `!text-ink-2` 或者 `:deep(.ant-btn)`。团队里定一条:**AntD 组件的样式只通过 token 或 `:deep()` 改,不上 utility**。别用 `@import 'tailwindcss' important;`,那会反过来把 AntD 的 hover/disabled 态全打坏。

**【P2】首帧闪白(FOUC)** — `data-theme` 由 `useTheme.ts` 的模块级 `watchEffect` 写,发生在 JS 解析之后;dark 用户每次启动都能看到一帧白。`index.html` `<head>` 最前面加:

```html
<script>
(function () {
  try {
    var m = localStorage.getItem('duty_theme_mode') || 'light';
    var d = m === 'dark' || (m === 'auto' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.dataset.theme = d ? 'dark' : 'light';
  } catch (e) {}
})();
</script>
```

另外默认值建议从 `'light'` 改为 `'auto'`——桌面内部工具,跟随系统是最不打扰的默认。

**【P2】`@theme inline` 在 Tailwind 4.2 的行为确认**
- ✔ `bg-surface` 编译为 `background-color: var(--dt-surface)`,运行时切换 `data-theme` 即生效。
- ✔ 透明度修饰符 `bg-primary/20` 编译为 `color-mix(in oklab, var(--dt-primary) 20%, transparent)`,可用。
- ✘ `var(--color-primary)`、`var(--font-sans)` 在 CSS 里**不存在**(inline 不产出变量),见 §1。
- ✘ `@theme` 必须在被 Tailwind 处理的文件顶层;不要放进 `@layer` 或 `@media`。

**【P2】`prefers-reduced-motion` 没处理** — `daPulse infinite`、`stagger-children`、blink 光标都是持续动画。

```css
@media (prefers-reduced-motion: reduce) {
  *, ::before, ::after { animation-duration: 0.01ms !important; animation-iteration-count: 1 !important; transition-duration: 0.01ms !important; }
}
```

**【P3】`:focus-visible` 全局 outline** — 和 AntD 不冲突(Button 自己的 `:focus-visible` 特异度更高,Input 用 box-shadow),可以保留。

**【P3】`ScheduleCalendar`** 几个小逻辑:
- `isWarningGap` 没排除周末,和 Dashboard/SchedulePage 的"仅工作日"不一致 → 用 §4 的 `isWorkday`。
- `hasConflict` 把"同一人排进多个区域"当冲突——如果业务允许一人多区,这个红点是误报,请和后端规则对一下;若确实是冲突,Tooltip 里应写出是谁。
- 3 个 chip(20px×3 + gap)+ 日期行 + padding ≈ 96px,`min-height: 76px` 会裁掉第三个 chip 的下半 → `min-height: 96px` 或 `CHIP_LIMIT = 2`。
- `cal__cell:hover` 的灰底会盖掉今日格的 indigo 底,加 `.cal__cell--today:hover { background: color-mix(in srgb, var(--dt-primary) 18%, var(--dt-surface)); }`(这里 color-mix 是瞬态,可用)。
- `inMonth: true` 字面类型是死字段;`cal__cell--out` 的 `startsWith` 字符串每格拼一次,提成 `monthPrefix` computed。

---

## 8. 下一步建议(按投入产出排序)

| # | 事项 | 投入 | 产出 |
|---|---|---|---|
| 1 | **§1 P0 重写 `antd.ts`**(seed token + 正确的组件 token 名) | 0.5 天 | 一半"以为生效"的定制真正生效;dark 下 placeholder/disabled/Tooltip/表格全部归到同一套灰 |
| 2 | **§4 P0 + P1 flash 改环形 + 绑内容签名** | 2 小时 | 消除白闪;flash 从噪音变成有效信号 |
| 3 | **§5 P0 IME 回车** + P1 对话状态单例 | 2 小时 | 中文用户第一天就会撞的两个坑 |
| 4 | **§4/§7 `useToday` + `utils/date.ts` 统一周起点/工作日/缺口定义** | 半天 | 三个页面的数字终于一致,跨午夜不失效 |
| 5 | **§6 人员色板 fg/bg/solid 三元组 + text3 用途收紧 + borderStrong** | 半天 | dark 模式从"能用"到"好看",对比度达标 |
| 6 | §5 P1 方案面板 diff 高亮 + run card 按 phase 分组/完成后折叠 | 1 天 | AI 页的核心价值——"AI 改了什么"——终于可见 |
| 7 | §1 P1 token 生成脚本 + 单入口 `index.css` + `legacy-aliases.css` 隔离 | 半天 | 消除双真值;`--da-*` 有了明确退场机制 |
| 8 | §3 rail 的三态主题图标、label 淡入、连接区语义修正 | 2 小时 | 打磨项,但每天都看得到 |
| 9 | §4 KPI 换成"覆盖 / 缺口 / 负载差 / 上次排班" | 半天 | 仪表盘从"统计"变成"待办" |
| 10 | `index.html` 主题预置脚本 + `color-scheme` + reduced-motion | 30 分钟 | 零成本的体验兜底 |

建议顺序:1 → 2 → 3 → 10(一天内可完成,收益最大且互不依赖),然后 4 → 5 → 6。

---

## 附:修改清单速查(按文件)

- `src/theme/tokens.ts`:`PaletteTokens` 加 `hover / borderStrong / selection / scrollbar`;人员色板改三元组;dark `surface2/overlay/text3` 微调。
- `src/theme/antd.ts`:§1 P0 整段替换;`boxShadow` 与 `boxShadowSecondary` 分开。
- `src/theme/useTheme.ts`:默认 `'auto'`;可选:运行时写 CSS 变量(方案 B)。
- `src/styles/index.css`(新):单入口;`tokens.css` 去掉 `@import 'tailwindcss'`;`base.css` 进 `layer(components)`;`legacy-aliases.css`(新)承接 `--da-*`。
- `src/styles/tokens.css`:`--dt-font-sans / --dt-border-strong / --dt-selection / --dt-scrollbar`;`@theme inline` 补 `hover / radius-control / radius-overlay / shadow-overlay`;`color-scheme`。
- `src/styles/base.css`:`daFlash` 改 box-shadow 环;`.ant-btn.ant-btn { font-weight: 500 }`;reduced-motion;滚动条改用 `--dt-scrollbar`。
- `index.html`:主题预置 script。
- `src/composables/useToday.ts`(新)、`src/utils/date.ts`(新):`startOfWeek / isWorkday / hasDuty`。
- `src/components/ui/PersonChip.vue`:`neutral` prop;改用 `--person-fg/bg/solid/on-solid`。
- `src/components/ui/StatCard.vue`:`flashKey` / `to` prop;hint 用 `text2`。
- `src/components/layout/AppLayout.vue`:连接区三处修正;三态图标;label 淡入;`<nav>` + aria;高度模型。
- `src/pages/Dashboard.vue`:签名式 flash;周起点/缺口定义统一;Hero 周末分支;`contactByName`;KPI 更换;next3Days 全标缺口。
- `src/components/schedule/ScheduleCalendar.vue`:`min-height 96`;周末不告警;today hover;`useToday`。
- `src/pages/SchedulePage.vue`:IME 守卫;状态提到 `useScheduleChat`;run card 分组/折叠/重试;方案 diff;toggle 改 button;窄屏 Drawer;TextArea 不禁用。
