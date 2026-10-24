# Duty-Agent 上架合规审计（2026-10-23）

对照 `Microsoft Store Policies v7.20` 与提交方给的检查单，逐条核对代码/包体证据。
**凡是没跑过的都标"未验证"，不拿"代码看起来对"当通过。**

- 分支：`independent-agent`
- 包体：`.release\HelemaZuba.Duty-Agent_1.0.0.0_x64.msix`（108 MB，**未签名**，`-ForStore` 产物）
- 静态认证（对**包内**清单跑，`-EnforceStorePublisher`）：**40 PASS / 0 FAIL / 1 WARN**
- 客户端单测：**52/52**；后端单测：26 个文件全绿，`test_cli.py` 1 例失败（环境原因，见 §5）

---

## 1. 本次修掉的问题

| # | 问题 | 位置 | 对应检查项 |
|---|---|---|---|
| 1 | **API Key 明文落盘**。`dpapi_compat` 的注释声称"由 C# 加密写入"，但全仓库没有任何 `CryptProtectData`/`ProtectedData` 调用，`config.json` 里一直是明文 | `state_ops._protect_plan_api_keys`（新增）、`_hydrate_runtime_config`、`save_config`、`patch_config`；`dpapi_compat.py` 注释纠正 | P0-3 凭据安全存储 |
| 2 | **版本面三处漂移**：清单 `1.0.0.0` / `version.py` `0.50.0` / 桥接 `0.50.0` / README `v0.51.8 Beta`；`cli.py` 还内联了一份 `APP_VERSION` | 四处统一为 `1.0.0`；`cli.py` 改为 `from version import APP_VERSION` | P0-1 版本递增、§9 提交 |
| 3 | **WebView2 缺失时只有"启动失败"**，用户不知道要装什么，审核员也拿不到可判定信息 | 新增 `DutyAgent.Client/WebViewRuntime.cs`；启动先探测并把版本写日志；缺失时弹带官方下载地址的提示，**后台继续运行** | P0-2 WebView2 兼容处理 |
| 4 | **开机自启默认开启**（装完即写登录项） | 后端默认改 `False`（`state_ops` / `query_service`）、客户端兜底改 `False`、设置页默认改 `False`；首次向导新增显式勾选项（默认勾选但可见可取消） | P0-5 自启需明确同意 |
| 5 | **包内夹带开发产物**：30 个 `test_*.py`、`_test_tool_call.py`、`Assets_Duty\logs\duty-backend-20260912.log`（开发机日志） | `New-DutyAgentClientRelease.ps1` 增加 `logs` 排除 + 递归删除 `test_*.py`/`_test_*.py`；实测包内已为 0 | P0-2 无测试密钥/无关文件 |
| 6 | **隐私政策在客户端内无处可看** | 设置页新增「关于」页：引擎版本、数据位置、AI 生成内容说明、隐私政策/使用说明/反馈/联系作者四个入口；宿主新增 `open-external`（白名单 http/https/mailto） | P0-3 界面可查看隐私政策 |
| 7 | **AI 调用前没有数据披露** | 首次向导第 2 步加常驻说明：发送的是**人员编号而非姓名**、日期、区域与指令；不想联网可用离线算法 | P0-3/P0-4 告知与披露 |
| 8 | **WebView 可被导航到任意外部站点**（页面持有宿主消息通道） | `NavigationStarting` 白名单：只放行回环 http 与 `about:`，其余取消并记日志；`LocalNavigationTests` 11 例钉住 | P0-5 不引入任意脚本执行 |
| 9 | 无模型用户/审核员走不完首次向导 | 向导第 2 步加「使用离线算法（无需 API Key，不联网）」一键切换 | P1-7 无 Key 演示路径 |

配套文档：`PRIVACY.md` 同步更新（DPAPI 加密、自启默认关闭、数据重置入口、AI 可能出错与反馈渠道），`README.md` 商店章节更新（含"触摸适配未落地"的更正）。

---

## 2. 逐条对照

### P0-1 开发者账户与应用身份

