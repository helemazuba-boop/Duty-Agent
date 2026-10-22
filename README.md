<div align="center">
  <img src="icon.png" width="128" alt="Duty-Agent Logo" />
  <h1>Duty-Agent</h1>
  <p><em>"Reasoning via LLM, Reliability via Code." —— 面向 Windows 的下一代混合智能排班系统</em></p>
  <p><strong>当前版本: v0.51.8 Beta</strong></p>
</div>

> **注意：项目理论上的逻辑已经通过，正在真实环境下优化细节。如果您需要在生产环境中应用，请稍等正式版。**

`Duty-Agent` 是一套智能排班系统，适用于教室值日安排、工作排班。它将大语言模型 (LLM) 的**灵活性**（理解自然语言指令）与 Python 代码的**确定性**（算法兜底、状态持久化）深度融合，彻底解决了传统排班算法（如轮询、随机）僵硬且无法处理复杂人事变动的痛点。


## 项目定位

Duty-Agent 解决的是“有真实规则、有临时变化、有长期状态”的值日排班问题。它适合以下场景：

- 需要在 ClassIsland 主屏显示今日值日安排，并接收排班通知与到点提醒
- 需要通过自然语言描述临时变化，例如请假、补班、替班、额外任务
- 需要保留排班状态、名单状态和后续运行上下文
- 需要为不同模型能力选择不同执行模式
- 需要通过 WebView、MCP 或 API 接入外部界面或自动化系统

## 系统组成

Duty-Agent 由三部分组成，可以分开安装、分开更新：

| 部分 | 形态 | 职责 |
|---|---|---|
| **独立客户端** | Windows 桌面程序（`DutyAgent.Client`）| 主界面（内嵌 WebView）、配置、名单、排班执行与预览；同时负责启动并看护本地后端进程。**日常操作主要在这里完成。** |
| **ClassIsland 桥接插件** | ClassIsland 插件（`duty-agent-bridge`）| 在主屏显示"值日人员"组件、转发排班通知与到点提醒，并提供"今日值日匹配"规则与"执行值日排班"自动化动作。**不包含配置/名单/排班界面。** |
| **后端引擎** | 本地服务（Python / FastAPI）| 真正的排班执行核心：读取配置、名单和状态，组织 Prompt、调用模型、解析结果并写回排班状态。由客户端自动拉起，一般无需手动管理。 |

桥接插件与独立客户端通过本机回环（127.0.0.1）通信，因此两者需要安装在同一台机器上。不使用 ClassIsland 的用户，只装独立客户端即可。

## 当前能力

- 在独立客户端内完成值日排班的配置、执行、预览和编辑
- 支持标准、Agents两种方案
- 支持通过 MCP 暴露工具能力
- 支持通过 API 调用后端执行与配置能力
- 支持手动执行和定时自动执行
- 支持值日完成通知和值日提醒通知
- 支持名单导入、启停、排班结果回写和状态持久化
- 支持突发情况处理：自然语言或 API/CLI/MCP 登记请假缺席（带日期区间，到期自动失效），排班结果由 Python 硬校验并确定性修复（剔除缺席、缺额补齐、超额裁剪）
- 支持用户备注（如“周三大扫除”）注入后续排班提示词，带过期日期、不会被运行结果覆盖
- 支持单日人数覆盖（大扫除等临时加派），按日期/区域生效并参与校验

## 工作方式

Duty-Agent 的执行核心已经收敛到后端服务。用户在独立客户端（或 ClassIsland 的自动化动作、WebView、MCP、CLI）中发起排班后，请求会送入后端，由后端统一读取配置、名单和状态，再根据当前方案生成 Prompt、调用模型、解析结果并写回排班状态。

当前版本的 Prompt 系统支持动态注入。后端会根据方案、模型画像、规则、名单和状态拼装输入内容，而不是固定使用一份静态提示词。这一做法的目标是减少无效上下文，降低 token 消耗，同时保留不同模式下所需的约束信息。


## 模式说明

### 标准

标准模式走单次执行链，适合使用能力较强的云模型。它的目标是在较少 token 消耗下得到稳定可用的排班结果，适合作为默认方案。

