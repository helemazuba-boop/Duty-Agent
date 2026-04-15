/** 工具服务器类型 */
export type ToolServerType = 'mcp' | 'http' | 'python';

/** 工具服务器配置 */
export interface ToolServerConfig {
  id: string;
  name: string;
  type: ToolServerType;
  enabled: boolean;
  // MCP 类型
  url?: string;
  token?: string;
  // HTTP 类型
  httpUrl?: string;
  httpMethod?: 'GET' | 'POST';
  httpHeaders?: Record<string, string>;
  // Python 类型
  functionName?: string;
  // 连接状态
  status: 'connected' | 'disconnected' | 'connecting' | 'error';
  errorMessage?: string;
  lastConnectedAt?: string;
}

/** AI 工具配置 */
export interface AiToolsConfig {
  enabled: boolean;
  toolServers: ToolServerConfig[];
  maxToolCallsPerTurn: number;
  toolCallTimeoutSeconds: number;
}

/** MCP 工具元信息 */
export interface McpToolInfo {
  name: string;
  description: string;
  inputSchema: {
    type: 'object';
    properties: Record<string, any>;
    required?: string[];
  };
  serverId: string;
}

/** 工具测试结果 */
export interface ToolTestResult {
  success: boolean;
  output?: string;
  error?: string;
  duration: number;
}
