# Duty-Agent UI 重绘方案

> 来源:与 claude-fable-5.1-high(arena.ai 直连)的咨询对话,2026-09-04。
> 提问背景:Vue 3 + TS + Vite + Ant Design Vue 4 + Tailwind 4,五个页面(仪表盘/排班安排/花名册/AI 排班/设置),现状为 AntD 默认后台风格,单管理员、桌面端、重实时状态。

下面按你要的 5 个部分给方案。先说一句总的判断:这类"单管理员、桌面、重实时状态"的内部工具,重绘的核心不是"更好看",而是把**注意力管理**做对——底色和容器要退后,状态和变化要突出。所有方向都围绕这一点。

---

## 一、三个设计方向

### 方向 A:Calm Ops(推荐)

**关键词**:中性冷灰、低饱和、边框优先、紧凑、"Linear / Vercel Dashboard"气质

| 角色 | 取值 | 说明 |
|---|---|---|
| 主色 Primary | `#4F46E5`(indigo-600) | 只用于主按钮、选中态、链接;不用于大面积 |
| 主色浅底 | `#EEF2FF` | 选中行/菜单/标签底 |
| 页面底 | `#F6F7F9` | 比 AntD 默认 #f5f5f5 略冷 |
| 容器 | `#FFFFFF` | 卡片、表格 |
| 次级容器 | `#F9FAFB` | 卡内分区、表头、代码块 |
| 边框 | `#E5E7EB` / 次级 `#F0F1F3` | 用边框替代阴影分层 |
| 文字 | 主 `#111827` / 次 `#6B7280` / 弱 `#9CA3AF` | |
| 成功 | `#16A34A` | 已连接、已排班 |
| 警告 | `#D97706` | 未排/待确认 |
| 危险 | `#DC2626` | 冲突、断连 |
| 信息/实时 | `#0891B2`(cyan-600) | 实时刷新、WS 活动、AI 工具调用中——**单独给"实时"一个色**,与主色区分 |
| 人员色板 | 8 色低饱和:`#7C3AED #DB2777 #EA580C #CA8A04 #65A30D #0D9488 #2563EB #9333EA` | 按姓名 hash 分配,日历与花名册头像统一 |

**字体**:`Inter, "PingFang SC", "Microsoft YaHei", system-ui, sans-serif`;数字全局 `font-variant-numeric: tabular-nums`(排班表、统计对齐很关键)。
**字号阶梯**(px / line-height):12/16 辅助 · 13/20 表格正文 · 14/22 正文 · 16/24 卡片标题 · 20/28 页面标题 · 28/36 大数字。
**圆角**:控件 6,卡片 10,弹层 12,pill 999。
**阴影**:卡片 0 阴影 + 1px 边框;只有悬浮层(Dropdown/Popover/Modal)有阴影 `0 8px 24px rgba(0,0,0,.08)`。
**密度**:控件高 32,表格 cell padding 8/12,卡片 padding 20。

### 方向 B:Warm Editorial

**关键词**:暖纸色、墨绿主色、稍大留白、"Notion / Stripe Docs"

- 主色 `#0F766E`(teal-700),页面底 `#FAF9F6`,容器 `#FFFFFF`,边框 `#E7E5E4`,文字 `#1C1917`/`#78716C`
- 警告 `#B45309`、危险 `#B91C1C`、成功复用主色系 `#15803D`
- 字体同上但标题可用 `font-weight: 600 + letter-spacing: -0.01em`,正文 14,标题 22
- 圆角 8/12/16,卡片有极轻阴影 `0 1px 2px rgba(0,0,0,.04)`,密度中等(控件 36、cell padding 12/16)

优点:亲和、少"后台味";缺点:暖底会削弱状态色对比,表格密度低,对"重实时、看表格"的场景不占优。

### 方向 C:Dark Console

**关键词**:深色为主、青绿荧光强调、监控台

- 页面底 `#0B0D12`,容器 `#12151C`,浮层 `#1A1E27`,边框 `rgba(255,255,255,.08)`
- 主色 `#22D3EE`(cyan-400),文字 `#E5E7EB`/`#9CA3AF`
- 圆角 6/8,无阴影靠明度分层,密度高(控件 30、cell padding 6/10),等宽数字更突出

优点:与"实时状态"气质最搭;缺点:单管理员白天长时间录入/查看表单,全深色易疲劳;AntD 深色算法下 Calendar / Table / Form 的细节需大量调,且 AI 对话页大量文本在深底上可读性更难控。