| 项 | 状态 | 证据 |
|---|---|---|
| 账户有效、身份验证完成 | **待你确认** | 只能看到 `Publisher="CN=B4407923-…"` 已是真实发布者 ID，账户状态无法从代码判定 |
| 已预留应用名称 | **待你确认** | 同上 |
| `Identity.Name` = `HelemaZuba.Duty-Agent` | ✅ | 清单第 24 行 |
| `Publisher` = `CN=B4407923-BF46-468D-B8F8-549EDD96BDC3` | ✅ | 清单第 25 行 |
| 与 Partner Center 逐字一致 | **待你核对** | 静态检查只能确认"形如商店 ID"，一致性必须比对后台 Product identity 页 |
| 四段式版本、后续递增 | ✅ | `1.0.0.0`，认证脚本 PASS；四处版本面已统一 |
| 发布者/支持方式/著作权归属 | ⚠️ | 支持邮箱与仓库已定（`helemazuba@gmail.com`）；**软件著作权归属需你书面确认**，商店列表的 Copyright 字段要填 |

### P0-2 MSIX 安装与系统兼容性

| 项 | 状态 | 证据 |
|---|---|---|
| 打包脚本 / 静态认证 / CI | ✅ | `Build-MsixPackage.ps1`、`MSIX-CertificationTest.ps1`、`.github/workflows/msix.yml` |
| 正式发布配置出 x64 MSIX | ✅ | 本次 `-ForStore` 产物 108 MB，`signed: false`（由商店签名） |
| WACK 完整测试并保存报告 | ❌ **未做** | 本机无 Windows App Certification Kit（随 Windows SDK 分发）。**仓库静态检查不能替代 WACK**，这条必须补 |
| 干净 Win10 / Win11 安装测试 | ❌ **未做** | 需要干净虚拟机；本机只能做稀疏包身份实测（2026-10-13） |
| 首跑 GUI + WebView2 + 后端 | ⚠️ 部分 | 代码路径已验证；WebView2 缺失分支本次新增，**未在缺运行时的机器上实跑** |
| WebView2 缺失有明确处理 | ✅ | 探测 + 官方下载指引 + 后台不退出。清单**不声明**依赖：官方文档明确 `win32dependencies:ExternalDependency` 只在 App Installer 安装时生效，商店安装走 PackageManager 会被忽略 |
| 未装 ClassIsland 时核心功能正常 | ✅ | 客户端与桥接解耦；桥接仅提供通知/组件，安装入口失败会提示手动方式 |
| 桥接缺失不崩溃、有说明与入口 | ✅ | 设置页「系统 → ClassIsland 桥接插件」按钮，宿主不可用时给出提示文案 |
| Python/依赖/静态资源/桥接完整打包 | ✅ | 包内 2190 个文件，含 `python-embed`、`Assets_Duty\web`、`bridge\DutyAgentBridge.dll` |
| 不依赖开发机的 Python/Node/VS | ✅ | 自包含发布（`coreclr.dll`/`hostfxr.dll` 在包内），Python 用内嵌解释器 |
| 无测试密钥/开发令牌/调试后门/无关文件 | ✅ 本次修复 | 包内 `test_*.py`=0、`logs`=0、`data`=0；密钥模式扫描（`sk-`/`ghp_`/`AKIA`/明文 `api_key`）零命中。DevTools 默认关闭，仅 `DUTY_WEBVIEW_DEVTOOLS=1` 打开 |
| `runFullTrust` 有合理说明 | ⚠️ | 代码侧只声明了这一个能力；**审核备注需写明用途**（见 §4 草稿） |
| 未请求不必要能力/管理员 | ✅ | 只有 `runFullTrust`，无 `broadFileSystemAccess`；全流程用户态 |
| 失败启动/端口冲突/异常退出/重启可恢复 | ✅ | 后端 watchdog 自动重启、`--port 0` 随机端口、`OnBackendRestarted` 重导航；WebView2 缺失不再整程序退出 |
| 升级不损坏数据 | ❌ **未验证** | 只有一个版本，v1→v2 覆盖安装需要另出 1.0.0.1 包做一次 |
| 卸载清理进程/文件/自启/注册表 | ⚠️ **部分** | 进程与文件由 MSIX 负责；**HKCU Run 值在商店版卸载后不会被清**（MSIX 无卸载钩子，清单又不能声明 `desktop6:startupTask`）——见 §5 已知限制 |
| 用户数据保留策略明确 | ✅ | 卸载不删 `%LOCALAPPDATA%\DutyAgent`；隐私政策 §5 + 设置页「数据重置」 |

### P0-3 隐私政策与个人数据