### Agents

6-Agent 模式适合关系较复杂、约束较多、需要更强过程拆分的场景。它把模板提取、账本意图提取、规则提取、优先池生成、指针池生成和最终装配拆成独立阶段，再由代码做屏障校验和最终结算。

## 使用流程

典型使用流程如下：

1. 安装并启动**独立 Duty-Agent 客户端**（它会自动拉起本地后端）
2. 在客户端中选择方案并填写模型地址、模型名称和 API Key
3. 导入或维护名单
4. 根据需要填写规则、每天人数等参数
5. 手动执行排班，或启用自动运行
6. 在客户端中查看结果

如果你还想在 ClassIsland 里显示值日人员、接收排班通知：

7. 在客户端中使用**一键安装桥接插件**（会把插件复制到 ClassIsland 插件目录），重启 ClassIsland 后即可看到"值日人员"组件、通知，以及"执行值日排班"自动化动作与"今日值日匹配"规则

如果需要更高阶的接入方式，还可以通过 WebView 页面、MCP 工具或 API 调用同一套后端能力。

## 接入方式

### 独立客户端（推荐）

这是最直接的使用方式。绝大部分用户通过客户端即可完成配置、排班和结果查看。

### ClassIsland 桥接插件

桥接插件只负责"把结果搬到 ClassIsland 里"，它**不包含配置页、名单管理或排班预览**——这些都在独立客户端中完成。它提供：

- **桌面组件"值日人员"**：在主屏显示今日值日（字体、颜色、换行方式可配）
- **通知**：排班完成、排班失败、值日到点提醒
- **自动化**：动作"执行值日排班"，规则"今日值日匹配"，以及三个触发器（排班成功 / 排班失败 / 值日安排更新）
- **设置页**：仅显示与独立客户端的连接状态，并提供手动重连

前置条件：客户端与 ClassIsland 位于同一台机器。桥接插件通过客户端写入的连接信息（端口、token）自动发现后端；便携部署可用环境变量 `CLASSISLAND_CONFIG_PATH` 指向 ClassIsland 配置目录。

### WebView 页面

独立客户端的主界面就是内嵌的 WebView2 页面（后端 `/app/`），配置、名单、排班与预览都在其中完成。这也是后续控制台原型的承载方式。

### MCP

Duty-Agent 提供 MCP 接入能力，便于外部 AI 或自动化工具使用“排班”“更新配置”“导入名单”等能力。

推荐配置方式如下：
- 在客户端数据目录的 `host-config.json` 中把 `enable_mcp` 设为 `true`（默认 `false`）。该项在客户端启动后端时读取，**改动后需重启客户端**才能生效。
- MCP 与 REST API、`/app/` 页面共用同一个后端服务端口。默认随机端口会随每次重启变化；如需让外部 MCP 客户端长期稳定接入，把 `host-config.json` 的 `server_port` 设为固定端口（如 `38888`；若该端口被占用，客户端会自动回退到随机端口）。
- 如果需要让外部 MCP 客户端长期稳定接入，建议同时把 `access_token_mode` 切换到 `static` 模式。`dynamic` token 会在 Python 后端每次重启后变化，更适合宿主内部链路。
- MCP 服务使用和 API 相同的 Bearer 鉴权边界，请在 MCP 客户端配置里填写 `Authorization: Bearer <token>`。
- 这里有两类不同的凭据：
  - `Bearer <token>` 是访问 Duty-Agent 本地 API / MCP 服务本身的鉴权 token。
  - `api_key` 是 Duty-Agent 调用上游模型提供商时使用的 provider API Key。
- 如果当前选中的排班方案里已经保存了 `api_key`，MCP 客户端不需要额外再提供 provider API Key。
- 只有当当前方案的 `api_key` 留空时，执行器才会回退读取环境变量：
  - 优先使用 `DUTY_AGENT_MCP_API_KEY`
  - 未设置时回退到 `DUTY_AGENT_API_KEY`

当前 MCP 通过本地 FastAPI 进程提供 `Streamable HTTP` 接入，路径为：

```text
http://127.0.0.1:<python-backend-port>/mcp/
```

