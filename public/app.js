const state = {
  currentConversationId: null,
  conversations: [],
  streaming: false,
};

// --- DOM elements ---
const messagesEl = document.getElementById('messages');
const welcomeEl = document.getElementById('welcome');
const messageForm = document.getElementById('message-form');
const messageInput = document.getElementById('message-input');
const sendBtn = document.getElementById('send-btn');
const newChatBtn = document.getElementById('new-chat');
const conversationListEl = document.getElementById('conversation-list');
const settingsBtn = document.getElementById('settings-btn');
const settingsPanel = document.getElementById('settings-panel');
const settingsForm = document.getElementById('settings-form');
const settingModel = document.getElementById('setting-model');
const settingApiKey = document.getElementById('setting-api-key');
const settingSystemPrompt = document.getElementById('setting-system-prompt');
const settingsStatus = document.getElementById('settings-status');
const themeOptions = document.querySelectorAll('.theme-option');

// --- Initialize ---
loadConversations();
initSettings();

// --- Event listeners ---
newChatBtn.addEventListener('click', createNewChat);
settingsBtn.addEventListener('click', showSettings);

messageForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const text = messageInput.value.trim();
  if (!text || state.streaming) return;
  messageInput.value = '';
  messageInput.style.height = 'auto';
  await sendMessage(text);
});

messageInput.addEventListener('input', () => {
  messageInput.style.height = 'auto';
  messageInput.style.height = Math.min(messageInput.scrollHeight, 200) + 'px';
});

messageInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    messageForm.dispatchEvent(new Event('submit'));
  }
});

// --- API functions ---

async function loadConversations() {
  const res = await fetch('/api/conversations');
  state.conversations = await res.json();
  renderConversationList();
}

async function createNewChat() {
  const res = await fetch('/api/conversations', { method: 'POST' });
  const { id, title } = await res.json();
  state.currentConversationId = id;
  state.conversations.unshift({ id, title, created: '', updated: '' });
  renderConversationList();
  showChat();
  clearMessages();
  messageInput.focus();
}

async function selectConversation(id) {
  state.currentConversationId = id;
  renderConversationList();

  const res = await fetch(`/api/conversations/${id}`);
  const conversation = await res.json();

  showChat();
  clearMessages();

  for (const msg of conversation.messages) {
    if (msg.role === 'user' || msg.role === 'assistant') {
      if (msg.content) {
        appendMessage(msg.role, msg.content, true);
      }
    }
  }

  scrollToBottom();
}

async function deleteConversation(id, e) {
  e.stopPropagation();
  await fetch(`/api/conversations/${id}`, { method: 'DELETE' });
  state.conversations = state.conversations.filter(c => c.id !== id);
  if (state.currentConversationId === id) {
    state.currentConversationId = null;
    hideChat();
  }
  renderConversationList();
}

async function sendMessage(text) {
  if (!state.currentConversationId) {
    await createNewChat();
  }

  state.streaming = true;
  sendBtn.disabled = true;

  appendMessage('user', text, true);
  const assistantEl = appendMessage('assistant', '', false);
  const contentEl = assistantEl.querySelector('.content');
  scrollToBottom();

  try {
    const res = await fetch(`/api/conversations/${state.currentConversationId}/messages`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: text }),
    });

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let fullText = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const events = buffer.split('\n\n');
      buffer = events.pop();

      for (const event of events) {
        if (!event.startsWith('data: ')) continue;
        const data = JSON.parse(event.slice(6));

        if (data.type === 'token') {
          fullText += data.content;
          contentEl.innerHTML = renderMarkdown(fullText);
          scrollToBottom();
        } else if (data.type === 'done') {
          if (data.title) {
            updateConversationTitle(state.currentConversationId, data.title);
          }
        } else if (data.type === 'error') {
          fullText += `\n\n**Error:** ${data.message}`;
          contentEl.innerHTML = renderMarkdown(fullText);
        }
      }
    }
  } catch (err) {
    contentEl.innerHTML = renderMarkdown(`**Error:** ${err.message}`);
  }

  state.streaming = false;
  sendBtn.disabled = false;
  messageInput.focus();
}

// --- Rendering ---

function renderConversationList() {
  conversationListEl.innerHTML = '';
  for (const conv of state.conversations) {
    const el = document.createElement('div');
    el.className = 'conversation-item' + (conv.id === state.currentConversationId ? ' active' : '');
    el.textContent = conv.title;
    el.addEventListener('click', () => selectConversation(conv.id));

    const deleteBtn = document.createElement('button');
    deleteBtn.className = 'delete-btn';
    deleteBtn.textContent = '\u00d7';
    deleteBtn.addEventListener('click', (e) => deleteConversation(conv.id, e));
    el.appendChild(deleteBtn);

    conversationListEl.appendChild(el);
  }
}

function showChat() {
  hideSettings();
  welcomeEl.style.display = 'none';
  messagesEl.classList.add('visible');
}

function hideChat() {
  hideSettings();
  welcomeEl.style.display = '';
  messagesEl.classList.remove('visible');
  clearMessages();
}

function clearMessages() {
  messagesEl.innerHTML = '';
}

