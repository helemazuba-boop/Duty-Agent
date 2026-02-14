# Duty-Agent 功能分层清单（普通用户层 / 调试层）

适用范围：
- `Assets_Duty/web/test.html`
- `Views/SettingPages/DutyWebSettingsPage.axaml.cs`
- `Services/DutyLocalPreviewHostedService.cs`

目标用户：
- 发布后使用者有一定计算机基础，但无开发基础。

## 1. 全量功能盘点

### 1.1 排班执行
- 执行排班（本地模拟或宿主桥接）  
  代码：`Assets_Duty/web/test.html:1573`
- 应用模式（`append` / `replace_all` / `replace_future` / `replace_overlap`）  
  代码：`Assets_Duty/web/test.html:164`
- 宿主执行进度与结果回传（`run_status` / `run_result`）  
  代码：`Assets_Duty/web/test.html:1529`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:241`
- 本地 REST 覆盖排班（`POST /api/v1/schedule/overwrite`）  
  代码：`Assets_Duty/web/test.html:1149`, `Services/DutyLocalPreviewHostedService.cs:775`

### 1.2 配置管理
- 配置读写（`load_all` / `save_config`）  
  代码：`Assets_Duty/web/test.html:1575`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:150`
- 核心参数：`api_key`、`base_url`、`model`、`python_path`、自动排班、跳过周末、规则等  
  代码：`Assets_Duty/web/test.html:267`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:389`
- MCP 开关（`enable_mcp`）  
  代码：`Assets_Duty/web/test.html:292`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:398`

### 1.3 名单与区域
- 名单读写（`save_roster`）  
  代码：`Assets_Duty/web/test.html:1603`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:199`
- 新增/删除学生，自动分配 ID，同名自动重命名  
  代码：`Assets_Duty/web/test.html:1424`
- 区域新增/删除、区域单独人数  
  代码：`Assets_Duty/web/test.html:1340`, `Assets_Duty/web/test.html:295`
- 通知模板新增/删除与渲染  
  代码：`Assets_Duty/web/test.html:1385`, `Assets_Duty/web/test.html:1227`

### 1.4 预览与监控
- 动态区域排班预览表  
  代码：`Assets_Duty/web/test.html:925`
- 固定双区域（教室/清洁区）兼容预览  
  代码：`Assets_Duty/web/test.html:944`
- 日志面板、错误过滤、清空日志  
  代码：`Assets_Duty/web/test.html:698`, `Assets_Duty/web/test.html:717`
- 快照导入/导出/重置（JSON）  
  代码：`Assets_Duty/web/test.html:1463`, `Assets_Duty/web/test.html:1472`, `Assets_Duty/web/test.html:1500`

### 1.5 通知能力
- 本地通知模板预览  
  代码：`Assets_Duty/web/test.html:1624`
- 发送宿主通知（`publish_notification`）  
  代码：`Assets_Duty/web/test.html:1260`, `Views/SettingPages/DutyWebSettingsPage.axaml.cs:307`
- 触发“排班完成通知”与“值日提醒通知”  
  代码：`Assets_Duty/web/test.html:1267`, `Assets_Duty/web/test.html:1283`

### 1.6 桥接与接口（集成能力）
- WebView 桥接动作：  
  `ready` / `load_all` / `save_config` / `save_roster` / `run_core` / `publish_notification` / `trigger_run_completion_notification` / `trigger_duty_reminder_notification` / `open_test_in_browser`  
  代码：`Views/SettingPages/DutyWebSettingsPage.axaml.cs:147`
- MCP Endpoint：`GET /mcp`（SSE）、`POST /mcp`（JSON-RPC）  
  代码：`Services/DutyLocalPreviewHostedService.cs:219`
- MCP Tools：`schedule_overwrite`、`roster_import_students`、`config_update_settings`  
  代码：`Services/DutyLocalPreviewHostedService.cs:16`, `Services/DutyLocalPreviewHostedService.cs:1468`
- MCP 导入“立即应用无需确认”：  
  `roster_import_students`、`config_update_settings` 返回 `applied_immediately = true`  
  代码：`Services/DutyLocalPreviewHostedService.cs:925`, `Services/DutyLocalPreviewHostedService.cs:1140`

## 2. 分层结果

说明：
- 普通用户层：默认可见，可直接完成日常使用。
- 调试层：默认隐藏，仅在“高级/诊断模式”开启后可见。

| 功能 | 建议层级 | 处理建议 |
|---|---|---|
| 执行排班（输入指令 + 开始） | 普通用户层 | 保留主入口 |
| 应用模式 | 普通用户层 | 保留，但默认只展示推荐项；其余放“高级”折叠 |
| 运行进度/结果提示 | 普通用户层 | 保留 |
| 核心配置（API Key、自动排班、生成天数、每区人数、跳过周末、规则） | 普通用户层 | 保留 |
| 区域管理（增删 + 每区人数） | 普通用户层 | 保留 |
| 名单管理（增删、导入、导出） | 普通用户层 | 保留 |
| 动态排班预览 | 普通用户层 | 保留 |
| 快照导入导出 | 普通用户层 | 保留为“备份与恢复” |
| 基础日志（仅状态/错误） | 普通用户层 | 保留简化版 |
| MCP 开关（启用/禁用） | 普通用户层 | 保留，附风险提示 |
| MCP 导入配置/名单（立即应用） | 普通用户层 | 保留；文案明确“导入即生效，无确认框” |
| 运行模式切换（mock/bridge） | 调试层 | 隐藏 |
| `load_all`/`save_config`/`save_roster` 独立按钮 | 调试层 | 隐藏，普通层改为自动保存或单一“保存全部” |
| 本地接口覆盖排班面板（API URL/原始响应） | 调试层 | 隐藏 |
| 手动触发通知按钮（完成/提醒） | 调试层 | 隐藏 |
| `open_test_in_browser` | 调试层 | 隐藏 |
| 固定双区域兼容预览 | 调试层 | 隐藏（仅回归验证） |
| 完整终端日志与 payload 细节 | 调试层 | 隐藏 |
| 错误日志过滤开关 | 调试层 | 隐藏（普通层仅展示最近错误） |

## 3. WPF UI 落地建议（按层）

普通用户层页面建议：
1. 排班执行
2. 配置
3. 名单
4. 预览
5. 备份与恢复

调试层页面建议（单独“高级/诊断”入口）：
1. 桥接测试
2. REST/MCP 接口测试
3. 通知触发测试
4. 原始日志与 payload

## 4. 关键产品约束

- “导入配置 / 导入名单”通过 MCP 传参后立即应用，不弹确认框。  
  代码：`Services/DutyLocalPreviewHostedService.cs:919`, `Services/DutyLocalPreviewHostedService.cs:1134`
- 普通用户层不暴露开发术语（如 JSON-RPC、SSE、payload、apply_mode 内部枚举）。
- 调试层必须明显标注“仅用于诊断，误操作可能覆盖数据”。