其中 `<python-backend-port>` 是客户端启动后本地 Python 后端实际监听的服务端口。这个端口同时用于：
- MCP
- REST API
- 内置 `/app/` 页面

如果你在 `host-config.json` 里设置了固定 `server_port`，MCP 地址可以长期保持稳定；否则请以客户端当前实际使用的服务端口为准。

当前暴露的 MCP tools：
- `inspect_workspace`
- `update_scheduler_config`
- `replace_roster`
- `edit_schedule_entry`
- `run_schedule`

示例：按 `streamableHttp` 方式配置 MCP 客户端

```json
{
  "mcpServers": {
    "duty-agent": {
      "type": "streamableHttp",
      "url": "http://127.0.0.1:51234/mcp/",
      "headers": {
        "Authorization": "Bearer <static-access-token>"
      }
    }
  }
}
```

更推荐把上面的 `51234` 换成你在客户端里配置的固定服务端口，例如：

```json
{
  "mcpServers": {
    "duty-agent": {
      "type": "streamableHttp",
      "url": "http://127.0.0.1:38888/mcp/",
      "headers": {
        "Authorization": "Bearer <static-access-token>"
      }
    }
  }
}
```

如果你没有在当前选中的方案里保存 provider API Key，才需要在启动 MCP 客户端前设置环境变量。优先使用 `DUTY_AGENT_MCP_API_KEY`，未设置时回退到 `DUTY_AGENT_API_KEY`。

```powershell
$env:DUTY_AGENT_MCP_API_KEY = "<provider-api-key>"
```

如果你的 MCP 客户端只支持设置一个兼容名称，也可以使用：

```powershell
$env:DUTY_AGENT_API_KEY = "<provider-api-key>"
```

如果你希望 MCP 的执行链路尽可能与客户端一致，当前实现已经做了以下对齐：
- `inspect_workspace`、`update_scheduler_config`、`replace_roster`、`edit_schedule_entry` 会走现有 FastAPI HTTP API。
- `run_schedule` 会优先走 `/api/v1/duty/live` WebSocket 控制链，只在控制通道已经建立后发生可恢复传输异常时才回退到 SSE。
- `/api/v1/duty/live` 当前是单 owner 连接策略；如果客户端或 ClassIsland 桥接已经占用该控制通道，MCP 的 `run_schedule` 会返回 busy。

排障提示：
- 如果 MCP 客户端报 `ERR_CONNECTION_REFUSED`，优先检查后端是否已启动，以及你填写的端口是否就是客户端显示的当前服务端口；这一步通常和 token 无关。
- 如果固定端口被占用，客户端会回退到随机端口继续启动；此时 MCP 仍可用，只是地址变成随机端口，外部 MCP 客户端需要重新读取实际端口（或修复端口冲突后重启以恢复固定地址）。

### CLI（外部 AI 上下文工程）

如果你已经有一套成型的外部上下文工程（例如 Claude Code、Codex 等外部 AI），可以让**外部 AI 接管推理层**，而 Duty-Agent 继续负责全部确定性能力（Prompt 组织、匿名化、解析、校验、账本结算）。切割点只有一处：Prompt 原本发给你在方案里配置的模型 provider，现在改为交给外部 AI，要求它按当前模式格式（V2 纯文本 INI）产出，再回喂给后端跑现有解析与结算。

当前版本仅支持 `single_pass` 模式的无状态两阶段委派；其余多轮模式（Agents / orchestrator / tool_loop）保持原有 provider 链路不变。发给外部 AI 的 Prompt 天然只含数字 ID，不含真实姓名（ID 匿名为硬约束）。

两阶段委派流程：

```text
            plan-prompt                       plan-ingest
  ┌──────────────────────────┐     ┌────────────────────────────────┐
  │ CLI --instruction "本周排班" │     │ CLI --completion <V2 INI>       │
  │        │                 │     │        --handle <resume_context> │
  ▼        ▼                 │     │        │                        │
Duty-Agent 构建匿名 Prompt ───┘     ▼        ▼                        │
  │  (prompt_text + resume_context) │  Duty-Agent 解析/校验/还原/结算 ──┘
  ▼                                 ▼
外部 AI 按 V2 INI 格式推理 ────────▶ state.json 落地 + 返回 snapshot
```

