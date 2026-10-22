# 数据重置（班级换届 / 学期重置）

duty-agent 装在班上，班级每年换届：上一届的学生毕业或转走，名单、排班历史、欠班账本全部失去意义。
这套功能就是把旧数据清掉、让新一届从零开始，同时**不误伤**这个安装的身份与模型凭据。

设计前提：`data/` 目录里混着三类东西，删除时必须分开对待——

| 类别 | 文件 | 删除策略 |
|---|---|---|
| 班级数据 | `state.json`、`state.prev.json`、`roster.csv` | 删 |
| 混合 | `config.json`（模型/密钥 = 安装身份；`areas`/`duty_rule` = 班级规则） | 拆开：凭据留，规则可选清 |
| 安装身份 | `host-config.json`（`access_token_mode`/`static_access_token_verifier`/`server_port`/`enable_mcp`） | **永不删** |

`host-config.json` 为什么不碰：客户端的看护重启会走 `EnsureDefaultHostConfig`
（`DutyAgent.Client/BackendProcessManager.cs:435-485`）用 `TryAdd` 补缺失键。文件被删掉后，
固定端口和静态令牌会被**默认值静默抹掉**，而且当前连接正在用的 token 当场失效。它是这个安装的
身份，不是某个班级的数据。

## 三档

`Assets_Duty/state_ops.py` 的 `RESET_SCOPE_ITEMS` 是唯一真源（后端），前端
`duty-agent-ui/src/components/settings/DataResetPanel.vue` 的 `SCOPES` 是它的镜像——
确认前就要让人看见每一档删什么、留什么。改一侧必须改另一侧。

| 档位 | 删除 | 保留 | 场景 |
|---|---|---|---|
| `schedule` 清空排班 | `schedule_pool`、`debt_counts`/`credit_counts`、`last_pointer`、`absences`、`user_notes`、`day_overrides`、`next_run_note`、`roster_fingerprint`、`state.prev.json` | 花名册、`config.json` 全部、`host-config.json` | 学期中重整、账本乱了、试跑太多 |
| `handover` 换届重置 | 同上，外加 `roster.csv` | `config.json` 全部（含 api_key）、`host-config.json` | **班级换届** |
| `factory` 恢复出厂 | 同上，外加 `config.json` 打回 `_create_default_persisted_config()` | `host-config.json` | 换班级、转交他人 |

`handover` 带一个复选框"同时清空值日区域与班级规则"（`areas`/`area_per_day_counts`/`duty_rule`）。
同教室沿用**不要**勾；换教室再勾。默认不勾。

## 强制归档

删除的第一件事是归档，**不可跳过**——没有它，"删除"就是不可逆操作。

`archive_class_data()` 把 `config.json`/`state.json`/`roster.csv`/`host-config.json` 复制到
`<data_dir>/archive/<档位名>-<YYYYmmdd-HHMMSS>[-n]/`。要点：

- **先复制后删除**。`shutil.copy2` 抛异常就中止整个重置，数据原封不动。
- 同名冲突会加 `-2`、`-3` 后缀——否则同一秒内的两次重置会塌成一个目录，第二次静默覆盖第一次归档。
- 归档目录本身在删除范围之外，第二次重置不会删掉第一次的归档。
- 只保留最近 `RESET_ARCHIVE_KEEP`（10）份，超出按时间淘汰；更老的由用户自己删。
- 归档含 `host-config.json` 只为留一份完整快照，**不代表**它会参与重置。

撤销路径就是归档本身：Phase 1 **刻意不做**"一键恢复归档"。恢复是一次反向写入，风险与删除同级，
还容易和 `state.prev.json` 回滚槽的语义打架。要恢复就手工把文件拷回 `data/`（后端停止时操作）。

## `state.prev.json` 必须一起删

它保存的是重置前那一代状态。留着它，"回滚"就能在换届后静默复活上一届的排班。删掉它之后，
归档是唯一的撤销路径，语义不再有两套。

同时，重置**写空 `state.json` 而不是删文件**：`load_state()` 对缺失文件返回同样的空态，但真文件
能让 `mtime_ns` 变化，桥接的 `DutyStateCache` 靠这个去抖——删文件会把它推到 null 分支。

