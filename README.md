# Duty-Agent

`Duty-Agent` 是一个面向 **ClassIsland** 的 AI 值日排班插件。  
它通过 WebView2 设置页 + Python 核心排班脚本，完成名单管理、区域管理、自动/手动排班与通知联动。

## 功能概览

- AI 排班：根据规则与名单生成值日安排。
- 多区域支持：内置 `教室 / 清洁区`，支持自定义区域。
- 名单管理：增删学生、自动分配 ID、启用/停用状态。
- 组件展示：支持组件页固定双区域显示（教室/清洁区）。
- 通知联动：支持通知模板与宿主通知发布。
- Web 设置页：生产页使用 `Assets_Duty/web/index.html`。

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
- `Assets_Duty/web/index.html`：生产设置页。
- `Assets_Duty/web/test.html`：测试页面。
- `manifest.yml`：插件元数据。

## 许可

见 `LICENSE`。