- `plan-prompt`：返回 `prompt_text` / `messages` / `resume_context`（一个不透明的可序列化 JSON handle）。
- 外部 AI：读取 `prompt_text`，自行推理出 V2 INI 排班文本。
- `plan-ingest`：把 completion 与 `resume_context` 回喂后端，落地排班并返回 `snapshot`。

CLI 默认连接**已经运行**的后端，连接信息解析优先级：`--base-url`/`--token` 显式参数 > 环境变量 `DUTY_AGENT_BASE_URL` / `DUTY_AGENT_TOKEN` > 自动发现 `.duty-agent-meta.json` > `--data-dir` 下的 `data/.dev-token` + `--port`（默认 8765）。

若后端尚未运行，外部 AI 可**仅凭 CLI 端到端自举**，无需手工拉起进程：

```bash
# 0) 探活 / 自举 / 停止（serve 幂等：已在跑则直接复用）
duty-cli.bat status                       # 探活：running/managed/version
duty-cli.bat serve --port 8765            # 后台分离拉起后端，写 pid + meta + token
duty-cli.bat serve --stop                 # 停止由 CLI 托管的后端
```

`serve` 以分离进程方式启动 `core.py --server --no-parent-watch`（跳过父进程自杀监控，改由 pid 文件托管生命周期），健康检查通过后把端口/token 写入 `<data-dir>/.duty-agent-meta.json`，后续命令即可自动发现。注意：`serve`/`status` 的 `--port`、`--data-dir` 是子命令参数（可放在子命令后）；其余命令的 `--port`/`--data-dir` 是全局参数，需放在子命令之前。

外部 AI 的典型 shell 调用样例：

```bash
# 1) 取回匿名 Prompt（stdout 恒为单个 JSON）
duty-cli.bat plan-prompt --instruction "本周排班" > prompt.json

# 2) 外部 AI 读取 prompt.json 里的 prompt_text，推理出 V2 INI，写入 out.ini；
#    并把 prompt.json 里的 resume_context 原样存为 handle.json

# 3) 回喂 completion 与 handle，落地排班（PowerShell 下用 --*-file 避开 @ 与 splatting 冲突）
duty-cli.bat plan-ingest --completion-file out.ini --handle-file handle.json
```

`--completion` / `--handle` 仍支持字面量、`@文件路径`、`-`（stdin）；但 PowerShell / opencode 环境下 `@` 会与 splatting 运算符冲突，优先用 `--completion-file` / `--handle-file`（各文件型参数均有 `--*-file` 伴生项，且 BOM 安全）。

CLI 面向 AI 自发现设计：运行 `duty-cli.bat describe`（或**不带任何子命令直接运行** `duty-cli.bat`）会输出全部子命令的机器可读 JSON schema（无需网络），外部 AI 可据此自动编排调用。即便 AI 敲错命令或漏传必填参数，stdout 仍返回 `{"status":"error",...}` 并附 `hint` 指向 `describe`（退出码 2），因此零先验的首次接触即可自解释。除委派命令外，CLI 还复用现有 REST 暴露了确定性子命令：`inspect`、`get-config`、`update-config`、`get-roster`、`replace-roster`、`edit-entry`、`rollback`、`health`；以及生命周期命令 `serve`（含 `--stop`/`--force`）、`status`。

输出约定：stdout 恒为单个 JSON（UTF-8），进度/日志走 stderr；失败时退出码非 0 且 stdout 为 `{"status":"error",...}`；返回体中的 `api_key` 默认脱敏，加 `--show-secrets` 可保留，`--pretty` 供人类阅读。

除委派两阶段外，CLI 还提供 `run --instruction "..."`：由后端直接调用**其自身配置的模型 provider**完成一次排班（provider 路径，适用于不由外部 AI 接管推理的场景）；配合 `update-config` 把选中方案的 `base_url`/`model` 指向本地模型（如 LM Studio `http://localhost:1234/v1`）即可本地跑通。

### API

后端提供正式 API，可作为后续控制台、局域网接入或其他程序集成的基础。

## 配置与数据