### 推荐:A,并把 C 作为深色模式吸收

理由:
1. 你的问题本质是"层次弱、引导差"——A 用中性底 + 边框分层 + 单独的"实时色",最直接解决注意力分配;
2. AntD 组件在浅色中性主题下最成熟,改造成本最低;Tailwind 4 默认 gray/indigo 色阶可以直接对上,不用自造色;
3. 深色可作为 A 的 token 翻转版实现(见第五部分),比一开始押注 C 更稳。

---

## 二、推荐方向下的整体布局

**导航形态**:左侧窄侧栏(icon rail),5 个页面不需要 240px 的宽菜单。

- 收起 56px(只图标 + tooltip),展开 200px;默认收起,hover 或固定按钮展开
- 侧栏背景用 `#FFFFFF` + 右边框,**不再用深色侧栏**(深色侧栏是"后台味"的最大来源);如果想保留一点识别度,可用 `#F6F7F9` 与内容区同色,靠边框区分
- 侧栏底部固定:**连接状态**(HTTP/WS 双点 + 服务地址缩略)+ 主题切换 + 设置入口。连接状态在这里比在 header 更合适:常驻但不抢眼,断线时变红并可点击跳设置

**顶部**:取消全局 Header。每页自带 `PageHeader`(标题 20px + 副标题/摘要 13px + 右侧操作区),高度 ~56,sticky。实时相关的"最后更新 12 秒前 · ● 已连接"放在 PageHeader 右侧、操作按钮左边。

**内容区栅格**:
- 内边距 24,`max-width: 1440px` 居中(超宽屏不撑满)
- 用 Tailwind `grid grid-cols-12 gap-4`,卡片按 col-span 布局;AntD `Row/Col` 只在表单内使用
- 断点只考虑 ≥1280 和 1024~1280 两档(桌面内部工具)

**卡片层级(三层,不超过三层)**:
1. 页面底 `#F6F7F9`
2. 面板 Panel:白底 + 1px `#E5E7EB` + 圆角 10 + 无阴影;标题 16/600 + 可选右侧操作
3. 面板内分区:`#F9FAFB` 底 + 圆角 6,或用分隔线;**不允许卡中套卡**
- 悬浮层(Drawer/Modal/Popover)才用阴影,这样"有阴影 = 临时层"的语义很清晰

---

## 三、各页面改造要点

### 1. 仪表盘(重点)

现状问题是 4 个等权统计卡 + 一个列表,没有主次。改为:

**顶部 Hero 行(col-span-12,分两块)**
- 左 8 列:**"今日值班"大卡**——这是管理员最常看的信息。大头像(hash 色)+ 姓名 24px + 联系方式可一键复制 + 备注 + 值班时段。多人值班时横排最多 3 个,其余 `+N`。空状态:「今天无人值班」+ 「去排班」按钮(警告色边框而非普通空态)
- 右 4 列:**接下来 3 天**的迷你列表(日期 · 星期 · 人名 chip),今天之后第一个未排的日期用警告色标出

**KPI 行(4 个 col-span-3)**
- 去掉 AntD `Statistic` 默认样式;自绘:标签 13px 次级色 → 数字 28px tabular → 底部一行 12px 对比信息("较上周 +2" / "覆盖率 86%")
- 建议指标:本周已排 / 本周缺口(警告色,为 0 时变成功色)/ 本月人均值班 / 待确认的 AI 方案(有则可点击跳转)
- KPI 卡不加 icon 大色块(那是 AntD 模板味的另一来源),最多标签前一个 14px 线性图标

**中部**
- 左 8 列:**本周条带**——7 列横向,每列一天,内部人名 chip,今日列底色 `#EEF2FF` + 顶部 2px 主色条;周末列底色 `#F9FAFB`;点击某天打开 Drawer 编辑(复用排班页的 Drawer)
- 右 4 列:**动态流**——自绘 feed(不用 `Timeline`),每条:来源图标(手动 / AI / 系统)+ 一句话 + 相对时间;AI 产生的动作带 cyan 小标签

**实时表现**
- 数据经 WS 更新时,对应卡片/行触发一次 `flash` 动画(底色从 `#ECFEFF` 渐回白,600ms);用一个 `v-flash="updatedAt"` 指令即可覆盖全站
- PageHeader 右侧的"已连接"绿点在收到消息时轻微脉冲一次

### 2. 排班安排

