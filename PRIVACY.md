# Duty-Agent 隐私政策 / Privacy Policy

**生效日期 / Effective date：2026-10-13**  
**适用范围 / Scope：Duty-Agent Windows 独立客户端、Python 后端及可选的 ClassIsland 桥接插件**  
**项目维护者 / Maintainer：Duty-Agent 项目维护者（GitHub：helemazuba-boop）**

[简体中文](#简体中文) | [English](#english)

---

## 简体中文

### 1. 产品及数据处理方式

Duty-Agent 是一款 Windows 本地智能排班工具，可管理成员名单、值日及工作排班、请假与替班记录，并可选用用户配置的 AI 模型提供商生成排班建议。本政策说明应用在设备上读取、保存、处理和可能传输的信息。

Duty-Agent **不要求注册 Duty-Agent 云端账号**。正常的名单和排班管理主要在用户设备上的客户端与本地 Python 后端进行。**但使用云端 AI 模型并非纯离线处理：排班提示词和相关内容会发送到用户选择的服务商。** 还可以由用户自行启用 API、MCP 和第三方集成。

### 2. 处理的信息与用途

为实现排班及相关功能，应用可能处理：

- **成员及排班数据**：用户导入或输入的姓名、编号、启用状态、排班日期、区域、人员分配、轮换/补偿状态，以及请假、替班、备注和相关日期。
- **用户输入与 AI 内容**：自然语言排班指令、规则、偏好、提示词、模型生成的内容及排班结果。
- **配置及凭据**：所选执行模式、模型名称、服务商 API 地址、API Key、宿主设置，以及供本地 API、MCP、桥接组件使用的端口和访问令牌。
- **诊断信息**：运行时间、错误、请求追踪标识、组件状态及故障排查日志。某些异常信息或用户输入可能意外包含个人信息，请在分享日志前检查和脱敏。
- **本地 WebView2 数据**：嵌入式界面可能在设备上产生缓存及其他浏览器运行数据。

这些信息用于显示和编辑名单、计算与生成排班、保存执行状态、提供提醒和通知、支持用户启用的集成，以及诊断应用错误。

### 3. 何时向第三方发送信息

**云端 AI 模型：** 当用户配置并调用云端 AI 服务时，Duty-Agent 会向用户设定的模型 API 地址发送生成排班所需的消息、提示词和相关参数，并向该地址提交相应的 API 凭据。模型服务商会收到这些请求，可能根据其自身条款、隐私政策、保留期限及所在地区处理数据。

**Duty-Agent 的正常内置排班流程不会直接将完整花名册文件（`roster.csv`）或姓名与 ID 的映射表发送给云端 AI。** 程序在生成模型请求前，将排班指令和规则中与本地花名册匹配的姓名替换为数字 ID；模型返回数字 ID 后，再在用户设备上恢复对应姓名。这是有意设计的本地匿名化边界。

**匿名化仍有实现边界：** 当前代码合并进提示词的「上次运行备注 / 用户备注」未统一经过同一姓名替换函数，部分其他自定义文本（例如区域名称）也可能含识别信息。如果用户在这些字段填写真实姓名，相关文字存在随模型请求发送的可能性。此项是代码审查发现的潜在风险，**不表示已经发生过学生姓名外传**。请避免在备注及其他自由文本中填写不必要的敏感信息。

**本地模型/离线模式：** 当用户选择真正运行在本机的模型端点或无需模型的离线算法，并且未启用其他外部集成时，排班推理可以不向云端模型发送请求。是否发生网络传输还取决于所配置端点和使用的其他工具。

**ClassIsland、API、MCP 与其他工具：** 用户主动安装或启用集成后，相关组件或获授权的客户端可以访问为执行其功能所需的排班、配置或状态信息。对于用户自行连接的外部 AI、网络服务或自动化工具，其后续数据使用受相应工具及服务的政策约束。

项目维护者不会仅因用户在本机安装或使用 Duty-Agent，就自动收到上述名单或排班数据库。本政策不代表第三方模型提供商承诺不留存或不训练数据。

### 4. 存储地点与保留时间

应用默认将后端配置、名单及排班状态等保存在本机，常见路径为 `%LOCALAPPDATA%\DutyAgent\data\`（例如 `config.json`、`roster.csv`、`state.json` 和 `host-config.json`）。日志、WebView2 用户数据与 ClassIsland 插件配置可能位于其他本地目录。MSIX 安装、便携模式或用户调整配置后，实际路径可能不同。

名单、排班记录和配置通常保留至用户修改或删除它们；卸载程序**不一定**删除所有本地数据、缓存、日志或备份。

Python 后端诊断日志实施约 **14 天**的过期清理，通常在启动及跨日写入时执行；该期限不适用于全部客户端日志、用户数据或由用户/第三方制作的备份。清理可能因程序未启动、文件权限或文件被占用而延迟。

### 5. 安全措施与限制

通过 Windows 客户端保存的模型 API Key 支持使用当前 Windows 用户范围的 DPAPI 加密，然后再保存至配置文件；部分旧配置、外部工具或环境变量提供的密钥可能采用其他保存方式。**这不表示所有本地文件都经过加密。**

默认后端服务监听本机回环地址（`127.0.0.1`）；受保护的 API/MCP 操作使用访问令牌控制。用户应保护自己的 Windows 账户、访问令牌、API Key 和数据文件，不应将服务暴露到不可信网络，也应优先使用 HTTPS 模型服务端点。任何安全措施都不能保证绝对安全。

### 6. 用户选择、访问与删除

用户可以在应用中查看或修改名单、配置及排班数据；可以删除不再需要的信息、停止使用云端 AI、改用本地模型或离线方案，或关闭不需要的 MCP 与其他集成。

如需清除本地留存数据，请先退出 Duty-Agent 及相关后端/桥接进程，按需备份，然后检查并删除对应的 Duty-Agent 数据目录、日志、WebView2 数据及 ClassIsland 插件数据。删除前请确认路径，避免误删 ClassIsland 的其他配置。卸载本身不保证清除这些文件。对已发送给第三方模型或工具的内容，需向相应服务商依据其政策提出访问或删除请求。

Duty-Agent 开发者通常无法直接读取或删除仅保存在用户设备上的数据。若您管理包含其他人的名单（特别是未成年人的名单），您应具有合法、适当的使用依据，并告知相关人员，不应向未经授权的第三方共享这些信息。

### 7. 未成年人及敏感信息

本产品可用于校园场景，因此排班名单可能涉及未成年人。请由有权限的教师、管理员或其他负责人管理这些资料，依据适用法律和所在组织的规则取得必要授权或同意。除排班必需的信息外，不应输入身份证号、联系方式、健康状况等敏感信息，也不应将其发送至云端模型。

### 8. 本政策的变化

产品功能、第三方服务或数据处理方式发生重大变化时，项目维护者会更新本政策及生效日期。请以公开发布的最新版本为准。

### 9. 联系方式

开发者与隐私事项联系邮箱：**[helemazuba@gmail.com](mailto:helemazuba@gmail.com)**。

一般功能建议或公开问题可以使用 [Duty-Agent GitHub Issues](https://github.com/helemazuba-boop/Duty-Agent/issues/new)。请勿在公开 Issue 中粘贴真实花名册、个人信息、访问令牌、API Key 或含敏感信息的日志。发送邮件时也请尽量脱敏，并避免提供不必要的学生个人信息。

---

## English

### 1. About Duty-Agent

Duty-Agent is a Windows scheduling application for managing rosters, duty assignments, absences, substitutions, and reminders. It runs a local client and Python backend, can integrate with ClassIsland, and optionally uses an AI model endpoint chosen by the user.

No Duty-Agent cloud account is required. Ordinary roster and schedule management is primarily local. **When a user runs scheduling through a cloud AI provider, the application transmits relevant prompts and request data to that provider.** Optional API, MCP and third-party integrations may also exchange data when enabled by the user.

### 2. Information processed and why

To provide its functions, Duty-Agent may process:

- **Roster and scheduling information:** names, member IDs, enabled status, dates, assignments, duty areas, rotation state, absence and substitution records, and notes.
- **Instructions and generated content:** user-provided scheduling instructions, rules, preferences, prompts, AI responses and generated schedules.
- **Settings and credentials:** selected models and modes, provider endpoint URLs, API keys, local service ports, access tokens and integration configuration.
- **Diagnostics:** timestamps, request trace identifiers, errors, component status and local logs. Exception messages or user input may incidentally contain personal information.
- **Local WebView2 data:** caches or browser runtime data created by the embedded interface.

This information is processed to manage rosters, generate and display schedules, retain scheduling state, send reminders, enable integrations selected by users, and troubleshoot faults.

### 3. Disclosures and third-party services

**Cloud AI providers:** When you configure and use a remote AI service, the app submits relevant prompts, messages, parameters and the associated API credential to the endpoint you selected. That provider may process and retain requests under its own privacy policy, terms, and jurisdiction.

**Duty-Agent's normal built-in scheduling flow does not directly upload the complete roster file (`roster.csv`) or the name-to-ID mapping to a cloud AI provider.** When constructing model prompts, it replaces names matching the local roster in scheduling instructions and rules with numeric member IDs. The numeric IDs are mapped back to names locally after the model responds.

**Limitations:** Previously saved run notes and user notes included in prompts are not consistently passed through the same name-replacement function in the current implementation. Other user-defined text, such as area names, may also contain identifying information. Consequently, names entered into those fields could be transmitted in model requests. This is a potential code-path limitation, **not evidence that any real student names have been transmitted**. Avoid entering unnecessary personal or sensitive details in free-text fields.

**Local and offline processing:** A genuinely local model endpoint or the built-in model-free scheduling mode can avoid sending scheduling prompts to a cloud AI service, provided no other external integration transmits them. Network behavior also depends on your chosen configuration.

**User-enabled integrations:** ClassIsland, authorized API/MCP clients and other connected tools may receive relevant scheduling or configuration data as needed for the operations you enable. Any onward use by third-party tools or providers is governed by their own policies.

Installing or using Duty-Agent locally does not, by itself, send roster databases to the project's maintainer. Duty-Agent cannot promise that a third-party AI provider will refrain from retaining requests or using them for model improvement.

### 4. Local storage and retention

Configuration, roster and scheduling state are normally stored on your device, typically under `%LOCALAPPDATA%\DutyAgent\data\` (including `config.json`, `roster.csv`, `state.json` and `host-config.json`). Logs, WebView2 data, ClassIsland configuration, portable installations and MSIX installations may use other directories.

Roster data, state and configuration ordinarily remain until you modify or remove them. **Uninstalling may not delete all local data, caches, logs or backups.**

The Python backend implements a roughly **14-day** cleanup policy for its diagnostic logs, generally applied at startup and on the first log write of a new day. This is not a universal retention period for client logs, saved roster data, third-party services or backups. Cleanup may be delayed by access or file-locking issues.

### 5. Security

API keys saved through the Windows client support Windows Data Protection API (DPAPI) encryption scoped to the current Windows user. Older configurations, external clients or environment variables may handle credentials differently. **Not every locally stored file is encrypted.**

The backend normally listens on the local loopback address (`127.0.0.1`), and protected API/MCP operations require access tokens. Protect your Windows account and credentials, avoid exposing the service to untrusted networks, and prefer HTTPS for remote model endpoints. No security mechanism is infallible.

### 6. Your choices, access and deletion

You may view or edit information in the application, remove entries, stop using a remote AI provider, choose a local/offline mode, and disable optional integrations.

To remove local data, exit Duty-Agent and its backend/bridge processes, make any backup you need, and carefully delete the relevant Duty-Agent data, logs, WebView2 profile and ClassIsland plugin files. Confirm directories before deleting to avoid removing unrelated ClassIsland settings. Uninstalling alone does not guarantee deletion. For requests already submitted to a third-party AI provider or integration, contact that provider to exercise applicable access or deletion rights.

The project maintainer generally cannot access or delete files that remain solely on your device. If you manage other people's roster information, including children's data, ensure you have an appropriate lawful basis and permission, and do not disclose their information to unauthorized parties.

### 7. Children and sensitive data

Duty-Agent may be used in schools and may therefore process information about minors. Authorized teachers or administrators should follow applicable laws and institutional requirements and obtain any required permission or consent. Do not enter or transmit identity document numbers, contact details, health information or other sensitive details unless strictly necessary and lawfully permitted.

### 8. Changes to this policy

The maintainer may update this policy and its effective date when features, integrations or data practices change. The latest published version applies.

### 9. Contact

For privacy inquiries, contact the developer at **[helemazuba@gmail.com](mailto:helemazuba@gmail.com)**.

For general issues, you may use [Duty-Agent GitHub Issues](https://github.com/helemazuba-boop/Duty-Agent/issues/new). Do **not** post personal rosters, private contact details, access tokens, API keys or sensitive logs in public issues. Minimize or redact any personal information you send by email, especially student data.
