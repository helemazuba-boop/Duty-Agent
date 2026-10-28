# Duty-Agent 隐私政策 / Privacy Policy

> **正式发布地址 / Canonical URL: <https://legal.helema.cn/>**
> 本文是同一份内容的仓库副本；对外提供（商店列表、软件内「设置 → 关于」）一律用上面的地址。
> 修改政策时请同步更新托管页，否则对外展示的仍是旧条款。

**更新日期 / Last updated: 2026-10-13**  
**适用产品 / Product: Duty-Agent**  
**隐私联系邮箱 / Privacy contact: <helemazuba@gmail.com>**

[简体中文](#简体中文) | [English](#english)

---

## 简体中文

### 1. 适用范围与产品简介

欢迎使用 Duty-Agent。本隐私政策说明 Duty-Agent 在提供排班管理服务时如何处理信息，以及您可以作出的选择。

Duty-Agent 是一款以本地运行为主的 Windows 排班工具，可管理人员名单、排班规则、请假与替班安排、排班结果及提醒。其组成包括桌面客户端、本地排班引擎，以及可选的 ClassIsland 桥接和 API/MCP 集成。

使用 Duty-Agent **不需要注册由 Duty-Agent 开发者运营的云端账户**。通常，名单、排班状态及配置由您自己的设备保存。您可以根据需要选择无需模型的离线排班、本地模型，或自行配置在线模型服务。

本政策适用于 Duty-Agent 软件本身的数据处理。对于您选择连接的独立模型服务商及第三方工具，其数据处理还受相应服务商的政策约束。

### 2. 本软件使用的信息

为提供您启用的功能，Duty-Agent 在本机处理以下信息：

- **名单与排班信息：** 您录入的姓名、人员编号及启用状态、日期、值日区域、任务分配、请假、替班、历史排班状态和备注。
- **用户输入及运行结果：** 自然语言排班指令、用户定义的规则、偏好设置、模型输出以及生成的排班安排。
- **服务配置：** 您选择的执行模式、模型名称、模型服务地址、访问凭据及集成设置。
- **运行诊断信息：** 用于故障排查与运行维护的本地日志，例如时间、组件状态和错误信息。
- **界面运行数据：** 为正常显示桌面内嵌界面而产生的本地缓存和必要设置。

这些信息用于管理人员、计算和保存排班结果、显示提醒、连接您启用的服务，以及协助排查软件运行问题。Duty-Agent 不要求用户为普通排班功能提供身份证号码、健康信息或其他与排班无关的敏感资料。

### 3. 在线模型与第三方服务

#### 3.1 离线排班及本地模型

当您使用无需模型的离线排班功能时，排班计算在本机执行，不需要将排班提示词发送至在线 AI 服务。

当您连接实际运行在本机的模型时，请求由该本地模型处理。是否涉及其他网络通信取决于您所选择的模型程序及额外启用的集成。

#### 3.2 用户配置的在线模型

当您配置在线模型服务并发起相关操作（包括模型连接测试及 AI 排班）时，Duty-Agent 会向您指定的模型服务地址发送完成相应请求所需的信息，例如模型名称、排班指令、日期、人员编号、排班规则、区域和与任务有关的上下文；同时按照该服务的要求使用相应访问凭据。

Duty-Agent 的排班处理采用**人员编号与本地姓名对应**的方式：在主要排班指令处理过程中，将与本地名单匹配的姓名转换为编号，并在本机将排班结果还原为人员姓名。**完整的本地花名册及姓名与编号的对应表不是内置模型排班请求所需的上传内容。**

您自行输入的规则、备注、区域名称或其他自由文本可能用于构建模型请求。请避免在这些内容中填写完成排班任务不需要的个人信息，尤其是其他人的敏感信息。

在线模型由您选择的服务商提供。该服务商对其接收的数据、存储期限及其他处理活动承担相应责任，并适用其隐私政策和服务条款。Duty-Agent 开发者无法代替该服务商管理其独立保存的请求数据。

#### 3.3 可选集成

如您启用 ClassIsland 桥接、API、MCP 或其他连接工具，Duty-Agent 可按照相应功能及授权向这些组件提供必要的排班信息或状态。ClassIsland 等本机组件可以在设备上显示人员姓名及排班安排。

您自行连接的外部工具或服务可能依其权限读取或处理相关数据。建议仅连接可信工具，并按需要配置和关闭相应集成。

在常规本地使用过程中，**Duty-Agent 开发者不会仅因您安装、管理名单或执行本地排班，就自动接收到您设备中的完整名单或排班数据库**。模型服务商接收的在线请求不等于将这些数据提交给 Duty-Agent 开发者。

### 4. 本地存储、保留与安全

Duty-Agent 默认把名单、配置、排班状态和运行数据保存在您的设备上。不同安装形式及组件可能使用不同的应用数据、日志或缓存目录。

名单和排班记录通常保留到您修改或删除相应数据。本地后端诊断日志设有约 14 天的过期清理机制；其他本地文件、客户端日志、系统缓存和用户自行保留的备份并不统一适用该期限。卸载应用不一定同时移除全部用户数据和备份。

Duty-Agent 对模型凭据和本地服务访问采取适当的保护措施：**保存的模型 API Key 会先用 Windows DPAPI（当前用户作用域）加密，再写入本地配置文件，不以明文落盘**；换一台电脑或换一个 Windows 账户后，该密文无法被解开（此时需要重新填写 Key）。本地 API 等受保护功能使用访问控制。您也应保护设备账户、模型密钥与授权令牌，避免向不可信的服务或人员提供访问权限。

本软件的 AI 排班结果由模型生成，**可能不准确、遗漏或不符合您的实际规则**；结果会经过本机校验，并且您可以在排班页面查看和手工修改。如遇到不当的 AI 输出，可通过软件内「设置 → 关于」页面的反馈渠道告知开发者。

### 5. 您的选择与数据管理

您可以根据所使用的功能：

- 在本机查看、修改或删除名单、排班信息及设置；
- 使用「设置 → 系统 → 数据重置」一次性清空名单、排班与配置（可选同时归档备份）；
- 选择离线排班或本地模型，或者更换、停止使用在线模型服务；
- 配置或关闭可选的 ClassIsland、API、MCP 等集成；
- **开机自启默认关闭**，仅在您于首次向导或「设置 → 系统」中主动开启后才会写入系统启动项，并可随时关闭；
- 备份或清理相关应用数据与日志。

隐私政策的完整内容可在软件内「设置 → 关于」页面随时打开。

如需彻底清理本地数据，请先退出 Duty-Agent 及相关组件，确认并移除相应的数据、缓存和备份文件。请注意，应用卸载与清除用户数据并非始终是同一操作。

您通过第三方模型或工具提交的内容，如需访问、更正或删除，应同时参考该服务商提供的处理方式与相应政策。Duty-Agent 开发者一般无法直接访问或删除仅存储在您的设备或第三方服务商系统中的内容。

如果您在名单中录入其他人的信息，应确保您有适当的使用依据与权限；对于未成年人信息，请遵守适用的法律及学校或组织的相关要求，并避免将不必要的个人信息提交给外部服务。

### 6. 隐私政策更新与联系我们

当产品功能或数据处理方式发生重要变化时，我们会更新本政策并注明更新日期。

如果您对 Duty-Agent 的隐私处理有疑问，可通过 **<helemazuba@gmail.com>** 联系项目维护者。一般产品问题亦可通过 [Duty-Agent GitHub 仓库](https://github.com/helemazuba-boop/Duty-Agent) 提出。

请勿在公开讨论区张贴真实花名册、模型密钥、访问令牌或其他敏感资料；联系支持时，建议先对诊断资料进行必要的脱敏。

---

## English

### 1. Scope and overview

This Privacy Policy explains how Duty-Agent processes information when providing its scheduling features and what choices you have.

Duty-Agent is a primarily local Windows scheduling application. It manages rosters, scheduling rules, absences, substitutions, schedules, and reminders. Its components include the desktop client, a local scheduling engine, and optional ClassIsland, API, and MCP integrations.

**A cloud account operated by the Duty-Agent developer is not required.** Rosters, schedule state, and settings are ordinarily stored on your device. You may choose model-free offline scheduling, a locally hosted model, or an online model endpoint that you configure.

Third-party model providers and connected tools have their own data handling practices and policies.

### 2. Information used by the application

Duty-Agent processes the following information on your device to deliver the features you use:

- **Roster and schedule information:** Names, member IDs, active status, dates, assigned areas and tasks, absences, substitutions, historical schedule state, and notes.
- **Inputs and results:** Natural-language scheduling instructions, custom rules, preferences, model responses, and generated schedules.
- **Service configuration:** Scheduling modes, selected models, provider endpoint addresses, credentials, and integration settings.
- **Diagnostics:** Local operational logs, such as timestamps, component status, and errors used for troubleshooting.
- **Interface data:** Local caches and settings needed by the embedded desktop interface.

This information is used to manage assignments, calculate and save schedules, display reminders, connect enabled services, and diagnose operational issues. Ordinary scheduling does not require identity document numbers, medical information, or other unrelated sensitive details.

### 3. AI processing and third-party connections

#### 3.1 Offline scheduling and local models

Model-free offline scheduling runs on your device and does not require prompts to be sent to an online AI provider.

When you select a model actually hosted on your own device, model requests are processed through that local endpoint. Other network behavior depends on the model software and optional integrations you choose.

#### 3.2 Online model endpoints you configure

When you configure an online model and initiate a related operation, such as a connection test or AI scheduling, Duty-Agent sends the information necessary for that request to the endpoint you selected. This can include model identifiers, scheduling instructions, dates, member IDs, rules, areas, and task context. The corresponding provider credential is used to authenticate requests as required.

Duty-Agent uses **member IDs with a local name mapping** for scheduling. Known roster names in primary scheduling instructions are converted to IDs, and generated schedules are mapped back to names locally. The complete local roster file and name-to-ID mapping are not required uploads for built-in model scheduling.

Rules, notes, area labels, and other free-form text that you enter may form part of a model request. Avoid placing unnecessary personal information about yourself or others, especially sensitive information, in such fields.

The online service you select independently governs its handling and retention of requests under its own terms and privacy policy. The Duty-Agent developer cannot manage information separately retained by that provider.

#### 3.3 Optional integrations

If you enable ClassIsland, API, MCP, or other connected tools, relevant schedule information and status may be made available to those components according to their functionality and access permissions. Local integrations, including ClassIsland, may display names and assignments on your device.

External tools you connect may process information under their own policies. Connect only trusted tools and disable integrations you no longer need.

**Installing Duty-Agent, managing a local roster, or running offline schedules does not, by itself, transfer your complete roster or schedule database to the Duty-Agent developer.** Requests sent to a model provider are separate from data received by the Duty-Agent developer.

### 4. Storage, retention, and security

Duty-Agent ordinarily stores roster information, configuration, schedule state, and operational data on your device. Storage locations may vary by installation format and component.

Roster and schedule records generally remain until you change or delete them. Local backend diagnostic logs have an approximately 14-day expiry cleanup policy. Other client logs, files, system caches, and user-created backups are not necessarily subject to that same period. Uninstalling the application may not remove all user data and backups.

Duty-Agent uses appropriate safeguards for model credentials and local service access: **saved model API keys are encrypted with Windows DPAPI (current-user scope) before being written to the local configuration file, so they are never stored in plain text**. The ciphertext cannot be decrypted on another machine or under another Windows account (the key must then be entered again). Protected local API features use access controls. You should also secure your device account, model credentials, and authorization tokens.

AI schedules are generated by a model and **may be inaccurate, incomplete, or inconsistent with your rules**. Results pass through local validation, and you can review and edit them on the schedule page. To report inappropriate AI output, use the feedback channel under Settings → About in the app.

### 5. Your choices and controls

Depending on the features you use, you can:

- View, edit, or remove local roster, schedule, and settings data;
- Clear the roster, schedules, and configuration at once via Settings → System → Data reset (with an optional archived backup);
- Use offline scheduling or a local model, or change or stop using an online model provider;
- Configure or disable optional ClassIsland, API, MCP, and other integrations;
- **Start with Windows is off by default**; a startup entry is created only after you enable it in the first-run wizard or under Settings → System, and you can turn it off at any time;
- Back up or clear relevant application data and local logs.

The full privacy policy is always available in the app under Settings → About.

To clear local data, first close Duty-Agent and related components, then carefully identify and delete applicable data, caches, and backups. Uninstalling the application and deleting stored user data are not always the same operation.


For information already provided to a third-party model provider or tool, consult that provider's procedures and policies for access, correction, or deletion. The Duty-Agent developer generally cannot directly access or delete information that remains solely on your device or in an independent provider's systems.

If you manage other individuals' information, ensure you have an appropriate basis and permission to do so. When handling minors' information, follow applicable law and relevant school or organizational policies, and avoid sending unnecessary personal information to external services.

### 6. Updates and contact

We may update this policy when the product or its data practices materially change. The updated policy will state its revision date.

For privacy questions, contact the maintainer at **[helemazuba@gmail.com](mailto:helemazuba@gmail.com)**. For general product questions, see the [Duty-Agent GitHub repository](https://github.com/helemazuba-boop/Duty-Agent).

Do not post private rosters, model credentials, access tokens, or other sensitive information in public discussions. Review and redact diagnostic information before sharing it for support.
