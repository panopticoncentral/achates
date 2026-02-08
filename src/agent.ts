import { chatCompletion, chatCompletionStream, type Message } from './openrouter.js';
import { getToolDefinitions, executeTool } from './tools/index.js';
import { saveConversation, type Conversation } from './memory.js';
import { getSettings } from './settings.js';
import logger from './logger.js';

const MAX_TOOL_ROUNDS = 10;

export async function* runAgent(
  conversation: Conversation,
  userMessage: string
): AsyncGenerator<string> {
  conversation.messages.push({ role: 'user', content: userMessage });
  logger.info({ conversationId: conversation.id }, 'Agent run started');

  const settings = await getSettings();
  const toolDefinitions = getToolDefinitions();
  let rounds = 0;

  while (rounds < MAX_TOOL_ROUNDS) {
    rounds++;
    logger.debug({ conversationId: conversation.id, round: rounds }, 'Agent round started');

    const messagesForApi: Message[] = [
      { role: 'system', content: settings.systemPrompt },
      ...conversation.messages,
    ];

    let assistantContent = '';
    const toolCallAccumulators: Map<number, { id: string; name: string; arguments: string }> = new Map();

    for await (const chunk of chatCompletionStream({
      messages: messagesForApi,
      tools: toolDefinitions.length > 0 ? toolDefinitions : undefined,
      tool_choice: toolDefinitions.length > 0 ? 'auto' : undefined,
    })) {
      const choice = chunk.choices[0];
      if (!choice) continue;

      if (choice.delta.content) {
        assistantContent += choice.delta.content;
        yield choice.delta.content;
      }

      if (choice.delta.tool_calls) {
        for (const tc of choice.delta.tool_calls) {
          if (!toolCallAccumulators.has(tc.index)) {
            toolCallAccumulators.set(tc.index, { id: '', name: '', arguments: '' });
          }
          const acc = toolCallAccumulators.get(tc.index)!;
          if (tc.id) acc.id = tc.id;
          if (tc.function?.name) acc.name = tc.function.name;
          if (tc.function?.arguments) acc.arguments += tc.function.arguments;
        }
      }
    }

    const toolCalls = Array.from(toolCallAccumulators.values());

    const assistantMessage: Message = {
      role: 'assistant',
      content: assistantContent || null,
    };
    if (toolCalls.length > 0) {
      assistantMessage.tool_calls = toolCalls.map(tc => ({
        id: tc.id,
        type: 'function' as const,
        function: { name: tc.name, arguments: tc.arguments },
      }));
    }
    conversation.messages.push(assistantMessage);

    if (toolCalls.length === 0) {
      break;
    }

    // Execute tool calls
    for (const tc of toolCalls) {
      let parsedArgs: Record<string, unknown> = {};
      try {
        parsedArgs = JSON.parse(tc.arguments);
      } catch {
        logger.warn({ tool: tc.name, rawArguments: tc.arguments }, 'Failed to parse tool call arguments');
      }

      logger.info({ tool: tc.name, conversationId: conversation.id }, 'Tool execution started');
      const result = await executeTool(tc.name, parsedArgs);
      logger.info({ tool: tc.name, conversationId: conversation.id, resultLength: result.length }, 'Tool execution completed');
      conversation.messages.push({
        role: 'tool',
        content: result,
        tool_call_id: tc.id,
      });
    }

    // Signal tool processing to client
    yield '\n';
  }

  // Auto-generate title on first exchange
  if (
    conversation.title === 'New Conversation' &&
    conversation.messages.filter(m => m.role === 'user').length === 1
  ) {
    try {
      logger.debug({ conversationId: conversation.id }, 'Generating conversation title');
      conversation.title = await generateTitle(conversation.messages);
      logger.debug({ conversationId: conversation.id, title: conversation.title }, 'Conversation title generated');
    } catch {
      logger.warn({ conversationId: conversation.id }, 'Title generation failed, keeping default');
    }
  }

  conversation.updated = new Date().toISOString();
  await saveConversation(conversation);
  logger.debug({ conversationId: conversation.id }, 'Conversation saved');
}

async function generateTitle(messages: Message[]): Promise<string> {
  const result = await chatCompletion({
    messages: [
      {
        role: 'system',
        content: 'Generate a short title (3-6 words) for this conversation. Return only the title text, nothing else. No quotes.',
      },
      ...messages
        .filter(m => m.role === 'user' || (m.role === 'assistant' && m.content))
        .slice(0, 4)
        .map(m => ({ role: m.role, content: m.content } as Message)),
    ],
  });
  return result.content?.trim() || 'New Conversation';
}