| 项 | 状态 | 证据 |
|---|---|---|
| `PRIVACY.md` 已合入 | ✅ | 在 `independent-agent` 分支（**origin/main 上还没有**） |
| 发布在无需登录的 HTTPS 地址 | ⚠️ **部分** | 现指向 `github.com/…/blob/independent-agent/PRIVACY.md`（GitHub 渲染、免登录）。**分支被删会 404**，建议合并进 `main` 或挂到你自己的域名后改成稳定 URL |
| Partner Center 填写该 URL | ❌ 待你填 | 见上 |
| 客户端内可查看隐私政策 | ✅ 本次修复 | 设置 → 关于 → 「隐私政策」 |
| 实际行为与政策一致 | ✅ 抽查 | 离线不联网、日志 14 天过期、数据落 `%LOCALAPPDATA%\DutyAgent` 均与政策一致 |
| 说明姓名/名单/编号/历史/规则/日志/Key 的用途 | ✅ | 政策 §2、§3.2 |
| 保存位置/保留期/删除方式/第三方类型 | ✅ | 政策 §4、§5 |
| 在线调用前告知发送内容 | ✅ 本次修复 | 向导第 2 步常驻说明 + 政策 §3.2 |
| 对第三方披露提供同意与撤回 | ✅ | 在线模型是用户自己配置的端点；撤回 = 切离线算法/删除 Key |
| 非用户个人信息（学生名单）审计 | ⚠️ | 政策 §5 已写明"录入他人信息需有依据"；**是否触发 10.5.3 的书面同意要求，属你的合规判断**，代码无法替代 |
| 姓名→编号脱敏严格验证 | ✅ | `build_prompt` 用 `all_roster_ids=`，提示词不含姓名；`cli.py` 外部 AI 通道同样只给编号。**注意：用户自己写的指令/规则会原样发送**，政策与向导都已提示 |
| 离线模式不向云端发送 | ✅ | `offline_scheduler.py` 纯本地；向导一键切换 |
| 凭据安全存储、不进日志 | ✅ 本次修复 | DPAPI 加密落盘（`test_config_secrets.py` 5 例）；`mask_secret` / `mcp_loopback._redact_api_keys` 负责日志与回环脱敏 |
| 本地 API/MCP 需授权 | ✅ | 动态 token（`secrets.token_urlsafe(32)`）或静态 PBKDF2 verifier；`SKIP_AUTH_BYPASS` 仅在显式环境变量下生效且启动即 WARN，客户端不设置 |
| 删除数据的方法 | ✅ | 设置 → 系统 → 数据重置（可归档） |
| 私密反馈渠道 | ✅ | 关于页：邮件（私密）+ Issues（公开，页面已提示勿提交真实名单） |
| 未成年人信息保护 | ⚠️ | 政策 §5 覆盖；具体合规由你判断 |

### P0-4 AI 功能专项（政策 11.16）

| 项 | 状态 | 证据 |
|---|---|---|
| 商店描述披露使用生成式 AI | ⚠️ 待填 | 文案草稿见 §4，**必须写进 Partner Center 描述** |
| Partner Center 声明实时生成式 AI | ❌ 待你勾选 | 提交页的 AI 相关选项 |
| 说明 AI 可能出错、可人工检查修改 | ✅ 本次修复 | 设置 → 关于；隐私政策 §4 |
| 不当输出举报渠道 | ✅ 本次修复 | 关于页「问题反馈」+ 邮件；政策写明 |
| 模型输出不能绕过鉴权/执行任意代码/破坏数据 | ✅ | 输出走 V2 INI 解析 + 确定性校验后才落库；无代码执行路径 |
| 避免生成/传播违规内容 | ⚠️ | 无内容过滤（排班场景），靠输出结构化 + 人工可改；如实写进备注即可 |
| 告知第三方模型费用与 Key 自理 | ✅ 本次修复 | 关于页明确"软件不提供、不转售模型额度" |
| 提供商不可用时安全失败/回退 | ✅ 代码 | `llm_transport` 失败上抛为任务失败 + 通知；用户可切离线算法。**未做断网实测** |

### P0-5 权限、安全与后台运行

