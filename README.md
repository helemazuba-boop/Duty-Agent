# Duty-Agent (v2.4)

> **"Fairness is King."** — The Humanity Protocol

`Duty-Agent` 是一个面向 **[ClassIsland](https://github.com/ClassIsland/ClassIsland)** 的下一代智能排班插件。

**为什么选择 AI 排班？**

传统的排班算法（如轮询、随机）是**冷冰冰的数学**——它们不懂“张三下周二要打篮球”，也不懂“李四昨天请假了，今天该补上”。管理员被迫在复杂的 Excel 表格和僵硬的规则配置中挣扎。

**Duty-Agent 是不同的。** 它不仅仅能安排值日，更能像人类管理员一样，理解自然语言指令，处理复杂的请假、调休、补班逻辑，并确保长期的公平性。

---

## ⚔️ AI vs 传统算法

| 特性 | 🟢 传统算法 / Excel | 🚀 Duty-Agent (AI 驱动) |
| :--- | :--- | :--- |
| **指令输入** | 僵硬的表单 (开始日期, 结束日期, 人数) | **自然语言** ("下周三个人，张三请假") |
| **复杂约束** | 难以配置 ("周二不排篮球队", "女生不排重活") | **一句话搞定** (直接告诉 AI 规则即可) |
| **突发调整** | 需要手动修改数据库/表格 | **智能重排** ("重新排，把王五换成赵六") |
| **公平性** | 机械轮询，容易因请假而乱序 | **Debt机制** (记得谁欠了班，主动补上) |
| **维护成本** | 随着规则增加，代码/配置指数级复杂 | **零配置** (规则写在 Prompt 里，无需改代码) |

---

## ✨ 核心特性

### 1. The Humanity Protocol (人性化协议)
传统的排班算法只在乎“填坑”，而 Duty-Agent 在乎“公平”。
- **Fairness First**: 如果某人因病或活动（如篮球队训练）跳过了值日，系统会将其标记为 **Debt (欠债)**。
- **Debt Repayment**: 在下一次可行的时间（如明天），系统会**优先**安排欠债的人补班 (Backfill)，而不是无脑启用新人。
- **Flow Control**: 即使欠债累累，系统也会智能控制每日流量，避免单日安排过多人员造成拥挤。

### 2. The "Two-Queue" Logic (双队列机制)
为了解决连续 ID 排班中的指针混乱问题，Agent 内部维护两套逻辑队列：
- **Debt Queue (优先)**: 存储因故暂缓值日的人员 ID。
- **Main Pointer (进度)**: 记录全局轮询进度，**只进不退**。
- *效果*: 即使频繁插队补班，全局轮询进度也不会丢失。

### 3. AI Memory (长时记忆/Context Persistence)
Duty-Agent 拥有跨会话的记忆能力。
- **Stateful**: 今天的排班结束后，AI 会在“小本本” (`state.json`) 上记下：“ID 12 还没还债，下次记得”。
- **Recall**: 下次启动时，这段记忆会自动注入 Prompt，确保公平性跨越时间。

### 4. 显式日期驱动 (Explicit Date Driver)
摒弃了传统的“生成 N 天”逻辑。用户只需说：“排下周”，AI 会自行理解“下周”的具体日期范围 (e.g., 2026-10-23 ~ 10-27)，并精准生成对应的 JSON。

### 5. Open MCP Support (开放模型上下文协议)
Duty-Agent 实现了 **Streamable HTTP MCP** 标准。
这意味着它不仅是一个插件，更是一个 **Tool Protiver**。外部的高级 AI Agent（如 Cursor, Windsurf）可以通过 MCP 连接到本插件，直接读取排班数据、修改配置，甚至执行排班操作。

### 6. Privacy First (隐私匿名化)
我们深知数据的敏感性。
- **Anonymization**: 在发送给 LLM 之前，所有人名都会被替换为无意义的 ID (e.g., "张三" -> "15")。
- **Restoration**: LLM 处理完毕后，Python 脚本在本地将 ID 还原为人名。
- **Zero Leakage**: 您的学生/员工名单永远不会以明文形式出现在 LLM 的服务端日志中。

### 7. Enterprise-Grade Security (企业级安全)
API Key 是您最重要的资产，我们采用金融级的加密方案：
- **AES-256-CBC + HMAC-SHA256**: 双重加密与完整性校验。
- **Physical Binding**: 密钥与本机**物理网卡 MAC 地址**绑定。即使配置文件被窃取，在其他机器上也无法解密。
- **Memory Protection**: 密钥仅在通过 Stdin 传输给 Python 核心时短暂存在，使用后立即从内存中擦除。

---

## 🛠 功能概览

| 功能 | 说明 |
|---|---|
| **AI 智能排班** | LLM 驱动，支持自然语言指令 (e.g. "下周三个人，张三请假") |
| **多区域支持** | 内置 `教室 / 清洁区`，支持无限扩展区域 |
| **名单管理** | 自动分配连续 ID，支持启用/禁用状态 |
| **MCP 协议** | 支持 Streamable HTTP MCP，可被外部 Agent (如 Cursor) 调用 |
| **Web 设置页** | 现代化的 WebView2 配置界面 |
| **安全加密** | API Key 绑定物理网卡 MAC，采用 AES-256 + HMAC 保护 |

---

## 🏗 技术架构

### 宿主-代理 (Host-Agent) 模式
- **Host (C#)**: .NET 8 (WPF)
    - 负责 UI、系统通知、进程管理、IPC 通信。
    - 仅仅是“传话筒”，不干涉排班逻辑。
- **Agent (Python)**: `Assets_Duty/core.py`
    - **独立大脑**: 负责 Prompt 构建、LLM 调用、结果解析、状态管理。
    - **自包含**: 所有排班算法与兜底逻辑均在 Python 侧闭环。

### 安全设计
- **API Key 零落地**: Key 通过 Stdin 管道传输给 Python，磁盘上的配置文件仅存密文。
- **MAC 绑定**: 密文仅能在加密它的物理机上解密，防拷贝。

---

## 🚀 快速开始

### 1. 安装
下载最新 Release 包，解压至 ClassIsland 的 `data/Plugins/Duty-Agent` 目录。

### 2. 配置
1. 在 ClassIsland 设置中找到 `Duty Agent`。
2. 输入兼容 OpenAI 格式的 API Key 和 Base URL (推荐 SiliconFlow/DeepSeek)。
3. 导入学生名单 (`roster.csv`) 或手动添加。

### 3. 使用
- **点击“自动排班”**: 观察 AI 的思考过程 (`thinking_trace`) 和最终结果。
- **自然语言指令**: 尝试输入 "下周一只要 1 个人，因为大扫除"。

---

## 📂 目录结构

```
Duty-Agent/
├── Services/                  # C# 宿主服务
│   ├── DutyBackendService.cs  # 核心业务桥梁
│   └── SecurityHelper.cs      # 加密模块
├── Assets_Duty/               # Python Agent 核心
│   ├── core.py                # 排班逻辑主程序 (The Brain)
│   ├── test_core.py           # 单元测试 (含 v2.4 新特性测试)
│   └── data/                  # 运行时状态 (state.json 存储记忆)
├── Views/                     # UI 层
└── README.md
```

## 📝 许可
MIT License.
