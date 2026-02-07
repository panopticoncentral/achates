import type { ToolDefinition } from '../openrouter.js';

export interface Tool {
  name: string;
  description: string;
  parameters: Record<string, unknown>;
  execute: (args: Record<string, unknown>) => Promise<string>;
}

const tools: Map<string, Tool> = new Map();

export function registerTool(tool: Tool): void {
  tools.set(tool.name, tool);
}

export function getTool(name: string): Tool | undefined {
  return tools.get(name);
}

export function getAllTools(): Tool[] {
  return Array.from(tools.values());
}

export function getToolDefinitions(): ToolDefinition[] {
  return getAllTools().map(tool => ({
    type: 'function' as const,
    function: {
      name: tool.name,
      description: tool.description,
      parameters: tool.parameters,
    },
  }));
}

export async function executeTool(name: string, args: Record<string, unknown>): Promise<string> {
  const tool = tools.get(name);
  if (!tool) {
    return JSON.stringify({ error: `Unknown tool: ${name}` });
  }
  try {
    return await tool.execute(args);
  } catch (err) {
    return JSON.stringify({ error: `Tool execution failed: ${(err as Error).message}` });
  }
}

// Register all tools
import { datetimeTool } from './datetime.js';
import { memorySearchTool } from './memory-search.js';

registerTool(datetimeTool);
registerTool(memorySearchTool);