| 项 | 状态 | 证据 |
|---|---|---|
| 自启用系统支持机制 + 知情同意 | ✅ 本次修复 | 默认关闭；向导/设置页显式开启；包身份下 Run 值名带 PFN |
| 检查 HKCU Run 回退实现 | ✅ | 值名 `DutyAgent.<PFN>`，命令 `explorer.exe shell:AppsFolder\<PFN>!App`；`IsEnabled` 兼容旧裸值并迁移 |
| 不因安装默认自启 | ✅ 本次修复 | 默认 `False`（三处一致） |
| 设置里关闭立即生效 | ✅ | `FileSystemWatcher` 监听 `host-config.json` → `ApplyLifecycleSettings` |
| 卸载不留失效 AUMID 启动项 | ❌ **未解决** | 见 §5 |
| 本地 API 只监听预期接口 | ✅ | `uvicorn.run(host="127.0.0.1")`，预绑定 socket 也绑 `127.0.0.1:0` |
| MCP/REST 鉴权与访问控制 | ✅ | 同源 token；MCP 默认关闭（`enable_mcp` 默认 false），且复用同一鉴权中间件 |
| WebView2 不暴露调试端口/高权限接口 | ✅ 本次加固 | DevTools 默认关；新增导航白名单；宿主消息按 type 白名单分发 |
| 仅可信来源执行预期代码 | ✅ | 见上；`open-external` 只放行 http/https/mailto |
| 恶意软件/依赖漏洞/敏感信息扫描 | ⚠️ 部分 | 敏感信息扫描已做（零命中）；**Defender/WACK 二进制检查未跑** |

---

## 3. P1 商店资料：现状与草稿

| 项 | 状态 |
|---|---|
| 应用名称 | ✅ `Duty-Agent 值日排班`（清单 DisplayName） |
| 分类 | ⚠️ 建议 **Productivity**（次选 Education） |
| 中文描述 | 草稿见 §4 |
| 区分独立客户端/桥接/云端 AI | 草稿已按此结构写 |
| 外部依赖在描述开头披露 | 草稿第 2 段 |
| 最低版本与架构 | Win10 1903 (18362) x64 |
| 截图（≥1，建议 4 张） | ❌ **0 张**，仓库内没有任何截图 |
| 图标/磁贴资产 | ✅ 49 张由 `winapp manifest update-assets` 生成 |
| 截图不含真实姓名/Key/日志 | — 待生成时保证（用虚构名单） |
| 支持链接/隐私政策/更新说明 | ⚠️ 链接已定；更新说明待写 |
| 年龄分级问卷 | ❌ 待你填（IARC） |
| 发行地区/价格/可发现性 | ❌ 待你填 |
| 不冒用官方身份/商标 | ✅ 文案无此类表述 |

### P1-7 审核材料（已在 §4 起草）

演示名单、无 Key 路径、离线/在线切换、`runFullTrust` 理由、Python 子进程/WebView2/MCP 用途、ClassIsland 可选性、隐私边界——全部写进备注草稿。

---

## 4. 可直接粘贴的材料草稿

### 4.1 认证备注（Notes for certification）

