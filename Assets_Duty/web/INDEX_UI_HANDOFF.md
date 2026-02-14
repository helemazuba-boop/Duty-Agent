# Duty-Agent `index.html` UI 交接说明

## 1. 文档目的
本文用于将当前 `Assets_Duty/web/index.html` 交接给熟悉 UI 的设计/前端同学，确保后续视觉升级不会破坏生产关键流程与交互语义。

## 2. 页面定位
- 页面类型：生产可用的本地排班工作台（非桥接模式）。
- 设计基线：视觉风格参考你提供的模板（卡片式布局、蓝色品牌色、简洁控件），但移除与业务无关的展示模块。
- 业务优先级：先保证“能排班、能维护数据、能导入导出、能看日志”，再做视觉精修。

## 3. 代码入口与结构锚点
- 根组件：`Assets_Duty/web/index.html:685`
- 页面头部：`Assets_Duty/web/index.html:1043`
- 主内容容器：`Assets_Duty/web/index.html:1066`
- 渲染入口：`Assets_Duty/web/index.html:1375`

## 4. 信息架构（当前生产版）
1. 调度执行：`Assets_Duty/web/index.html:1068`
2. 通知模板预览：`Assets_Duty/web/index.html:1108`
3. 核心配置：`Assets_Duty/web/index.html:1139`
4. 区域与模板：`Assets_Duty/web/index.html:1209`
5. 人员名单：`Assets_Duty/web/index.html:1243`
6. 排班预览：`Assets_Duty/web/index.html:1293`
7. 快照与监控：`Assets_Duty/web/index.html:1337`

## 5. 保留与删除策略
### 5.1 必须保留（生产关键）
- 排班指令输入、应用模式、执行按钮、执行状态反馈。
- 核心参数配置：`api_key`、`base_url`、`model`、生成天数、每区域人数、跳过周末、排班规则备注。
- 区域列表与模板列表的增删能力。
- 名单管理：新增、删除、启用开关、下一次值日与总次数统计。
- 排班预览表（动态区域列）。
- 快照导入/导出（JSON）与日志筛选（仅错误/全部）。

### 5.2 已删除（不应回流到生产页）
- 大量提醒卡片墙（行程、天气、地图、SecRandom 等展示内容）。
- 主题/背景/圆角/字体大小等“外观实验器”。
- 歌词式实时预览区、纯展示卡片、演示型虚拟数据面板。
- 宿主桥接按钮与 WebView 通道交互入口。

## 6. 视觉与组件规范（可改样式，不改语义）
### 6.1 视觉 token（当前实现）
- 背景渐变与玻璃感卡片：样式定义从 `Assets_Duty/web/index.html:11` 开始。
- 品牌主色：`--brand: #0067c0`（同文件样式段）。
- 状态色：`ok/warn/err` 分别对应绿色/琥珀/红色语义。

### 6.2 关键组件语义
- `.panel`：业务分区容器，建议保留卡片边界感。
- `.input`：统一输入控件，保持 focus 高亮反馈。
- `.btn`、`.btn-primary`、`.btn-danger`：按钮语义必须保持。
- `.table-wrap` + `table`：表格可滚动，表头 sticky。
- `.terminal`：日志区域深底高对比，不建议改为浅底。

## 7. 数据模型与状态约束
### 7.1 默认数据定义
- `DEFAULT_CONFIG`：`Assets_Duty/web/index.html:280`
- `DEFAULT_ROSTER`：`Assets_Duty/web/index.html:293`
- `DEFAULT_STATE`：`Assets_Duty/web/index.html:302`

### 7.2 归一化函数（不要删）
- `normalizeConfig`：`Assets_Duty/web/index.html:372`
- `normalizeRoster`：`Assets_Duty/web/index.html:391`
- `normalizeState`：`Assets_Duty/web/index.html:440`
- `assignment`：`Assets_Duty/web/index.html:453`

这些函数负责容错、去重、字段兼容，是页面稳定性的核心；UI 改版不可绕过。

## 8. 关键交互流程
1. 执行排班：`handleRun`，`Assets_Duty/web/index.html:798`
2. 通知预览：`previewNotice`，`Assets_Duty/web/index.html:837`
3. 区域增删：`addArea` / `delArea`，`Assets_Duty/web/index.html:843`
4. 模板增删：`addTemplate` / `delTemplate`，`Assets_Duty/web/index.html:892`
5. 学生增删：`addStudent` / `delStudent`，`Assets_Duty/web/index.html:936`
6. 快照导出：`exportSnapshot`，`Assets_Duty/web/index.html:980`
7. 快照导入：`importSnapshot`，`Assets_Duty/web/index.html:993`

## 9. 排班与统计核心逻辑（禁止视觉改版时破坏）
- 本地排班主流程：`runLocalSchedule`，`Assets_Duty/web/index.html:529`
- 名单统计（下一次值日/总次数）：`computeRosterStats`，`Assets_Duty/web/index.html:606`
- 区域动态列：`allAreaNames`，`Assets_Duty/web/index.html:644`

## 10. 设计改版边界
### 10.1 可以大胆改
- 版式比例、留白、字体系统、细节动效、图标风格、卡片视觉语言。
- 同层信息的聚合方式（例如将“通知预览”并入“调度执行”）。

### 10.2 不能改坏
- 业务字段名与语义。
- 执行路径中的校验与错误提示。
- 快照导入导出格式兼容性。
- 排班表对动态区域的渲染机制。

## 11. 推荐交接流程
1. UI 同学先按本文件理解“保留/删除边界”。
2. 先做低保真结构改版（不动函数，仅改 JSX 布局层）。
3. 再做视觉 token 和组件统一。
4. 最后逐项走回归：执行排班、名单开关、模板预览、快照导入导出、错误日志筛选。

## 12. 备注
- 当前版本明确为“本地模式（未启用桥接）”，文案位置：`Assets_Duty/web/index.html:1051`。
- 若未来恢复桥接，应单独增加“运行模式开关”，不要直接把宿主能力混入现有生产主路径。
