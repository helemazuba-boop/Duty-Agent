# 自然语言与结构化输入冲突问题说明（事实版）

日期：2026-02-15  
项目：Duty-Agent  
文档性质：问题说明文档（仅记录背景、现状、现象与影响）

## 1. 文档目的

本文用于让不了解 Duty-Agent 的读者快速理解：

1. 这个项目是什么。  
2. 排班输入在系统中如何流转。  
3. 当前“自然语言输入”与“结构化输入”为什么会出现冲突。  
4. 这些冲突在实际使用中会表现成什么问题。  

说明：本文不包含改造方案、优化方向或实施计划。

## 2. 项目背景

Duty-Agent 是 ClassIsland 的值日排班插件。它通过 C# 宿主层 + Python 核心脚本完成排班。

系统同时存在多种输入入口：

1. 主设置页（普通用户）：文本输入排班指令、长期规则、配置项。  
2. 本地 REST 接口：`/api/v1/schedule/overwrite`。  
3. MCP 工具接口：`schedule_overwrite`、`config_update_settings`、`roster_import_students`。

这意味着同一个“业务意图”（例如周末是否排班）可能从不同通道进入，并以不同表达方式（自然语言或结构化字段）传入系统。

## 3. 当前输入模型与执行链

## 3.1 自然语言输入（文本）

1. 主设置页文本控件：
   - `instruction` 输入框：`Views/SettingPages/DutyMainSettingsPage.axaml:52`  
   - `duty_rule` 输入框：`Views/SettingPages/DutyMainSettingsPage.axaml:111`
2. Python 提示词构建将 `instruction` 与 `duty_rule` 作为文本拼接：
   - `Assets_Duty/core.py:300`  
   - `Assets_Duty/core.py:331`  
   - `Assets_Duty/core.py:345`
3. MCP 排班工具描述为“natural-language instruction”：
   - `Services/DutyLocalPreviewHostedService.cs:1581`

## 3.2 结构化输入（字段）

1. 后端向 Python 传递结构化字段：
   - `instruction`：`Services/DutyBackendService.cs:304`  
   - `skip_weekends`：`Services/DutyBackendService.cs:309`  
   - `duty_rule`：`Services/DutyBackendService.cs:310`
2. MCP 配置工具直接暴露结构化字段（包含 `skip_weekends`、`duty_rule`）：
   - 工具定义：`Services/DutyLocalPreviewHostedService.cs:1648`  
   - 字段：`Services/DutyLocalPreviewHostedService.cs:1666`、`Services/DutyLocalPreviewHostedService.cs:1667`

## 3.3 输入合并规则（核心脚本）

`core.py` 会把 root 与 `config` 合并，并保留 root 的 `instruction`：

1. 合并函数：`Assets_Duty/core.py:635`  
2. 保留 root instruction：`Assets_Duty/core.py:645`、`Assets_Duty/core.py:646`

若最终 instruction 为空，会写入默认文本：

1. 读取 instruction：`Assets_Duty/core.py:672`  
2. 空值处理：`Assets_Duty/core.py:673`、`Assets_Duty/core.py:674`

## 3.4 日期生成与排班落盘

1. 日期由程序端生成，不是由 LLM 返回具体日期：
   - 日期生成函数：`Assets_Duty/core.py:429`
2. 是否跳过周末由结构化布尔值控制：
   - 读取 `skip_weekends`：`Assets_Duty/core.py:700`  
   - 生成目标日期：`Assets_Duty/core.py:743`
3. 排班合并落盘由 `apply_mode` 控制：
   - `Assets_Duty/core.py:611`

## 4. 观测到的冲突现象

## 4.1 权威来源冲突：文本规则 vs 结构化字段

现象：

1. 用户可在 `duty_rule` 文本中写“周六安排/不安排”。  
2. 系统又同时读取结构化 `skip_weekends`。  
3. 最终日期由程序的 `skip_weekends` 路径决定（`Assets_Duty/core.py:429`, `Assets_Duty/core.py:700`, `Assets_Duty/core.py:743`）。

表现：文本表达与最终排班结果可能不一致。

## 4.2 输出契约冲突：用户常按“具体日期”思考，模型契约按“周几”输出

现象：