> **What this app does**
> Duty-Agent is a classroom duty-roster scheduler for Windows. It stores a roster,
> generates a duty schedule (optionally with an LLM the user configures), lets the
> user review/edit each entry, and can display the result through an optional
> ClassIsland desktop component.
>
> **How to test in under two minutes (no account, no API key, no payment)**
> 1. Launch the app. The first-run wizard opens.
> 2. Step 1 "导入名单": paste any demo names, one per line (e.g. `Alice / Bob / Carol`),
>    leave the "start with Windows" checkbox as you prefer, click 保存并继续.
> 3. Step 2 "选择模型": click **"使用离线算法（无需 API Key，不联网）"** at the bottom.
>    This switches to the fully local deterministic scheduler. No network request is
>    made and no credential is required.
> 4. Step 3: click 试跑一次 → a 5-day schedule appears in a table.
> All features required for review are reachable through this path.
>
> **Optional AI path (not required for review)**
> The user may instead enter an OpenAI-compatible endpoint + model + API key. The
> key is encrypted with Windows DPAPI before being written to disk. Requests go
> directly from the user's machine to the endpoint the user entered; the developer
> operates no server and receives no data. Prompts contain roster **IDs**, dates,
> areas and the user's instruction — not names.
>
> **Why runFullTrust is required**
> This is a Win32 desktop application packaged as MSIX. It hosts a local WebView2
> UI, runs a bundled Python scheduling engine as a child process, writes user data
> to `%LOCALAPPDATA%\DutyAgent`, and — only when the user explicitly clicks the
> button — copies an optional plugin DLL into a ClassIsland installation folder on
> the same machine. No broadFileSystemAccess or other restricted capability is
> declared; all file access uses the user's normal Win32 permissions.
>
> **Components**
> - `duty-agent.exe` — WinForms shell + WebView2 host (UI is a local page served from 127.0.0.1).
> - `Assets_Duty\python-embed\python.exe` — bundled CPython running the scheduling engine;
>   bound to 127.0.0.1 only, protected by a per-run bearer token.
> - Local REST/MCP API — loopback only, disabled by default, same bearer-token auth.
> - ClassIsland bridge — **optional**; the app is fully functional without it.
>
> **Privacy boundary**
> No developer-operated backend. Roster/schedule/logs stay under `%LOCALAPPDATA%\DutyAgent`.
> Backend logs expire after ~14 days. Only when the user configures an online model do
> data leave the device, and only to that endpoint.
>
> **Known limitations (by design)**
> - x64 only.
> - Auto-start is off by default and only written after the user enables it. Because the
>   manifest declares no `desktop6:startupTask` extension (the build toolchain cannot
>   validate Desktop extensions), the app falls back to an `HKCU\...\Run` value; that
>   value may remain in the registry after uninstall and is harmless (it points at an
>   AUMID that no longer resolves). We provide a cleanup script in the repo.
> - When started via the auto-start entry the main window is shown (MSIX activation
>   cannot pass the `--silent` argument).

### 4.2 商店列表文案（中文）

**简短描述（≤ 200 字）**
> 班级值日排班助手：导入名单，一键生成值日表，支持离线算法与自备大模型两种方式。
> 排班结果可逐条查看和修改，并可选择把当天值日投到 ClassIsland 桌面组件上。

**详细描述**
> Duty-Agent 是一款面向班级的值日排班工具，把"谁哪天值日、负责哪个区域"这件事
> 从纸面搬到桌面。
>
> **两种排班方式**
> · 离线算法：完全在本机计算，不联网、不需要任何账号或 API Key。
> · AI 排班：使用你自己配置的、兼容 OpenAI 接口的模型服务（本地 LM Studio / Ollama
>   或云端均可）。模型服务与费用由你自理，本软件不提供也不转售模型额度。
>   发送给模型的是人员编号、日期、区域与你的指令，**姓名不会离开本机**。
>
> **可核对、可修改**
> AI 生成的结果会经过本地规则校验，并在排班页逐条列出；你可以直接改人、改区域，
> 也可以回滚到历史版本。AI 输出可能出错，最终以你确认的结果为准。
>
> **可选：投到 ClassIsland**
> 安装随包的桥接插件后，ClassIsland 的卡片/组件可以显示当天值日人员。
> 该集成为可选，不安装时本软件全部核心功能正常。
>
> **隐私**
> 不需要注册账号。名单、排班、配置与日志都保存在本机 `%LOCALAPPDATA%\DutyAgent`，
> 不会上传到开发者服务器。模型 API Key 使用 Windows DPAPI 加密后落盘。
> 详见隐私政策（应用内「设置 → 关于」可直接打开）。
>
> **系统要求**
> Windows 10 1903（build 18362）或更高，x64；主界面依赖 Microsoft Edge WebView2
> 运行时（Windows 11 自带，Windows 10 缺失时应用内会给出官方安装指引）。

### 4.3 商店列表文案（English，简版）

> **Short:** Classroom duty-roster scheduler. Import a roster, generate a duty
> schedule locally or with your own LLM endpoint, review and edit every entry, and
> optionally show today's duty list on a ClassIsland desktop widget.
>
> **Long:** Duty-Agent schedules who is on duty on which day and area. It ships a
> fully offline deterministic scheduler (no account, no API key, no network) and an
> optional AI mode that talks only to the OpenAI-compatible endpoint you configure —
> prompts contain roster IDs, dates and areas, never names. Results are validated
> locally and can be edited entry by entry. No developer-operated backend: roster,
> schedules, settings and logs stay in `%LOCALAPPDATA%\DutyAgent`, and API keys are
> encrypted with Windows DPAPI. Requires Windows 10 1903+ (x64) and the WebView2
> Runtime (bundled with Windows 11; the app shows install guidance if missing).

