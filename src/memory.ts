import fs from 'fs/promises';
import path from 'path';
import crypto from 'crypto';
import { fileURLToPath } from 'url';
import type { Message } from './openrouter.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const DATA_DIR = path.join(__dirname, '..', 'data', 'conversations');

export interface Conversation {
  id: string;
  title: string;
  created: string;
  updated: string;
  model: string;
  messages: Message[];
}

export async function createConversation(): Promise<Conversation> {
  await fs.mkdir(DATA_DIR, { recursive: true });

  const id = crypto.randomBytes(6).toString('hex');
  const now = new Date().toISOString();
  const model = process.env.MODEL || 'anthropic/claude-sonnet-4';

  const conversation: Conversation = {
    id,
    title: 'New Conversation',
    created: now,
    updated: now,
    model,
    messages: [],
  };

  await saveConversation(conversation);
  return conversation;
}

export async function saveConversation(conversation: Conversation): Promise<void> {
  await fs.mkdir(DATA_DIR, { recursive: true });

  const filename = `${conversation.created.slice(0, 10)}_${conversation.id}.md`;
  const filepath = path.join(DATA_DIR, filename);

  // Check if file exists with a different date prefix (conversation may span days)
  const existing = await findConversationFile(conversation.id);
  const targetPath = existing || filepath;

  const content = serializeConversation(conversation);
  await fs.writeFile(targetPath, content, 'utf-8');
}

export async function loadConversation(id: string): Promise<Conversation> {
  const filepath = await findConversationFile(id);
  if (!filepath) {
    throw new Error(`Conversation not found: ${id}`);
  }

  const content = await fs.readFile(filepath, 'utf-8');
  return deserializeConversation(content);
}

export async function listConversations(): Promise<Array<{
  id: string;
  title: string;
  created: string;
  updated: string;
}>> {
  await fs.mkdir(DATA_DIR, { recursive: true });

  const files = await fs.readdir(DATA_DIR);
  const mdFiles = files.filter(f => f.endsWith('.md'));

  const conversations = [];
  for (const file of mdFiles) {
    const content = await fs.readFile(path.join(DATA_DIR, file), 'utf-8');
    const meta = parseFrontmatter(content);
    if (meta.id) {
      conversations.push({
        id: meta.id,
        title: meta.title || 'Untitled',
        created: meta.created || '',
        updated: meta.updated || '',
      });
    }
  }

  // Sort by updated date, most recent first
  conversations.sort((a, b) => b.updated.localeCompare(a.updated));
  return conversations;
}

export async function searchConversations(query: string): Promise<Array<{
  id: string;
  title: string;
  snippet: string;
}>> {
  await fs.mkdir(DATA_DIR, { recursive: true });

  const files = await fs.readdir(DATA_DIR);
  const mdFiles = files.filter(f => f.endsWith('.md'));
  const queryLower = query.toLowerCase();
  const results = [];

  for (const file of mdFiles) {
    const content = await fs.readFile(path.join(DATA_DIR, file), 'utf-8');
    const contentLower = content.toLowerCase();

    if (contentLower.includes(queryLower)) {
      const meta = parseFrontmatter(content);
      const idx = contentLower.indexOf(queryLower);
      const start = Math.max(0, idx - 80);
      const end = Math.min(content.length, idx + query.length + 80);
      const snippet = (start > 0 ? '...' : '') +
        content.slice(start, end).replace(/\n/g, ' ') +
        (end < content.length ? '...' : '');

      results.push({
        id: meta.id || file,
        title: meta.title || 'Untitled',
        snippet,
      });
    }
  }

  return results;
}

// --- Serialization ---