- 日历/表格切换用 `Segmented` 放 PageHeader 右侧,与"新建排班"主按钮并列;切换状态记入 query,刷新不丢
- **日历**:用 `dateCellRender` 完全接管格子——去掉 AntD 默认的日期大数字 + 徽标风格;格子左上小号日期、内部人名 chip(hash 色浅底 + 深字),未排的过去日期不管,未排的未来 7 天内格子加虚线警告边;今日格子 `#EEF2FF` 底;周末列 `#F9FAFB`;冲突(同人连排/超频)在 chip 上加红点
- **表格**:按周分组表头(`rowClassName` 做周分隔线),列:日期/星期 · 值班人(头像+名)· 时段 · 来源 · 备注 · 操作;hover 显示操作,不常驻;开启 `size="middle"`
- **编辑**:Modal 改右侧 `Drawer`(宽 420),保留日历上下文;Drawer 内表单单列、label 在上;值班人选择器渲染头像 + 最近值班日期(辅助公平判断)
- 日历上支持拖拽调换是加分项,不在本次范围也可以,先留 hook

### 3. 花名册

- 表格 + 右侧 Drawer 编辑(与排班页一致的交互语言)
- 首列头像(hash 色,字母/姓)+ 姓名 + 备注灰字第二行;联系方式列带复制按钮
- 新增两列:**本月值班次数、上次值班日期**——花名册的核心价值之一是看公平性,这两列比"备注"重要
- 空状态是首次运行的重要入口:「还没有成员」+ 「添加第一位成员」,并提示可以让 AI 排班

### 4. AI 排班(重点)

改为**两栏 + 底部固定输入区**,把"对话"和"结果"分开:

**左栏 对话流(flex-1,内容 max-width 720 居中)**
- 用户消息:右对齐、`#EEF2FF` 底、圆角 12、无头像
- AI 消息:左侧不做气泡,直接贴底渲染 Markdown 文本(带气泡的 AI 消息看起来像客服),左侧 24px 小 logo 图标
- **工具调用卡**:AI 消息内嵌的可折叠条目,一行内含:状态图标(spinner cyan / check 绿 / x 红)· 工具名等宽字体 `get_roster` · 简短参数摘要 · 耗时 `1.2s` · 展开箭头;展开后显示入参/出参 JSON(`#F9FAFB` 底、12px 等宽)。连续多个工具调用垂直串成一组,组外有 1px 边框
- 流式输出:末尾闪烁光标;生成中时输入框变"停止"按钮
- 时间戳仅在两条消息间隔 >5 分钟时显示一次分隔

**右栏 方案面板(宽 380,可折叠)**
- AI 生成排班方案后,这里渲染**迷你周/月视图**(复用日历 chip 组件),与当前生效排班的差异用颜色标出(新增绿边、变更黄边、删除红划线)
- 底部固定两个按钮:「应用方案」主按钮 / 「丢弃」;应用后面板显示"已应用 · 12 条",可撤销
- 没有方案时显示:当前排班摘要 + 缺口提示,作为让用户发起对话的引子

**底部输入区**
- sticky,白底上边框;Textarea 自增高,最大 6 行;右侧发送按钮
- 上方一排快捷 chips:「排下周」「补齐本月缺口」「按公平分配」「谁值班最多」——内部工具最有效的引导方式
- 输入区左上角显示模型名 + MCP 工具服务器状态点,异常时红点并可点跳设置

**PageHeader**:标题「AI 排班」+ 右侧「新对话」「历史」两个次要按钮

### 5. 设置

- 左侧锚点导航(`Anchor`)+ 右侧分组 Panel:连接(服务地址、令牌)· AI / MCP 工具服务器 · 外观(主题)· 关于
- 每个字段下有一行 12px 说明;令牌用 `Input.Password`;服务地址旁「测试连接」按钮带三态(测试中/成功+延迟/失败+原因)
- MCP 服务器列表用卡片列表而非表格:名称、地址、状态点、工具数量、启用开关
- 保存策略:每个 Panel 独立保存 + 顶部有"未保存更改"提示条;不要一个页面底一个大保存
- **首次运行向导**:独立路由 `/setup`,全屏居中 560px 宽卡片,`Steps` 三步(连接 → 添加成员或跳过 → 配置 AI 或跳过),完成后落到仪表盘并高亮"去 AI 排班"

---

## 四、落地方式

### 结构:单一 token 源 → 三处消费

