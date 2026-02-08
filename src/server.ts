import 'dotenv/config';
import express from 'express';
import path from 'path';
import { fileURLToPath } from 'url';
import { runAgent } from './agent.js';
import { createConversation, loadConversation, listConversations } from './memory.js';
import { getSettings, saveSettings, maskApiKey } from './settings.js';
import './tools/index.js';
import logger from './logger.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const app = express();

app.use(express.json());
app.use(express.static(path.join(__dirname, '..', 'public')));

// List conversations
app.get('/api/conversations', async (_req, res) => {
  try {
    const conversations = await listConversations();
    res.json(conversations);
  } catch (err) {
    logger.error({ err }, 'Failed to list conversations');
    res.status(500).json({ error: (err as Error).message });
  }
});

// Create new conversation
app.post('/api/conversations', async (_req, res) => {
  try {
    const conversation = await createConversation();
    res.json({ id: conversation.id, title: conversation.title });
  } catch (err) {
    logger.error({ err }, 'Failed to create conversation');
    res.status(500).json({ error: (err as Error).message });
  }
});

// Load conversation
app.get('/api/conversations/:id', async (req, res) => {
  try {
    const conversation = await loadConversation(req.params.id);
    res.json(conversation);
  } catch {
    logger.warn({ conversationId: req.params.id }, 'Conversation not found');
    res.status(404).json({ error: 'Conversation not found' });
  }
});

// Send message — SSE streaming response
app.post('/api/conversations/:id/messages', async (req, res) => {
  const { message } = req.body;
  if (!message || typeof message !== 'string') {
    res.status(400).json({ error: 'message is required' });
    return;
  }

  let conversation;
  try {
    conversation = await loadConversation(req.params.id);
  } catch {
    logger.warn({ conversationId: req.params.id }, 'Conversation not found for message');
    res.status(404).json({ error: 'Conversation not found' });
    return;
  }

  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');
  res.flushHeaders();

  try {
    logger.info({ conversationId: req.params.id }, 'Chat message received');
    for await (const token of runAgent(conversation, message)) {
      res.write(`data: ${JSON.stringify({ type: 'token', content: token })}\n\n`);
    }
    logger.info({ conversationId: req.params.id }, 'Chat response complete');
    res.write(`data: ${JSON.stringify({ type: 'done', title: conversation.title, id: conversation.id })}\n\n`);
  } catch (err) {
    logger.error({ err, conversationId: req.params.id }, 'Error during agent streaming');
    res.write(`data: ${JSON.stringify({ type: 'error', message: (err as Error).message })}\n\n`);
  }

  res.end();
});

// Delete conversation
app.delete('/api/conversations/:id', async (req, res) => {
  try {
    const conversation = await loadConversation(req.params.id);
    const fs = await import('fs/promises');
    const dataDir = path.join(__dirname, '..', 'data', 'conversations');
    const files = await fs.readdir(dataDir);
    const file = files.find(f => f.includes(`_${conversation.id}.md`));
    if (file) {
      await fs.unlink(path.join(dataDir, file));
    }
    res.json({ ok: true });
  } catch {
    logger.warn({ conversationId: req.params.id }, 'Conversation not found for deletion');
    res.status(404).json({ error: 'Conversation not found' });
  }
});

// Get settings
app.get('/api/settings', async (_req, res) => {
  try {
    const settings = await getSettings();
    res.json({
      model: settings.model,
      apiKey: maskApiKey(settings.apiKey),
      systemPrompt: settings.systemPrompt,
      theme: settings.theme,
    });
  } catch (err) {
    logger.error({ err }, 'Failed to load settings');
    res.status(500).json({ error: (err as Error).message });
  }
});

// Update settings
app.put('/api/settings', async (req, res) => {
  try {
    const { model, apiKey, systemPrompt, theme } = req.body;
    const current = await getSettings();

    // If the client sends back the masked key, keep the existing one
    const resolvedApiKey =
      apiKey && !apiKey.includes('...') && apiKey !== '****'
        ? apiKey
        : current.apiKey;

    const updated = await saveSettings({
      model,
      apiKey: resolvedApiKey,
      systemPrompt,
      theme,
    });

    res.json({
      model: updated.model,
      apiKey: maskApiKey(updated.apiKey),
      systemPrompt: updated.systemPrompt,
      theme: updated.theme,
    });
  } catch (err) {
    logger.error({ err }, 'Failed to save settings');
    res.status(500).json({ error: (err as Error).message });
  }
});

// Proxy OpenRouter model list
let modelsCache: { data: Array<{ id: string; name: string }>; fetchedAt: number } | null = null;
const MODELS_CACHE_TTL = 60 * 60 * 1000; // 1 hour

app.get('/api/models', async (_req, res) => {
  try {
    if (modelsCache && Date.now() - modelsCache.fetchedAt < MODELS_CACHE_TTL) {
      res.json(modelsCache.data);
      return;
    }

    const settings = await getSettings();
    if (!settings.apiKey) {
      res.json([]);
      return;
    }

    const response = await fetch('https://openrouter.ai/api/v1/models', {
      headers: {
        'Authorization': `Bearer ${settings.apiKey}`,
        'HTTP-Referer': 'http://localhost:3000',
        'X-Title': 'Achates',
      },
    });

    if (!response.ok) {
      logger.warn({ status: response.status }, 'Failed to fetch models from OpenRouter');
      res.json([]);
      return;
    }

    const json = await response.json() as { data: Array<{ id: string; name: string }> };
    const models = json.data
      .map(m => ({ id: m.id, name: m.name }))
      .sort((a, b) => a.name.localeCompare(b.name));

    modelsCache = { data: models, fetchedAt: Date.now() };
    res.json(models);
  } catch (err) {
    logger.error({ err }, 'Failed to fetch models');
    res.status(500).json({ error: (err as Error).message });
  }
});

const PORT = parseInt(process.env.PORT || '3000', 10);
app.listen(PORT, '127.0.0.1', () => {
  logger.info({ port: PORT }, 'Achates server started');
});
