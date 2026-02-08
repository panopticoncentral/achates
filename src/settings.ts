import fs from 'fs/promises';
import path from 'path';
import { fileURLToPath } from 'url';
import logger from './logger.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SETTINGS_PATH = path.join(__dirname, '..', 'data', 'settings.json');

export interface Settings {
  model: string;
  apiKey: string;
  systemPrompt: string;
  theme: 'dark' | 'light';
}

const DEFAULT_SYSTEM_PROMPT = `You are Achates, a helpful and knowledgeable AI assistant. You are named after Aeneas's faithful companion in the Aeneid.

You have access to tools that you can use to help answer questions. Use them when appropriate.

Be concise but thorough. Use markdown formatting in your responses when it helps readability.`;

let cached: Settings | null = null;

function defaults(): Settings {
  return {
    model: process.env.MODEL || 'anthropic/claude-sonnet-4',
    apiKey: process.env.OPENROUTER_API_KEY || '',
    systemPrompt: DEFAULT_SYSTEM_PROMPT,
    theme: 'dark',
  };
}

export async function getSettings(): Promise<Settings> {
  if (cached) return cached;
  cached = await loadSettings();
  return cached;
}

async function loadSettings(): Promise<Settings> {
  try {
    const content = await fs.readFile(SETTINGS_PATH, 'utf-8');
    const saved = JSON.parse(content) as Partial<Settings>;
    const fallback = defaults();
    return {
      model: saved.model || fallback.model,
      apiKey: saved.apiKey || fallback.apiKey,
      systemPrompt: saved.systemPrompt ?? fallback.systemPrompt,
      theme: saved.theme === 'light' ? 'light' : 'dark',
    };
  } catch {
    logger.debug('No settings file found, using defaults');
    return defaults();
  }
}

export async function saveSettings(update: Partial<Settings>): Promise<Settings> {
  const current = await getSettings();

  const merged: Settings = {
    model: update.model ?? current.model,
    apiKey: update.apiKey ?? current.apiKey,
    systemPrompt: update.systemPrompt ?? current.systemPrompt,
    theme: update.theme === 'light' ? 'light' : 'dark',
  };

  await fs.mkdir(path.dirname(SETTINGS_PATH), { recursive: true });
  await fs.writeFile(SETTINGS_PATH, JSON.stringify(merged, null, 2), 'utf-8');
  logger.info('Settings saved');

  cached = merged;
  return merged;
}

export function maskApiKey(key: string): string {
  if (!key || key.length < 8) return key ? '****' : '';
  return key.slice(0, 6) + '...' + key.slice(-4);
}
