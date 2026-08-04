Duty-Agent 独立客户端安装说明
================================

1. 解压本目录到任意位置（路径建议不含空格与中文）。
2. 双击 client\duty-agent.exe 启动（自带运行时，无需安装 .NET / Python）。
3. 首次启动会自动拉起后端并打开配置页，按向导完成：导入名单 -> 选模型 -> 试跑一次。
4. 如使用 ClassIsland：在客户端界面点“安装 ClassIsland 插件”，或手动把 bridge\ 目录
   拷贝到 <ClassIsland>\data\Plugins\duty-agent-bridge\ 后重启 ClassIsland。
5. AI 接入：参考 client\Assets_Duty\AGENTS.md.example，或运行
   client\Assets_Duty\python-embed\python.exe client\Assets_Duty\cli.py doctor

数据与备份：花名册/配置/排班状态均在 data\ 目录（roster.csv / config.json / state.json）。