function serializeConversation(conv: Conversation): string {
  const lines: string[] = [
    '---',
    `id: ${conv.id}`,
    `title: "${conv.title.replace(/"/g, '\\"')}"`,
    `created: ${conv.created}`,
    `updated: ${conv.updated}`,
    `model: ${conv.model}`,
    '---',
    '',
  ];

  for (const msg of conv.messages) {
    if (msg.role === 'system') continue;

    if (msg.role === 'tool') {
      lines.push(`## Tool (${msg.tool_call_id || 'unknown'})`);
      lines.push('');
      lines.push('```tool-result');
      lines.push(msg.content || '');
      lines.push('```');
      lines.push('');
      continue;
    }

    const roleName = msg.role === 'user' ? 'User' : 'Assistant';
    lines.push(`## ${roleName}`);
    lines.push('');

    if (msg.content) {
      lines.push(msg.content);
      lines.push('');
    }

    if (msg.tool_calls && msg.tool_calls.length > 0) {
      for (const tc of msg.tool_calls) {
        lines.push('```tool-call');
        let parsedArgs = {};
        try {
          parsedArgs = JSON.parse(tc.function.arguments);
        } catch {
          // Empty or malformed arguments (e.g. tools called with no args)
        }
        lines.push(JSON.stringify({
          id: tc.id,
          name: tc.function.name,
          arguments: parsedArgs,
        }));
        lines.push('```');
        lines.push('');
      }
    }
  }

  return lines.join('\n');
}

function deserializeConversation(content: string): Conversation {
  const meta = parseFrontmatter(content);

  // Extract body after frontmatter
  const fmEnd = content.indexOf('---', content.indexOf('---') + 3);
  const body = fmEnd >= 0 ? content.slice(fmEnd + 3).trim() : '';

  const messages: Message[] = [];
  const sections = body.split(/^## /m).filter(s => s.trim());

  for (const section of sections) {
    const newlineIdx = section.indexOf('\n');
    const header = newlineIdx >= 0 ? section.slice(0, newlineIdx).trim() : section.trim();
    const sectionBody = newlineIdx >= 0 ? section.slice(newlineIdx + 1).trim() : '';

    if (header === 'User') {
      messages.push({ role: 'user', content: sectionBody });
    } else if (header === 'Assistant') {
      const msg: Message = { role: 'assistant', content: null };

      // Extract tool calls
      const toolCallRegex = /```tool-call\n([\s\S]*?)```/g;
      let match;
      const toolCalls: Message['tool_calls'] = [];
      let textContent = sectionBody;

      while ((match = toolCallRegex.exec(sectionBody)) !== null) {
        try {
          const parsed = JSON.parse(match[1].trim());
          toolCalls.push({
            id: parsed.id,
            type: 'function',
            function: {
              name: parsed.name,
              arguments: JSON.stringify(parsed.arguments),
            },
          });
        } catch {
          // Skip malformed tool calls
        }
        textContent = textContent.replace(match[0], '').trim();
      }

      msg.content = textContent || null;
      if (toolCalls.length > 0) {
        msg.tool_calls = toolCalls;
      }

      messages.push(msg);
    } else if (header.startsWith('Tool (')) {
      const toolCallId = header.match(/Tool \(([^)]+)\)/)?.[1] || 'unknown';
      const resultRegex = /```tool-result\n([\s\S]*?)```/;
      const resultMatch = sectionBody.match(resultRegex);
      const resultContent = resultMatch ? resultMatch[1].trim() : sectionBody;

      messages.push({
        role: 'tool',
        content: resultContent,
        tool_call_id: toolCallId,
      });
    }
  }

  return {
    id: meta.id || 'unknown',
    title: meta.title || 'Untitled',
    created: meta.created || '',
    updated: meta.updated || '',
    model: meta.model || process.env.MODEL || 'anthropic/claude-sonnet-4',
    messages,
  };
}

// --- Helpers ---

function parseFrontmatter(content: string): Record<string, string> {
  const meta: Record<string, string> = {};
  const match = content.match(/^---\n([\s\S]*?)\n---/);
  if (!match) return meta;

  for (const line of match[1].split('\n')) {
    const colonIdx = line.indexOf(':');
    if (colonIdx < 0) continue;
    const key = line.slice(0, colonIdx).trim();
    let value = line.slice(colonIdx + 1).trim();
    // Strip surrounding quotes
    if (value.startsWith('"') && value.endsWith('"')) {
      value = value.slice(1, -1).replace(/\\"/g, '"');
    }
    meta[key] = value;
  }

  return meta;
}

async function findConversationFile(id: string): Promise<string | null> {
  try {
    const files = await fs.readdir(DATA_DIR);
    const match = files.find(f => f.includes(`_${id}.md`));
    return match ? path.join(DATA_DIR, match) : null;
  } catch {
    return null;
  }
}