```
src/theme/
  tokens.ts        # 设计 token 的 TS 对象(light / dark 两份)
  antd.ts          # 把 tokens 映射成 ConfigProvider 的 theme 对象
  useTheme.ts      # 切换主题:写 CSS 变量到 :root + 返回 antd theme
src/styles/
  tokens.css       # @theme inline 把 CSS 变量暴露给 Tailwind
  base.css         # 全局:字体、tabular-nums、flash 动画、滚动条
src/components/ui/
  PageHeader.vue / Panel.vue / StatCard.vue / StatusDot.vue / PersonChip.vue
```

**为什么以 TS 为源**:AntD Vue 4.x 的 ConfigProvider 需要 JS 值(不像 antd React 5.12+ 有 cssVar 模式,antdv 这块支持不稳),所以 TS 对象是最稳的单一来源;`useTheme` 在切换时同时 `setProperty` 到 `:root` 供 Tailwind 和自定义组件使用。

### ConfigProvider 关键 token(light)

```ts
token: {
  colorPrimary: '#4F46E5',
  colorInfo: '#0891B2',
  colorSuccess: '#16A34A', colorWarning: '#D97706', colorError: '#DC2626',
  colorBgLayout: '#F6F7F9', colorBgContainer: '#FFFFFF', colorFillAlter: '#F9FAFB',
  colorBorder: '#E5E7EB', colorBorderSecondary: '#F0F1F3',
  colorText: '#111827', colorTextSecondary: '#6B7280', colorTextTertiary: '#9CA3AF',
  fontFamily: 'Inter, "PingFang SC", "Microsoft YaHei", system-ui, sans-serif',
  fontSize: 14, fontSizeHeading4: 20, fontSizeHeading5: 16,
  borderRadius: 6, borderRadiusLG: 10, borderRadiusSM: 4,
  controlHeight: 32, lineWidth: 1,
  boxShadow: '0 8px 24px rgba(0,0,0,.08)', boxShadowSecondary: '0 8px 24px rgba(0,0,0,.08)',
  motionDurationMid: '0.15s',
},
components: {
  Layout: { siderBg: '#FFFFFF', bodyBg: '#F6F7F9', headerBg: '#FFFFFF' },
  Menu: { itemBg: 'transparent', itemSelectedBg: '#EEF2FF', itemSelectedColor: '#4F46E5',
          itemHoverBg: '#F3F4F6', itemBorderRadius: 6, itemMarginInline: 8, iconSize: 18 },
  Card: { paddingLG: 20, headerBg: 'transparent', headerFontSize: 16, borderRadiusLG: 10 },
  Table: { headerBg: '#F9FAFB', headerColor: '#6B7280', headerSplitColor: 'transparent',
           rowHoverBg: '#F9FAFB', cellPaddingBlock: 8, cellPaddingInline: 12, cellFontSize: 13,
           headerBorderRadius: 0 },
  Button: { primaryShadow: 'none', defaultShadow: 'none', fontWeight: 500 },
  Segmented: { itemSelectedBg: '#FFFFFF', trackBg: '#F3F4F6' },
  Drawer: { paddingLG: 20 },
  Modal: { borderRadiusLG: 12 },
  Form: { labelColor: '#374151', verticalLabelPadding: '0 0 6px', itemMarginBottom: 20 },
  Tag: { borderRadiusSM: 4 },
  Calendar: { fullBg: '#FFFFFF', fullPanelBg: '#FFFFFF', itemActiveBg: '#EEF2FF' },
}
```

**去"后台味"最见效的几个点**:`Layout.siderBg` 白色、`Button.primaryShadow: 'none'`、`Table.headerBg` 浅灰 + `headerSplitColor` 透明、`Menu` 圆角选中态。这几个改完,不动业务代码视觉就变了一半。

### CSS 变量 + Tailwind 4

`tokens.css`:

```css
:root {
  --dt-primary: #4F46E5; --dt-primary-soft: #EEF2FF;
  --dt-bg: #F6F7F9; --dt-surface: #FFFFFF; --dt-surface-2: #F9FAFB;
  --dt-border: #E5E7EB; --dt-border-2: #F0F1F3;
  --dt-text: #111827; --dt-text-2: #6B7280; --dt-text-3: #9CA3AF;
  --dt-live: #0891B2; --dt-live-soft: #ECFEFF;
  --dt-radius: 6px; --dt-radius-lg: 10px;
}
@theme inline {
  --color-primary: var(--dt-primary);
  --color-surface: var(--dt-surface);
  --color-surface-2: var(--dt-surface-2);
  --color-line: var(--dt-border);
  --color-live: var(--dt-live);
  --radius-panel: var(--dt-radius-lg);
  /* ... */
}
```

