import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { AiToolsConfig, ToolServerConfig, McpToolInfo } from '@/types/ai-tools';

export const useAiToolsStore = defineStore('aiTools', () => {
  // 配置
  const config = ref<AiToolsConfig>({
    enabled: false,
    toolServers: [],
    maxToolCallsPerTurn: 10,
    toolCallTimeoutSeconds: 30,
  });

  // 已连接的工具列表
  const connectedTools = ref<McpToolInfo[]>([]);

  // 启用/禁用
  const setEnabled = (enabled: boolean) => {
    config.value.enabled = enabled;
  };

  // 服务器管理
  const addServer = (server: ToolServerConfig) => {
    config.value.toolServers.push(server);
  };

  const updateServer = (id: string, updates: Partial<ToolServerConfig>) => {
    const idx = config.value.toolServers.findIndex((s) => s.id === id);
    if (idx !== -1) {
      config.value.toolServers[idx] = { ...config.value.toolServers[idx], ...updates };
    }
  };

  const removeServer = (id: string) => {
    config.value.toolServers = config.value.toolServers.filter((s) => s.id !== id);
    connectedTools.value = connectedTools.value.filter((t) => t.serverId !== id);
  };

  // 调用设置
  const setMaxToolCalls = (max: number) => {
    config.value.maxToolCallsPerTurn = max;
  };

  const setTimeout = (seconds: number) => {
    config.value.toolCallTimeoutSeconds = seconds;
  };

  // 连接 MCP 服务器
  const connectServer = async (id: string) => {
    const server = config.value.toolServers.find((s) => s.id === id);
    if (!server || server.type !== 'mcp') return;

    updateServer(id, { status: 'connecting' });

    try {
      const tools = await refreshTools(id);
      updateServer(id, {
        status: 'connected',
        lastConnectedAt: new Date().toISOString(),
      });
      // 替换该服务器的工具列表
      connectedTools.value = [
        ...connectedTools.value.filter((t) => t.serverId !== id),
        ...tools,
      ];
    } catch (error) {
      updateServer(id, {
        status: 'error',
        errorMessage: String(error),
      });
    }
  };

  const disconnectServer = (id: string) => {
    updateServer(id, { status: 'disconnected' });
    connectedTools.value = connectedTools.value.filter((t) => t.serverId !== id);
  };

  // 获取工具列表
  const refreshTools = async (id: string): Promise<McpToolInfo[]> => {
    const server = config.value.toolServers.find((s) => s.id === id);
    if (!server?.url) return [];

    const response = await fetch(server.url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(server.token ? { Authorization: `Bearer ${server.token}` } : {}),
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/list',
        params: {},
        id: 1,
      }),
    });

    const result = await response.json();
    const tools: McpToolInfo[] = (result.tools || []).map((t: any) => ({
      name: t.name,
      description: t.description || '',
      inputSchema: t.inputSchema || { type: 'object', properties: {} },
      serverId: id,
    }));

    return tools;
  };

  // 测试工具
  const testTool = async (
    serverId: string,
    toolName: string,
    args: Record<string, any>,
  ) => {
    const server = config.value.toolServers.find((s) => s.id === serverId);
    if (!server?.url) throw new Error('Server not configured');

    const response = await fetch(server.url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(server.token ? { Authorization: `Bearer ${server.token}` } : {}),
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/call',
        params: { name: toolName, arguments: args },
        id: Date.now(),
      }),
    });

    return await response.json();
  };

  return {
    config,
    connectedTools,
    setEnabled,
    addServer,
    updateServer,
    removeServer,
    setMaxToolCalls,
    setTimeout,
    connectServer,
    disconnectServer,
    refreshTools,
    testTool,
  };
});