1. Prompt 明确要求 day 字段使用 `Mon..Sun`：`Assets_Duty/core.py:327`。  
2. 用户通常会提出“某天/某周五/某个日期”的需求。  
3. 当前流程里“具体日期”由程序后置生成，不直接来自模型输出。

表现：用户对“模型已经理解日期”的感知与系统真实执行链不一致。

## 4.3 接口行为冲突：UI 与 REST/MCP 的空指令处理不同

现象：

1. 后端 `RunCoreAgentWithMessage` 支持空指令回退：`Services/DutyBackendService.cs:265`、`Services/DutyBackendService.cs:266`。  
2. REST 覆盖排班要求 instruction 非空：`Services/DutyLocalPreviewHostedService.cs:281`、`Services/DutyLocalPreviewHostedService.cs:294`。  
3. MCP `schedule_overwrite` 同样要求 instruction 非空：`Services/DutyLocalPreviewHostedService.cs:775`。

表现：同一意图在不同入口会有不同结果（一个可运行，一个直接报错）。

## 4.4 粒度冲突：业务需求细粒度，结构化字段较粗

现象：

1. 配置中存在 `skip_weekends` 这类二元开关。  
2. 实际需求常是“周五 6 人、周六不排、其他 2 人”这类按星期差异策略。  
3. 这类策略往往只能写在自然语言里，缺少等价结构化承载。

表现：系统执行依赖“文本解释 + 后处理补偿”，稳定性与可验证性下降。

## 4.5 可解释性冲突：冲突解决过程不可见

现象：

1. 结果回传中没有标准化“冲突裁决记录”（例如哪个规则覆盖了哪个规则）。  
2. 当文本意图与结构化字段不一致时，外部很难直接从结果判断“为何这样排”。

表现：排查成本高，跨端复现困难。

## 5. 可复现实例

## 实例 A：周末规则冲突

输入：

1. `duty_rule` 文本：写明“周六安排值日”。  
2. 结构化配置：`skip_weekends=true`。

执行链：

1. 文本进入 prompt（`Assets_Duty/core.py:331`）。  
2. 日期生成仍按 `skip_weekends` 跳过周末（`Assets_Duty/core.py:700`, `Assets_Duty/core.py:743`）。

结果：周六不会被生成到排班日期中。

## 实例 B：空指令跨入口不一致

输入：

1. UI 触发空 instruction。  
2. REST/MCP 触发空 instruction。

结果：

1. UI 路径可回退默认 instruction（`Services/DutyBackendService.cs:265`）。  
2. REST/MCP 路径返回 validation 错误（`Services/DutyLocalPreviewHostedService.cs:294`, `Services/DutyLocalPreviewHostedService.cs:775`）。

## 实例 C：嵌套 config 与 root 字段冲突

输入：

1. root 中有 `instruction=A`。  
2. `config` 中有 `instruction=B`。

执行：

1. 合并后保留 root instruction（`Assets_Duty/core.py:645`, `Assets_Duty/core.py:646`）。

补充：该行为有测试覆盖：`Assets_Duty/test_core.py:28`, `Assets_Duty/test_core.py:52`。

## 6. 影响范围

受影响对象：

1. 终端用户：同一需求在不同入口出现不同结果，理解成本增加。  
2. 维护人员：缺少冲突裁决可见信息，排障需要跨多文件追链。  
3. 接口调用方（MCP/REST）：难以建立稳定的“输入->输出”预期。

受影响模块：

1. UI 输入层：`Views/SettingPages/DutyMainSettingsPage.axaml`、`Views/SettingPages/DutyMainSettingsPage.axaml.cs`。  
2. 服务编排层：`Services/DutyBackendService.cs`、`Services/DutyLocalPreviewHostedService.cs`。  
3. 核心执行层：`Assets_Duty/core.py`。  
4. 单测覆盖：`Assets_Duty/test_core.py`（仅覆盖部分合并与周末逻辑，不覆盖跨入口一致性）。

## 7. 现状结论

当前系统同时接受自然语言与结构化输入，但“同一业务语义”的统一权威表达不存在。  
因此在输入合并、日期生成、入口行为和结果解释上，出现了可复现的冲突现象。  
这些问题已经体现在现有代码路径中，并非单一页面文案或单个字段问题。
