import type { Tool } from './index.js';
import { searchConversations } from '../memory.js';

export const memorySearchTool: Tool = {
  name: 'search_memory',
  description: 'Search past conversations for relevant information. Use when the user refers to something discussed previously or when context from past conversations would be helpful.',
  parameters: {
    type: 'object',
    properties: {
      query: {
        type: 'string',
        description: 'The search query to find in past conversations.',
      },
    },
    required: ['query'],
  },
  execute: async (args) => {
    const results = await searchConversations(args.query as string);
    if (results.length === 0) {
      return JSON.stringify({ results: [], message: 'No matching conversations found.' });
    }
    return JSON.stringify({ results: results.slice(0, 5) });
  },
};
