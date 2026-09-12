# Duty-Agent 桥接（ClassIsland 插件）

把 [Duty-Agent](https://github.com/helemazuba-boop/Duty-Agent) 的值日排班接入
[ClassIsland](https://github.com/ClassIsland/ClassIsland)：在主屏显示今日值日、
转发排班通知与到点提醒，并提供"今日值日匹配"规则与"执行值日排班"自动化动作。

## 工作方式

本插件与**独立 Duty-Agent 客户端**协同：客户端负责启动后端并把连接信息
（端口、token）写入 ClassIsland 配置目录的 `DutyAgentBridge\.duty-agent-meta.json`；
插件读取该文件与后端建立 HTTP/SSE/WebSocket 连接。

- 通知总线：SSE 长连接由桥接服务持有，随连接状态自动启停；
  后端推送 `snapshot_changed` / `schedule_completed` / `schedule_failed` /
  `duty_reminder` 时实时呈现。
- 桌面组件"值日人员"：数据由 `DutyStateCache` 统一维护（推送驱动 + 60s 安全
  轮询兜底），组件与自动化规则只读内存，不做轮询请求。
- 插件设置保存在 ClassIsland 为本插件分配的配置目录
  （`Config\Plugins\duty-agent-bridge\Settings.json`），随宿主配置一起备份。

## 前置条件

1. 安装并启动独立 Duty-Agent 客户端（它管理后端进程的生命周期）。
2. ClassIsland 与客户端位于同一台机器（本插件只连接 127.0.0.1）。
3. 特殊部署（便携版等）可设置环境变量 `CLASSISLAND_CONFIG_PATH` 指向
   ClassIsland 的配置目录，插件、客户端、CLI 均优先读取该变量。

## 构建与打包

```
dotnet build Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj -c Release
dotnet publish Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj -c Release -p:CreateCipx=true
```

`dotnet publish -p:CreateCipx=true` 输出官方 `.cipx` 插件包与 MD5 校验信息到项目
目录的 `cipx\`。本地部署可直接使用 `scripts/Deploy-BridgeToClassIsland.ps1`。