function appendMessage(role, content, rendered) {
  const el = document.createElement('div');
  el.className = `message ${role}`;

  const roleEl = document.createElement('div');
  roleEl.className = 'role';
  roleEl.textContent = role === 'user' ? 'You' : 'Achates';

  const contentEl = document.createElement('div');
  contentEl.className = 'content';
  if (rendered) {
    contentEl.innerHTML = renderMarkdown(content);
  }

  el.appendChild(roleEl);
  el.appendChild(contentEl);
  messagesEl.appendChild(el);

  return el;
}

function updateConversationTitle(id, title) {
  const conv = state.conversations.find(c => c.id === id);
  if (conv) {
    conv.title = title;
    renderConversationList();
  }
}

function scrollToBottom() {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

// --- Settings ---

let selectedTheme = 'dark';

async function initSettings() {
  try {
    const res = await fetch('/api/settings');
    const settings = await res.json();
    selectedTheme = settings.theme || 'dark';
    applyTheme(selectedTheme);
  } catch {
    // Use defaults
  }
}

async function showSettings() {
  state.currentConversationId = null;
  renderConversationList();

  welcomeEl.style.display = 'none';
  messagesEl.classList.remove('visible');
  messageForm.style.display = 'none';
  settingsPanel.classList.add('visible');

  settingsStatus.textContent = '';
  settingsStatus.className = 'settings-status';

  try {
    const [settingsRes, modelsRes] = await Promise.all([
      fetch('/api/settings'),
      fetch('/api/models'),
    ]);
    const settings = await settingsRes.json();
    const models = await modelsRes.json();

    // Populate model dropdown
    settingModel.innerHTML = '';
    if (models.length > 0) {
      for (const m of models) {
        const opt = document.createElement('option');
        opt.value = m.id;
        opt.textContent = m.name;
        settingModel.appendChild(opt);
      }
    } else {
      // Fallback: just show current model as an option
      const opt = document.createElement('option');
      opt.value = settings.model;
      opt.textContent = settings.model;
      settingModel.appendChild(opt);
    }
    settingModel.value = settings.model;

    // If current model isn't in the list, add it
    if (settingModel.value !== settings.model) {
      const opt = document.createElement('option');
      opt.value = settings.model;
      opt.textContent = settings.model;
      settingModel.prepend(opt);
      settingModel.value = settings.model;
    }

    settingApiKey.value = settings.apiKey;
    settingSystemPrompt.value = settings.systemPrompt;
    selectedTheme = settings.theme || 'dark';
    updateThemeButtons();
  } catch {
    settingsStatus.textContent = 'Failed to load settings';
    settingsStatus.className = 'settings-status error';
  }
}

function hideSettings() {
  settingsPanel.classList.remove('visible');
  messageForm.style.display = '';
}

function updateThemeButtons() {
  for (const btn of themeOptions) {
    btn.classList.toggle('active', btn.dataset.theme === selectedTheme);
  }
}

function applyTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
}

for (const btn of themeOptions) {
  btn.addEventListener('click', () => {
    selectedTheme = btn.dataset.theme;
    updateThemeButtons();
    applyTheme(selectedTheme);
  });
}

settingsForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  settingsStatus.textContent = 'Saving...';
  settingsStatus.className = 'settings-status';

  try {
    const res = await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: settingModel.value,
        apiKey: settingApiKey.value,
        systemPrompt: settingSystemPrompt.value,
        theme: selectedTheme,
      }),
    });

    if (!res.ok) throw new Error('Save failed');

    const updated = await res.json();
    settingApiKey.value = updated.apiKey;
    settingsStatus.textContent = 'Saved';
    settingsStatus.className = 'settings-status success';
    setTimeout(() => { settingsStatus.textContent = ''; }, 2000);
  } catch {
    settingsStatus.textContent = 'Failed to save';
    settingsStatus.className = 'settings-status error';
  }
});

// --- Markdown rendering ---

function renderMarkdown(text) {
  if (!text) return '';

  let html = escapeHtml(text);

  // Code blocks (``` ... ```)
  html = html.replace(/```(\w*)\n([\s\S]*?)```/g, (_, lang, code) => {
    return `<pre><code class="language-${lang}">${code.trim()}</code></pre>`;
  });

  // Inline code
  html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

  // Headers
  html = html.replace(/^### (.+)$/gm, '<h3>$1</h3>');
  html = html.replace(/^## (.+)$/gm, '<h2>$1</h2>');
  html = html.replace(/^# (.+)$/gm, '<h1>$1</h1>');

  // Bold
  html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');

  // Italic
  html = html.replace(/\*(.+?)\*/g, '<em>$1</em>');

  // Blockquote
  html = html.replace(/^&gt; (.+)$/gm, '<blockquote>$1</blockquote>');

  // Unordered lists
  html = html.replace(/^[*-] (.+)$/gm, '<li>$1</li>');
  html = html.replace(/(<li>.*<\/li>\n?)+/g, '<ul>$&</ul>');

  // Ordered lists
  html = html.replace(/^\d+\. (.+)$/gm, '<li>$1</li>');

  // Line breaks: convert double newlines to paragraphs
  html = html.split('\n\n').map(block => {
    block = block.trim();
    if (!block) return '';
    // Don't wrap block-level elements in <p>
    if (/^<(h[1-6]|pre|ul|ol|blockquote|li)/.test(block)) return block;
    return `<p>${block.replace(/\n/g, '<br>')}</p>`;
  }).join('\n');

  return html;
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}
