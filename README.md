# Duty-Agent

`Duty-Agent` 是一个面向 **[ClassIsland](https://github.com/ClassIsland/ClassIsland)** 的 AI 值日排班插件。  
通过 WebView2 设置页 + Python 核心排班脚本，完成名单管理、区域管理、自动/手动排班与通知联动。

## 功能概览

| 功能 | 说明 |
|---|---|
| AI 排班 | 调用 LLM 根据名单和规则自动生成值日安排 |
| 多区域支持 | 内置 `教室 / 清洁区`，支持自定义区域和每区域人数 |
| 名单管理 | 增删学生、自动分配 ID、启用/停用状态 |
| 定时自动排班 | 可配置每周指定日期和时间自动触发排班 |
| 值日提醒 | 可配置每日提醒时间和模板，联动宿主通知系统 |
| 组件展示 | ClassIsland 组件页固定双区域显示 |
| MCP 协议 | 支持 Streamable HTTP MCP 端点，可被外部 AI Agent 调用 |
| Web 设置页 | 基于 `test.html` 的完整配置 UI |

## 技术栈

- **.NET 8** (`net8.0-windows`) — C# 插件框架
- **ClassIsland.PluginSdk** — 宿主接口
- **WebView2** — 设置页渲染
- **Python 3** (`Assets_Duty/core.py`) — 核心排班逻辑

## 安全设计

### API Key 加密存储

API Key 使用 **AES-256-CBC + HMAC-SHA256** 加密存储，绑定到本机物理网卡 MAC 地址：

- 加密密钥通过 **PBKDF2**（120,000 次迭代）派生，输入为 MAC + 应用熵
- 每次加密使用随机 Salt 和 IV
- HMAC 使用 `FixedTimeEquals` 防时序攻击
- 密钥使用后主动 `ZeroMemory` 擦除

### MAC 地址绑定增强

针对教室环境（USB 网卡插拔等场景），解密时会遍历系统所有物理网卡尝试匹配：

- **加密**：优先选择最稳定的网卡（Ethernet > Wireless > 其他）
- **解密**：自动尝试所有候选 MAC，不会因临时插入 USB 网卡而失败
- 过滤虚拟/VPN/蓝牙等非物理网卡

### API Key 传输

- C# → Python：通过 **stdin 管道** 传递（非环境变量），进程外不可见
- Python → LLM：通过 `Authorization: Bearer` HTTPS 请求传输
- UI 显示：仅显示 `********` 掩码
- 磁盘文件：`ipc_input.json` 不含 API Key

## 构建与发布

```powershell
dotnet build -c Release
dotnet publish -c Release -o ./PublishOutput --no-self-contained
```

将 `PublishOutput` 内容复制到 ClassIsland 插件目录（`data/Plugins/Duty-Agent`）后重启宿主。

## 目录结构

```
Duty-Agent/
├── Plugin.cs                          # 插件入口与服务注册
├── manifest.yml                       # 插件元数据 (v1.0.0)
├── Services/
│   ├── DutyBackendService.cs          # 配置、名单、核心调度
│   ├── SecurityHelper.cs              # API Key 加密/解密
│   ├── DutyLocalPreviewHostedService.cs  # 本地 HTTP/MCP 服务
│   ├── DutyNotificationService.cs     # 通知联动
│   └── PythonProcessTracker.cs        # Python 进程生命周期
├── Models/
│   ├── DutyConfig.cs                  # 配置模型
│   └── DutyState.cs                   # 排班状态模型
├── Views/SettingPages/
│   └── DutyWebSettingsPage.axaml.cs   # WebView2 设置页
├── Assets_Duty/
│   ├── core.py                        # Python 排班核心脚本
│   ├── test_core.py                   # 单元测试 (22 cases)
│   ├── web/test.html                  # Web 设置页
│   ├── data/                          # 运行时数据
│   │   ├── config.json                # 加密配置
│   │   ├── state.json                 # 排班状态
│   │   └── roster.csv                 # 学生名单
│   └── python-embed/                  # 嵌入式 Python
└── README.md
```

## 本地 API 接口

插件启动后在本机监听 HTTP 服务（默认 `http://127.0.0.1:48380`，端口自动递增）。

### REST API

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/health` | 健康检查 |
| `POST` | `/api/v1/schedule/overwrite` | 覆盖排班（`replace_all` 模式） |

请求示例：

```json
{
  "instruction": "从今天开始覆盖排7天，每个区域每天2人。",
  "config": {
    "base_url": "https://integrate.api.nvidia.com/v1",
    "model": "moonshotai/kimi-k2-thinking",
    "auto_run_coverage_days": 7,
    "per_day": 2,
    "skip_weekends": true,
    "area_names": ["教室", "清洁区"]
  }
}
```

### MCP 端点 (Streamable HTTP)

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/mcp` (Accept: `text/event-stream`) | SSE 会话 |
| `POST` | `/mcp` (Header: `Mcp-Session-Id`) | JSON-RPC 提交 |

支持的 JSON-RPC 方法：`initialize`、`tools/list`、`tools/call`

可用工具：
- `schedule_overwrite` — 覆盖排班
- `config_update_settings` — 更新配置

## 许可

见 `LICENSE`。
