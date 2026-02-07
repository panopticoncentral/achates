import type { Tool } from './index.js';

export const datetimeTool: Tool = {
  name: 'get_datetime',
  description: 'Get the current date and time. Use when the user asks what time or date it is, or needs time-related information.',
  parameters: {
    type: 'object',
    properties: {
      timezone: {
        type: 'string',
        description: 'IANA timezone name (e.g., "America/New_York"). Defaults to system timezone.',
      },
    },
    required: [],
  },
  execute: async (args) => {
    const tz = (args.timezone as string) || Intl.DateTimeFormat().resolvedOptions().timeZone;
    const now = new Date();
    const formatted = now.toLocaleString('en-US', {
      timeZone: tz,
      weekday: 'long',
      year: 'numeric',
      month: 'long',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      timeZoneName: 'short',
    });
    return JSON.stringify({ datetime: now.toISOString(), formatted, timezone: tz });
  },
};