---

## 5. 仍然缺的（按谁来做分组）

### 5.1 只能你做（Partner Center / 合规判断）

1. 核对 Product identity 的 `Package/Identity/Name` 与 `Publisher` **逐字一致**。
2. 填隐私政策 URL（建议先把 `PRIVACY.md` 合进 `main`，或托管到你自己的域名）。
3. 年龄分级问卷（IARC）、定价与市场、可发现性。
4. 商店列表：分类（Productivity）、描述、关键词、支持联系方式、Copyright。
5. **上传 4 张以上截图**（当前 0 张）。
6. 在提交页勾选"实时生成式 AI"相关声明。
7. 确认软件著作权归属。
8. 隐私合规判断：名单含学生姓名，是否触发 10.5.3 的书面同意要求。

### 5.2 需要环境/时间（我可以做，但要你点头或提供环境）

1. **WACK**：需要装 Windows SDK（或在一台装了 VS 的机器上跑）→ 出原始报告。
2. **干净 Win10 + Win11 首装/首跑**：需要虚拟机；顺带验 WebView2 缺失分支。
3. **v1 → v2 覆盖安装**：需要再出 `1.0.0.1` 包做一次升级回归。
4. **商店版卸载残留**：装一次包身份版本 → 开自启 → 卸载 → 查 `HKCU\...\Run`。
5. **截图**：起客户端 + 虚构名单，截 4 张（排班主界面 / 名单管理 / AI 配置 / 结果与历史）。
6. **断网场景**：AI 任务失败提示与回退。

### 5.3 已知限制（不修，但要在备注里写明）

- **卸载后 `HKCU\...\Run` 残留**：MSIX 没有卸载钩子；清单又不能声明 `desktop6:startupTask`
  （本机 makeappx/winapp 的 schema 集没有 Desktop XSD，任何 `desktop:` 扩展都校验失败）。
  真正的修法是换完整 Windows SDK 构建并恢复清单注释里那段 XML——属独立批次。
  当前缓解：默认不开自启；残留值指向失效 AUMID，登录时静默失败；仓库提供
  `scripts\Uninstall-MsixForTesting.ps1` 清理。
- **触摸**：`docs/store-touch-remediation-plan.md` 的批次 0/1 不在本仓库（README 已更正）。
  不影响审核，属质量项。
- **MSIX 自启无 `--silent`**：登录后显示主窗口而非静默驻留托盘。

---

## 6. 复现命令

```bat
:: 1. 出商店包（不签名、不上传）
build_msix.bat -ForStore -NoUpload
:: 产物：.release\HelemaZuba.Duty-Agent_1.0.0.0_x64.msix（约 108MB，signed=false）

:: 2. 对仓库清单做静态认证（CI 同款）
powershell -ExecutionPolicy Bypass -File scripts\MSIX-CertificationTest.ps1 -EnforceStorePublisher

:: 3. 对包内清单再跑一遍（必须：winapp 会重排属性）
::    解包后：
powershell -ExecutionPolicy Bypass -File scripts\MSIX-CertificationTest.ps1 `
  -ManifestPath .msix-work\verify-<ts>\AppxManifest.xml -LayoutPath .msix-work\verify-<ts> -EnforceStorePublisher

:: 4. 单测
dotnet test DutyAgent.Client.Tests -c Release
::    后端：每个 test_*.py 是独立脚本（CI 逐个 python 跑）
```

> 本机跑后端单测的坑：`python-embed` 的 `python311._pth` 把 `sys.path` 固定成解释器目录，
> 直接用 `-m unittest` 或 `PYTHONPATH` 都找不到 `Assets_Duty` 下的模块。用
> `python-embed\python.exe -c "import sys,runpy; sys.path.insert(0,'.'); runpy.run_path('test_x.py', run_name='__main__')"`
> 或直接用系统 Python 3.11（CI 就是这么跑的）。
>
> `test_cli.py` 在本机有 1 例失败（`test_plan_prompt_missing_roster_returns_error_result`）：
> 它发现并连上了本机正在运行的真实后端（meta 指向 `D:\ClassIsland_...\DutyAgentBridge`），
> 于是拿到 401 而不是预期的 roster 错误。属环境耦合，不是本次改动引入——CI 上没有这个
> meta 文件时该用例走 mock。
