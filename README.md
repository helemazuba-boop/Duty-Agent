# Duty-Agent

`Duty-Agent` 是一个面向 **ClassIsland** 的 AI 值日排班插件。  
它通过 WebView2 设置页 + Python 核心排班脚本，完成名单管理、区域管理、自动/手动排班与通知联动。

## 功能概览

- AI 排班：根据规则与名单生成值日安排。
- 多区域支持：内置 `教室 / 清洁区`，支持自定义区域。
- 名单管理：增删学生、自动分配 ID、启用/停用状态。
- 组件展示：支持组件页固定双区域显示（教室/清洁区）。
- 通知联动：支持通知模板与宿主通知发布。
- Web 设置页：当前统一使用 `Assets_Duty/web/test.html`。

## 技术栈

- .NET 8 (`net8.0-windows`)
- ClassIsland.PluginSdk
- WebView2
- Python（`Assets_Duty/core.py`）

## 构建与发布

```powershell
dotnet build -c Release
dotnet publish -c Release -o ./PublishOutput --no-self-contained
```

将 `PublishOutput` 内容复制到 ClassIsland 插件目录（`data/Plugins/Duty-Agent`）后重启宿主。

## 目录说明

- `Plugin.cs`：插件入口与服务注册。
- `Views/SettingPages/DutyWebSettingsPage.axaml.cs`：WebView2 设置页桥接层。
- `Services/DutyBackendService.cs`：配置、名单、核心调度调用。
- `Assets_Duty/core.py`：Python 排班核心。
- `Assets_Duty/web/test.html`：测试页面。
- `Services/DutyLocalPreviewHostedService.cs`：本地预览与二次开发 API。
- `manifest.yml`：插件元数据。

## 本地覆盖排班接口（供二次开发）

插件启动后会在本机自动启动本地服务（默认 `http://127.0.0.1:48380`，若占用会自动换端口）。

- 健康检查：`GET /health`
- 覆盖排班：`POST /api/v1/schedule/overwrite`

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
    "duty_rule": "",
    "python_path": ".\\Assets_Duty\\python-embed\\python.exe",
    "area_names": ["教室", "清洁区"]
  }
}
```

说明：

- 接口固定使用覆盖模式 `replace_all`。
- 返回 JSON 内含 `success`、`message`、`ai_response`、`state` 等字段，便于外部系统直接消费。
- `test.html` 已内置“通过本地接口覆盖排班”按钮，可直接联调该接口。

## 许可

见 `LICENSE`。

## MCP (Streamable HTTP) endpoint

The local server also exposes an MCP-compatible wrapper endpoint:

- SSE session: `GET /mcp` with header `Accept: text/event-stream`
- JSON-RPC submit: `POST /mcp` with header `Mcp-Session-Id: <session-id>`
- Session fallback: `POST /mcp?sessionId=<session-id>` is also accepted when client cannot send custom headers.

Notes:
- `POST /mcp` returns `202 Accepted` immediately.
- JSON-RPC responses are pushed via the SSE channel as `event: message`.
- `endpoint` SSE event now includes the `sessionId` query parameter for better client compatibility.
- Existing REST endpoint `POST /api/v1/schedule/overwrite` is kept for backward compatibility.

Supported JSON-RPC methods:
- `initialize`
- `tools/list`
- `tools/call` (tool name: `schedule_overwrite`)

`schedule_overwrite` arguments:

```json
{
  "instruction": "从今天开始覆盖排7天，每个区域每天2人",
  "config": {
    "base_url": "https://integrate.api.nvidia.com/v1",
    "model": "moonshotai/kimi-k2-thinking",
    "auto_run_coverage_days": 7,
    "per_day": 2,
    "skip_weekends": true,
    "duty_rule": "",
    "start_from_today": true,
    "python_path": ".\\Assets_Duty\\python-embed\\python.exe",
    "area_names": ["教室", "清洁区"]
  }
}
```