当前项目已经把配置职责拆分开：

- **客户端与后端配置**：由独立客户端统一管理，负责模型、方案、规则、执行模式、名单等排班执行相关能力，以及后端进程的生命周期
- **桥接插件设置**：仅保存在 ClassIsland 为本插件分配的配置目录中（连接开关、健康检查间隔等），随宿主配置一起备份

常见数据包括：

- 后端配置（`config.json`）
- 花名册（`roster.csv`）
- 排班状态（`state.json`）
- 宿主级配置（`host-config.json`）
- 运行日志（`logs/`）

这些数据默认位于 `%LOCALAPPDATA%\DutyAgent\data\`。桥接插件与独立客户端通过一个连接信息文件（`.duty-agent-meta.json`）互相发现，该文件位于 ClassIsland 配置目录的 `DutyAgentBridge\` 下。

这套结构的目标是让"界面"和"执行核心"分工明确，减少配置互相覆盖的问题。

## 日志与排查

项目已经加入较详细的诊断日志。客户端会写入 `%LOCALAPPDATA%\DutyAgent\data\logs\client-*.log`，桥接插件会写入其插件目录下的 `logs\`。排班执行、后端配置请求和后端状态变更都会写入日志，便于定位问题。

当排班异常、设置未生效、模式行为不符合预期时，日志通常比界面提示更接近真实原因。

## 适用人群

Duty-Agent 适合：

- 使用 ClassIsland 管理班级事务的教师或管理员
- 需要比传统轮询排班更灵活的值日场景
- 希望把 AI 排班能力接入现有校园工作流的用户
- 想通过 MCP、WebView 或 API 扩展能力的开发者

## 开发与构建

项目由独立客户端、ClassIsland 桥接插件和 Python 后端共同组成：

- **独立客户端**（`DutyAgent.Client/`，WinForms + WebView2）负责主界面、后端进程管理与桥接插件安装
- **桥接插件**（`Duty-Agent-ClassIsland-Bridge/`）负责 ClassIsland 内的组件、通知与自动化
- **Python 后端**（`Assets_Duty/`）负责后端配置、执行核心、Prompt 组织、模型调用和结果结算

常用构建命令：

```bash
dotnet build DutyAgent.Client/DutyAgent.Client.csproj -c Release                 # 客户端
dotnet build Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj -c Release     # 桥接插件
build_client.bat                                                                 # 完整发布（客户端 + 桥接 + 后端）
```

如果你计划在本地开发或调试，建议先确保：

- .NET 8 SDK 可用
- ClassIsland 运行环境正常（调试桥接插件时）
- Python 后端依赖可用
- 所选模型接口可访问
- 日志目录可写

更详细的开发者说明（架构、数据分层、进程生命周期、ClassIsland 集成点）见 `CLAUDE.md`。

## 上架微软商店（MSIX）

代码层的包身份适配已完成，打包骨架见 `DutyAgent.Client.Package/`。完整改造计划、
分批记录与实测数据见 `docs/msix-store-adaptation-plan.md`；上架与触摸适配的
修订版计划、落地记录与待真机清单见 `docs/store-touch-remediation-plan.md`。

前置条件：

- Node/npm 的 `@microsoft/winappcli`（`npm install -g @microsoft/winappcli`）——
  **不需要 Visual Studio 或 Windows Kits**，winapp 用 NuGet 的 Windows SDK BuildTools 打包
- Partner Center 开发者账号
- WebView2 运行时：Win11 预装；Win10 绝大多数设备已随 Edge 更新获得，但仍有少量
  缺失。当前客户端**还没有**做运行期版本探测，缺失时是 `InitializeWebViewAsync` 抛异常、
  由 `InitializeAsync` 兜成"启动失败"对话框（托盘与后端仍正常），这是待补项
  （不随包分发 Fixed Version——那会让包体再涨 250MB+）

构建：

```bat
build_msix.bat                                  :: 默认（版本沿用清单，并自动组装测试包）
build_msix.bat -ReleaseVersion 1.2.3
build_msix.bat -ForStore
```

等价于 `powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 <同样的参数>`。

产物在 `.msix-work\HelemaZuba.Duty-Agent_1.2.3_x64.msix`（本机已实证产出约 107MB，含完整内嵌 Python
后端的包）。提交商店前必须做的事：

1. `Package.appxmanifest` 里的 `Publisher` 与 `Name` 已换成 Partner Center 账户的发布者 ID
   与应用名（当前 `Name="HelemaZuba.Duty-Agent"` / `Publisher="CN=B4407923-BF46-468D-B8F8-549EDD96BDC3"`），
   提交前再逐字符核对一遍，并用 `-ForStore`（不本地签名）交付给商店签名。
   本地签名证书按清单 Publisher 派生：清单换了 Publisher 会自动重新生成，避免旧 subject 签名失败
2. 商店图标（`DutyAgent.Client.Package/Assets/`）需最终确认合规；当前由
   `winapp manifest update-assets icon.png` 生成

行为差异（包身份 vs zip 分发）：

| 能力 | zip 分发 | MSIX |
|---|---|---|
| 开机自启 | 写 `HKCU\...\Run`（exe 路径） | 优先系统 `StartupTask`；清单未声明该扩展时回退 `HKCU\...\Run`（值名带 PFN 后缀，命令 `explorer.exe shell:AppsFolder\<PFN>!App`，两条路都已实测）。**MSIX 自启无法传 `--silent`**：登录后会显示主窗口，而不是静默驻留托盘 |
| 开始菜单快捷方式 | 客户端自建 `.lnk` | 平台托管，客户端不再自建 |
| 通知 AUMID | 自建 `.lnk` 上的硬编码 ID | `<PackageFamilyName>!App` |
| 单实例互斥体 | `Local\DutyAgent.Client.*` | 同上加 `PackageFamilyName\` 前缀；跨形态另有**不带前缀**的数据目录 owner 互斥——zip 版与商店版可并存安装，但同机只允许一个客户端持有后端，后启动者唤醒前者后退出 |
| 数据与日志 | `%LOCALAPPDATA%\DutyAgent` | 同一路径，两种形态共用同一份数据（因此需要上面的跨形态互斥） |
| 与 ClassIsland 握手 | 写宿主配置目录 | 宿主目录 + `%LOCALAPPDATA%\ClassIsland\Config` + `LocalState` 三处，桥接按"新鲜度优先"取最应连接的一个，并兜底扫描 `Packages\<包>\...` |

> **MSIX 隔离语义已实证**（2026-10-13，winapp 稀疏包身份实测）：包身份进程写 Win32
> `%LOCALAPPDATA%` 子路径**不重定向**，三处 meta 落点 md5 一致，桥接（非包进程）可直接
> 读到 Win32 路径那份；`HKCU\...\Run` 写入同样不重定向；AUMID 激活可拉起全信任客户端。
> 逐条证据见 `docs/msix-store-adaptation-plan.md` 修正 B。

> **清单不声明任何 `<Extensions>`**：本机工具链（NuGet `Microsoft.Windows.SDK.BuildTools`
> 的 makeappx schema 集，22 个 XSD 无 Desktop schema）无法校验 `desktop:`/`desktop6:`
> 扩展元素，winapp 同理。改用完整 Windows SDK 构建时可恢复 `startupTask` 扩展，
> 现清单注释里留有现成 XML；Run 键回退逻辑继续兜底。`fullTrustProcess` 扩展经实测不需要
> （Python 子进程在稀疏包身份下启动/监管/自动重启均正常）。

> **触摸适配**：标题栏拖拽与边框缩放对触摸走 `WM_SYSCOMMAND + SC_MOVE/SC_SIZE`
> （`PostMessage` 投递，避免在 WebView 回调内同步重入消息泵）；窄窗口热区与控件在
> `any-pointer: coarse` 下放大。`DUTY_CHROME_PROBE=1` 可把 native / web 两条命中
> 路径写进客户端日志，用于真机定位。细节见 `docs/store-touch-remediation-plan.md`。

约束：仅 x64；未接 Store 商业化与增量更新通道；同机同时只运行一个客户端
（跨形态由数据目录 owner 互斥保证，否则会抢后端端口、写坏共享 state）。

## 许可证

MIT License