`roster.csv` 则**真删**：`load_roster()` 对缺失文件抛 `FileNotFoundError`，这正是首次安装的状态，
FirstRunWizard 认得这条路。

## 安全闸门

按拦截顺序：

1. **键入确认**。请求体的 `confirm` 必须与 `scope` 的中文标签（"清空排班"/"换届重置"/"恢复出厂"）
   一致，服务器端校验，不匹配返回 400。这让误触的按钮和误粘贴的 curl 都炸不掉数据。
2. **config 版本**。会写 `config.json` 的档位（`factory`、或 `handover` 勾了清规则）在**任何删除
   之前**校验 `expected_version`，过期返回 409。顺序要紧：后校验会让 409 留下一个删了一半的
   工作区。`patch_config` 在自己的锁内再校验一次作为最终防线。
3. **本进程排班锁**。`runtime.schedule_run_lock` 非阻塞获取并**持有整场重置**——不是只探测，
   否则删除途中进来一次结算就会把账本清一半。
4. **WS 单主人**。`runtime.get_duty_live_owner()` 非空即 409。排班可能从界面、MCP、CLI 或桥接发起，
   只有这条能覆盖其它入口。

2/3/4 都返回 409（`ConfigVersionConflictError` 是 `ValueError` 子类，路由统一映射）。

## 接口

```
POST /api/v1/data-reset
{
  "scope": "schedule" | "handover" | "factory",
  "clear_areas_and_rule": false,
  "expected_version": 12,
  "confirm": "换届重置"
}
→ 200 { status, scope, label, removed[], kept[], archive{name,path,files[]}, config_rewritten }

GET /api/v1/data-reset/archives
→ { data_dir, archives[{ name, path, files[], size_bytes }] }
```

## 实现落点

| 文件 | 内容 |
|---|---|
| `Assets_Duty/state_ops.py` | `RESET_SCOPE_LABELS`/`RESET_SCOPE_ITEMS`、`_reset_empty_state()`、`archive_class_data()`、`list_reset_archives()`、`reset_class_data()` |
| `Assets_Duty/models/schemas.py` | `DutyDataResetRequest` |
| `Assets_Duty/application/command_service.py` | `CommandService.reset_data()`（持锁 + 拒绝闸门 + 广播） |
| `Assets_Duty/application/query_service.py` | `QueryService.get_reset_archives()` |
| `Assets_Duty/routers/config.py` | `POST /api/v1/data-reset`、`GET /api/v1/data-reset/archives` |
| `duty-agent-ui/src/api/http.ts` | `resetData()` / `getResetArchives()` + 类型 |
| `duty-agent-ui/src/components/settings/DataResetPanel.vue` | 数据管理面板 + 确认弹窗 |
| `duty-agent-ui/src/pages/SettingsPage.vue` | `system` tab 挂载面板 |
| `Assets_Duty/test_data_reset.py` | 21 条回归 |

重置完成后广播 `snapshot_changed`（reason `data-reset`），桥接的 `DutyStateCache` 自动重拉，
组件与"今日值日匹配"规则随之更新——**桥接插件零改动**，它的 `Settings.json` 只是连接偏好，
与班级数据无关。

前端在 `handover`/`factory` 成功后清掉 `localStorage` 的 `duty_first_run_dismissed`
（`Dashboard.vue:62`），否则会停在空仪表盘而不是重新引导录入名单。

## 明确不做

- **不给 MCP 加 reset 工具**。现有 5 个工具已经能改配置换名单，再给一个删除入口等于让 LLM
  一键清空班级数据。这个功能只走人类点击的 UI/API。
- **不做多届存档切换**。真正的"多班级 profile"要引入数据集注册表和切换时重启后端，工作量高一个
  数量级。归档文件夹已经能满足"翻去年记录"。
- **C# 侧零改动**。Python 是 `state.json`/`roster.csv` 的唯一写权威，删除必须落在 Python。

## 回归

```bash
Assets_Duty/python-embed/python.exe test_data_reset.py
```

注意 `python-embed` 是隔离解释器（`python311._pth` 固定了 `sys.path`，不含脚本目录），
手工跑需要先 `sys.path.insert(0, <Assets_Duty>)`；CI 用真 Python 3.11 逐文件独立进程跑，不受影响。
