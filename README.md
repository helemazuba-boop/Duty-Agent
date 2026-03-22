<div align="center">
  <img src="icon.png" width="128" alt="Duty-Agent Logo" />
  <h1>Duty-Agent</h1>
  <p><em>"Reasoning via LLM, Reliability via Code." —— 面向 ClassIsland 的下一代混合智能排班系统</em></p>
  <p><strong>当前版本: v0.51.2 Beta</strong></p>
</div>

> **注意：项目理论上的逻辑已经通过，正在真实环境下优化细节。如果您需要在生产环境中应用，请稍等正式版。**

`Duty-Agent` 是一个专为 **[ClassIsland](https://github.com/ClassIsland/ClassIsland)** 设计的下一代智能排班插件。它首创了 **“混合智能”** 架构——将大语言模型 (LLM) 的**灵活性**（理解自然语言指令）与 Python 代码的**确定性**（算法兜底、状态持久化）深度融合，彻底解决了传统排班算法（如轮询、随机）僵硬且无法处理复杂人事变动的痛点。


## 项目定位

Duty-Agent 解决的是“有真实规则、有临时变化、有长期状态”的值日排班问题。它适合以下场景：

- 需要在 ClassIsland 内直接管理值日排班
- 需要通过自然语言描述临时变化，例如请假、补班、替班、额外任务
- 需要保留排班状态、名单状态和后续运行上下文
- 需要为不同模型能力选择不同执行模式
- 需要通过 WebView、MCP 或 API 接入外部界面或自动化系统

## 当前能力

- 在 ClassIsland 内完成值日排班的配置、执行、预览和编辑
- 支持标准、6-Agent、增量小模型三种方案
- 支持通过 WebView2 展示网页界面
- 支持通过 MCP 暴露工具能力
- 支持通过 API 调用后端执行与配置能力
- 支持手动执行和定时自动执行
- 支持值日完成通知和值日提醒通知
- 支持名单导入、启停、排班结果回写和状态持久化

## 工作方式

Duty-Agent 的执行核心已经收敛到后端服务。用户在 ClassIsland 中发起排班后，插件会把排班请求送入后端，由后端统一读取配置、名单和状态，再根据当前方案生成 Prompt、调用模型、解析结果并写回排班状态。

当前版本的 Prompt 系统支持动态注入。后端会根据方案、模型画像、规则、名单和状态拼装输入内容，而不是固定使用一份静态提示词。这一做法的目标是减少无效上下文，降低 token 消耗，同时保留不同模式下所需的约束信息。

设置系统也已经重构。现在的设置页风格和交互更接近 ClassIsland 宿主，宿主配置和后端配置已经分离，用户主要通过插件设置页完成模型与方案管理。

## 模式说明

### 标准

标准模式走单次执行链，适合使用能力较强的云模型。它的目标是在较少 token 消耗下得到稳定可用的排班结果，适合作为默认方案。

### 6-Agent

6-Agent 模式适合关系较复杂、约束较多、需要更强过程拆分的场景。它把模板提取、账本意图提取、规则提取、优先池生成、指针池生成和最终装配拆成独立阶段，再由代码做屏障校验和最终结算。

### 增量小模型

增量小模型模式仍然属于单次执行链，但会使用更适合小模型的输入组织方式。它更强调细化约束和稳定输出，适合在模型综合能力有限时使用。

### 边沿模型

项目已经为边沿微调模型预留了执行框架。未来面向教室设备部署的微调模型会继续复用单次执行主链，但使用更细化的 Prompt 和更高的推理预算，以换取更稳定的本地排班效果。

## 使用流程

典型使用流程如下：

1. 在 ClassIsland 中安装并启用 Duty-Agent
2. 打开设置页，选择方案并填写模型地址、模型名称和 API Key
3. 导入或维护名单
4. 根据需要填写规则、每天人数等参数
5. 手动执行排班，或启用自动运行
6. 在设置页、组件或通知中查看结果

如果需要更高阶的接入方式，还可以通过 WebView 页面、MCP 工具或 API 调用同一套后端能力。

## 接入方式

### ClassIsland 设置页

这是最直接的使用方式。绝大部分用户通过宿主设置页即可完成配置、排班和结果查看。

### WebView 页面

项目内置了 WebView2 页面承载能力，适合做更灵活的操作界面或后续控制台原型。

### MCP

Duty-Agent 提供 MCP 接入能力，便于外部 AI 或自动化工具使用“排班”“更新配置”“导入名单”等能力。

推荐配置方式如下：
- 在 ClassIsland 插件设置页中启用 `MCP`。
- 默认使用随机服务端口；如果需要让外部 MCP 客户端长期稳定接入，建议在“高级设置 -> MCP 导入接口”中切换到 `固定端口`。
- 如果需要让外部 MCP 客户端长期稳定接入，建议在“高级设置 -> 访问鉴权”中切换到 `静态 token` 模式。`动态 token` 会在 Python 后端每次重启后变化，更适合宿主内部链路。
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

其中 `<python-backend-port>` 是插件启动后本地 Python 后端实际监听的服务端口。这个端口同时用于：
- MCP
- REST API
- 内置 `/app/` 页面

如果你启用了固定端口，MCP 地址可以长期保持稳定；如果仍然使用随机端口，请以设置页显示的“当前 MCP 地址”为准。

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

更推荐把上面的 `51234` 换成你在设置页里配置的固定服务端口，例如：

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

如果你希望 MCP 的执行链路尽可能与当前 C# 设置页一致，当前实现已经做了以下对齐：
- `inspect_workspace`、`update_scheduler_config`、`replace_roster`、`edit_schedule_entry` 会走现有 FastAPI HTTP API。
- `run_schedule` 会优先走 `/api/v1/duty/live` WebSocket 控制链，只在控制通道已经建立后发生可恢复传输异常时才回退到 SSE。
- `/api/v1/duty/live` 当前是单 owner 连接策略；如果 ClassIsland 宿主已经占用该控制通道，MCP 的 `run_schedule` 会返回 busy。

排障提示：
- 如果 MCP 客户端报 `ERR_CONNECTION_REFUSED`，优先检查后端是否已启动，以及你填写的端口是否就是设置页显示的当前服务端口；这一步通常和 token 无关。
- 如果固定端口被占用，宿主会回退到随机端口继续启动，但本次运行会禁用 MCP。设置页会明确显示“已回退随机端口，MCP 已禁用”，此时外部 MCP 客户端无法连接，直到你修复端口冲突并重启。

### API

后端提供正式 API，可作为后续控制台、局域网接入或其他程序集成的基础。

## 配置与数据

当前项目已经把配置职责拆分开：

- 宿主配置：由 ClassIsland 插件本地管理，负责通知、定时、WebView/MCP 壳层等宿主功能
- 后端配置：由后端统一管理，负责模型、方案、规则、执行模式等排班执行相关能力

常见数据包括：

- 后端配置
- 花名册
- 排班状态
- 运行日志

这套结构的目标是让“界面”和“执行核心”分工明确，减少配置互相覆盖的问题。

## 日志与排查

项目已经加入较详细的诊断日志。设置页配置加载、保存、排班执行、后端配置请求和后端状态变更都会写入日志，便于定位问题。

当排班异常、设置未生效、模式行为不符合预期时，日志通常比界面提示更接近真实原因。

## 适用人群

Duty-Agent 适合：

- 使用 ClassIsland 管理班级事务的教师或管理员
- 需要比传统轮询排班更灵活的值日场景
- 希望把 AI 排班能力接入现有校园工作流的用户
- 想通过 MCP、WebView 或 API 扩展插件能力的开发者

## 开发与构建

项目由 C# 插件层和 Python 后端共同组成：

- C# 负责宿主集成、设置页、通知、WebView 和本地壳层
- Python 负责后端配置、执行核心、Prompt 组织、模型调用和结果结算

如果你计划在本地开发或调试，建议先确保：

- ClassIsland 插件运行环境正常
- Python 后端依赖可用
- 所选模型接口可访问
- 日志目录可写

## 许可证

MIT License