注意用 `@theme inline`,这样切换主题时改 `:root` 上的 `--dt-*`,Tailwind 生成的 `bg-surface` 等类会跟着变(非 inline 会把值编译死)。

**Tailwind 与 AntD 的共存**:Tailwind 4 所有样式都在 cascade layer 里,AntD 的 CSS-in-JS 是无 layer 样式,天然优先级更高,冲突比 v3 时代少很多。剩余需要注意的只有 preflight 对 `button`/`img` 的 reset,实测基本不影响 AntD;若有个别问题,用 `@layer base` 局部补回即可,不要关掉 preflight。

**分工原则**:AntD 组件内部外观 → 全部走 token;页面布局、间距、自定义组件 → Tailwind;跨框架的颜色语义 → `--dt-*` 变量。禁止在业务组件里写 hex。

### 业务代码怎么少动

- 新增 5 个基础组件(`Panel` 替代裸 `a-card`、`PageHeader`、`StatCard`、`StatusDot`、`PersonChip`),页面里做替换,不改数据流
- 排班/花名册的 Modal → Drawer:只换外壳标签和 props,表单内容不动
- `v-flash` 指令 + `usePersonColor(name)` 工具函数,两个文件覆盖全站的实时高亮和人员配色
- AI 页是唯一需要重写模板的页面,但 store/WS 逻辑不动,只重排消息渲染层(拆出 `MessageItem` / `ToolCallCard` / `PlanPanel`)

---

## 五、深色模式

**值得做,但放第二阶段**,理由:单管理员工具,如果存在夜间盯排班/值班的场景,深色就是刚需;同时你已经把 token 集中了,深色的边际成本很低。第一阶段先把 token 架构搭好,第二阶段一到两天能出。

**实现**:`ConfigProvider theme.algorithm = theme.darkAlgorithm` + 覆盖背景/边框/主色 token,同时切换 `:root[data-theme=dark]` 下的 `--dt-*`。跟随系统 + 手动三态,存 localStorage。

**配色策略**:

| 角色 | 深色取值 | 原则 |
|---|---|---|
| 页面底 | `#0F1115` | 不用纯黑 |
| 容器 | `#161920` | 比页面底亮一档 |
| 次级容器 / 表头 | `#1C2028` | 再亮一档;深色靠明度分层而非阴影 |
| 浮层(Drawer/Modal/Dropdown) | `#1F242E` | 最亮,可加 `0 8px 24px rgba(0,0,0,.4)` |
| 边框 | `rgba(255,255,255,.08)` / 次级 `.05` | 半透明边框在各层上都自然 |
| 主色 | `#818CF8`(indigo-400) | 深底上主色要**提亮一档**,600 会发闷 |
| 主色浅底 | `rgba(129,140,248,.14)` | 用透明度而非固定色 |
| 实时色 | `#22D3EE` | 深色下正好接上方向 C 的气质 |
| 成功/警告/危险 | `#4ADE80 / #FBBF24 / #F87171` | 一律提亮到 400 档,且用于文字/点;大面积底用 `.12` 透明度 |
| 文字 | 主 `#E6E8EB` / 次 `#9AA0A6` / 弱 `#6B7280` | 主文字不用纯白 |
| 人员色板 | 同一组 hue,明度提到 400 档 | chip 底用 `.18` 透明度 |

**几个容易翻车的点**:
- `flash` 动画的高亮色在深色下改成 `rgba(34,211,238,.15)`,不要沿用浅色的 `#ECFEFF`
- AntD Calendar 全屏模式在深色算法下格子边框会消失,需手动给 `Calendar.fullPanelBg` 和格子边框补值
- 工具调用卡的 JSON 区域用 `#0F1115`(比容器更暗,形成"内嵌"感),语法高亮色也要换一套
- 图表/条带的周末底色改为 `rgba(255,255,255,.03)`,否则看不出

---

如果只能先做一件事:把第四部分的 ConfigProvider token 和白色侧栏上了,再补 `PageHeader` + `Panel` 两个组件,一天内就能让整体气质脱离默认模板;然后按仪表盘 → AI 页 → 排班页的顺序重做布局。
