import SwiftUI

struct ChatView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var speechService = SpeechService()
    @State private var isAtBottom = true
    @State private var scrollPos = ScrollPosition(idType: String.self)
    @State private var showAgentEditor = false
    @State private var hasNewMessages = false
    @State private var findText = ""
    @State private var showFind = false
    @State private var findIndex = 0
    @FocusState private var isFindFocused: Bool
    @State private var showRename = false
    @State private var renameText = ""
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var draft: ConversationDraft {
        appState.draft(for: agent.id, sessionID: appState.currentSessionId ?? "")
    }

    private var conversationTitle: String {
        appState.sessions.first { $0.id == appState.currentSessionId }?.title ?? "New Conversation"
    }

    private var matches: [ChatMessage] {
        guard !findText.isEmpty else { return [] }
        return visibleMessages.filter { $0.textContent.localizedCaseInsensitiveContains(findText) }
    }
    @State private var showConversation = false
    @State private var showVoiceSetup = false
    @State private var priorSpeechEnabled = false

    /// Live agent data from AppState, falls back to the navigation snapshot.
    private var liveAgent: Agent {
        appState.agents.first { $0.id == agent.id } ?? agent
    }
    @AppStorage("show_tool_activity") private var showToolActivity = false

    var body: some View {
        VStack(spacing: 0) {
            #if os(iOS)
            if !appState.usesSplitNavigation { ConnectionStatusBanner() }
            #endif
            if showFind {
                HStack {
                    TextField("Find in conversation", text: $findText)
                        .textFieldStyle(.roundedBorder)
                        .focused($isFindFocused)
                        .onSubmit { findNext() }
                    Text("\(matches.count) matches").font(.caption).foregroundStyle(.secondary)
                    Button("Next", action: findNext).disabled(matches.isEmpty)
                    Button("Done") { showFind = false; findText = "" }
                }
                .padding(10)
            }
            if appState.isLoadingHistory {
                ProgressView("Loading conversation…").padding()
            } else if let error = appState.historyLoadError {
                InlineNotice(message: error, actionTitle: "Retry") {
                    if let id = appState.currentSessionId {
                        Task { await appState.openSession(id, for: agent) }
                    }
                }
            }

            ScrollView {
                // Eager VStack (not LazyVStack): real bubble heights keep the
                // content size stable. A LazyVStack estimates off-screen heights,
                // and that estimate lurches when the composer/keyboard resizes the
                // viewport — flinging the bottom-anchored offset into blank space
                // mid-conversation. Eager layout costs more on very long sessions
                // but is correct; window the history later if it ever bites.
                VStack(spacing: 2) {
                    let items = visibleMessages
                    if items.isEmpty && !appState.isStreaming && !appState.isLoadingHistory && appState.historyLoadError == nil {
                        emptyState
                    }

                    ForEach(Array(items.enumerated()), id: \.element.id) { index, message in
                        // Show timestamp if >5 min gap from previous message
                        if let label = timestampLabel(at: index, in: items) {
                            Text(label)
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                                .padding(.top, 8)
                                .padding(.bottom, 2)
                        }

                        let position = bubblePosition(for: index, in: items)
                        let isLast = isLastAssistantMessage(at: index, in: items)
                        let isLastUser = isLastUserMessage(at: index, in: items)
                        let isStreamingMsg = appState.isStreaming && appState.streamingMessageId == message.id
                        let canResubmit = !appState.isStreaming && hasResubmittableUserMessage
                        MessageBubble(
                            message: message,
                            position: position,
                            agent: agent,
                            isLastAssistantMessage: isLast,
                            isLastUserMessage: isLastUser,
                            isStreaming: isStreamingMsg,
                            onResubmit: resubmitAction(
                                for: message,
                                canResubmit: canResubmit,
                                isLastAssistant: isLast),
                            onBeginEdit: (canResubmit && isLastUser) ? {
                                beginEditingLastUserMessage()
                            } : nil
                        )
                        .id(message.id)
                        .padding(.top, position.topPadding)
                        .transition(.opacity.animation(.easeIn(duration: 0.15)))
                    }
                }
                .scrollTargetLayout()
                .frame(maxWidth: InterfaceMetrics.readingWidth)
                .frame(maxWidth: .infinity)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
            }
            .scrollPosition($scrollPos)
            .defaultScrollAnchor(.bottom, for: .sizeChanges)
            .onChange(of: appState.messages.count) { _, _ in
                guard appState.messages.last != nil else { return }
                guard isAtBottom else { hasNewMessages = true; return }
                Task { @MainActor in
                    await Task.yield()
                    scrollPos.scrollTo(edge: .bottom)
                }
            }
            .onChange(of: isAtBottom) { _, atBottom in
                if atBottom { hasNewMessages = false }
            }
            .onChange(of: findText) { _, _ in findIndex = 0 }
            .onChange(of: showFind) { _, visible in isFindFocused = visible }
            #if os(iOS)
            .scrollDismissesKeyboard(.interactively)
            #endif
            .modifier(ScrollBottomDetector(isAtBottom: $isAtBottom))
            .overlay(alignment: .bottomTrailing) {
                if !isAtBottom {
                    Button {
                        withAnimation(reduceMotion ? nil : .easeOut(duration: 0.2)) {
                            scrollPos.scrollTo(edge: .bottom)
                        }
                        isAtBottom = true
                    } label: {
                        Label(hasNewMessages ? "New messages" : "Latest messages", systemImage: "arrow.down")
                            .font(.callout.weight(.medium))
                            .padding(10)
                            .background(.regularMaterial, in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .padding(12)
                    .transition(.opacity.animation(.easeInOut(duration: 0.2)))
                    .accessibilityLabel("Scroll to bottom")
                }
            }

            if appState.failedSend != nil {
                failedSendBanner
            }

            if let notice = appState.currentConversation.interruption, !appState.isStreaming {
                HStack(spacing: 8) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    Text(notice).font(.footnote)
                    Spacer()
                    if appState.currentConversation.canContinue {
                        Button("Continue") {
                            Task { await appState.continueResponse() }
                        }
                        .disabled(!appState.canSubmitMessage)
                        .font(.footnote.weight(.semibold))
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color.orange.opacity(0.12))
            }

            ComposerView(
                speechService: speechService,
                draft: draft,
                onSend: { text, attachments in
                    isAtBottom = true
                    Task { await appState.sendMessage(text, attachments: attachments) }
                },
                onResubmit: { text, attachments in
                    isAtBottom = true
                    Task { await appState.resubmitLast(text: text, attachments: attachments) }
                },
                onCancel: {
                    Task { await appState.cancelStreaming() }
                }
            )
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        #if os(macOS)
        .onKeyPress(.escape) {
            if appState.isStreaming {
                Task { await appState.cancelStreaming() }
                return .handled
            }
            return .ignored
        }
        #endif
        .toolbar {
            ToolbarItem(placement: .principal) {
                Button { showAgentEditor = true } label: {
                    HStack(spacing: 8) {
                        AgentAvatar(agent: liveAgent, size: 28)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(conversationTitle).font(.headline).lineLimit(1)
                            Text(liveAgent.displayName).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .foregroundStyle(.primary)
                }
                .buttonStyle(.plain)
                .help("Edit \(liveAgent.displayName)")
                .accessibilityLabel("\(conversationTitle), \(liveAgent.displayName). Edit agent")
            }
            ToolbarItemGroup(placement: .primaryAction) {
                #if os(iOS)
                conversationButton
                Menu {
                    Button { Task { await appState.toggleSpeechForCurrentSession() } } label: {
                        Label(appState.currentSpeechEnabled ? "Stop Reading Replies Aloud" : "Read Replies Aloud", systemImage: "speaker.wave.2")
                    }
                    .disabled(appState.connectionStatus != .connected)
                    Button("Find in Conversation", systemImage: "magnifyingglass") { showFind = true }
                    Button("Rename Conversation…", systemImage: "pencil", action: beginRename)
                        .disabled(!canRename)
                    Button("Edit Agent", systemImage: "person.crop.circle") { showAgentEditor = true }
                } label: { Label("Conversation Actions", systemImage: "ellipsis.circle") }
                #else
                speechToggleButton
                Button { showFind.toggle() } label: {
                    Label("Find in Conversation", systemImage: "magnifyingglass")
                }
                .keyboardShortcut("f", modifiers: .command)
                .help("Find in conversation")
                Menu {
                    Button("Rename Conversation…", systemImage: "pencil", action: beginRename)
                        .disabled(!canRename)
                    Button("Edit Agent…", systemImage: "person.crop.circle") { showAgentEditor = true }
                } label: { Label("Conversation Actions", systemImage: "ellipsis.circle") }
                .help("Conversation actions")
                #endif
            }
        }
        .focusedSceneValue(\.findConversation, InterfaceCommand { showFind = true })
        .focusedSceneValue(\.renameConversation, InterfaceCommand(isEnabled: canRename, perform: beginRename))
        .alert("Rename Conversation", isPresented: $showRename) {
            TextField("Conversation title", text: $renameText)
            Button("Rename") {
                if let id = appState.currentSessionId {
                    let title = renameText.trimmingCharacters(in: .whitespacesAndNewlines)
                    Task { await appState.renameSession(id, title: title, for: agent) }
                }
            }
            .disabled(renameText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            Button("Cancel", role: .cancel) {}
        }
        .sheet(isPresented: $showAgentEditor) {
            NavigationStack { AgentEditView(agent: liveAgent) }
                #if os(macOS)
                .frame(minWidth: 540, idealWidth: 620, minHeight: 600)
                #endif
        }
        #if os(iOS)
        .alert("No Voice Configured", isPresented: $showVoiceSetup) {
            Button("Edit Agent") { showAgentEditor = true }
            Button("Continue with Text Replies") { startConversation(checkVoice: false) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Choose a voice for \(liveAgent.displayName) to hear replies, or continue using dictation with text replies.")
        }
        .fullScreenCover(isPresented: $showConversation, onDismiss: {
            Task { @MainActor in await appState.setSpeechForCurrentSession(priorSpeechEnabled) }
        }) {
            NavigationStack {
                ConversationView(agent: agent)
            }
            .environment(appState)
        }
        #endif
    }

    #if os(iOS)
    @ViewBuilder
    private var conversationButton: some View {
        Button {
            startConversation()
        } label: {
            Image(systemName: "waveform.circle")
                .accessibilityLabel("Start voice conversation")
        }
        .disabled(appState.connectionStatus != .connected)
    }

    /// Ensure a session exists, force speech on for the call, and present the
    /// full-screen conversation view. The prior speech setting is restored when
    /// the cover is dismissed.
    private func startConversation(checkVoice: Bool = true) {
        Task {
            if checkVoice {
                do {
                    let config = try await appState.loadAgentConfig(liveAgent)
                    if config.voice?.isEmpty != false { showVoiceSetup = true; return }
                } catch {
                    appState.error = "Couldn’t check voice settings. \(error.localizedDescription)"
                    return
                }
            }
            if appState.currentSessionId == nil,
               let id = await appState.createSession(for: agent) {
                await appState.openSession(id, for: agent)
            }
            guard appState.currentSessionId != nil else { return }
            priorSpeechEnabled = appState.currentSpeechEnabled
            await appState.setSpeechForCurrentSession(true)
            showConversation = true
        }
    }
    #endif

    @ViewBuilder
    private var speechToggleButton: some View {
        let on = appState.currentSpeechEnabled
        Button {
            Task { await appState.toggleSpeechForCurrentSession() }
        } label: {
            Image(systemName: on ? "speaker.wave.2.fill" : "speaker.slash")
                .accessibilityLabel(on ? "Stop reading replies aloud" : "Read replies aloud")
        }
        .disabled(appState.currentSessionId == nil || appState.connectionStatus != .connected)
        .help(on ? "Stop reading replies aloud" : "Read replies aloud")
    }

    /// Inline strip above the composer when a chat.send never reached the server.
    private var failedSendBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
            Text("Message failed to send.")
                .font(.footnote)
            Spacer()
            Button("Retry Sending") {
                Task { await appState.retryFailedSend() }
            }
            .disabled(!appState.canSubmitMessage)
            .font(.footnote.weight(.semibold))
            Button {
                appState.failedSend = nil
            } label: {
                Image(systemName: "xmark")
                    .frame(width: InterfaceMetrics.actionSize, height: InterfaceMetrics.actionSize)
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("Dismiss")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color.red.opacity(0.12))
        .accessibilityElement(children: .combine)
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            Spacer()
            AgentAvatar(agent: liveAgent, size: 72)
            Text(liveAgent.displayName)
                .font(.title3.weight(.semibold))
            Text("Start a conversation")
                .font(.subheadline)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.vertical, 40)
    }

    private func timestampLabel(at index: Int, in items: [ChatMessage]) -> String? {
        let date = items[index].timestamp
        if index == 0 || !Calendar.current.isDate(date, inSameDayAs: items[index - 1].timestamp) {
            return date.formatted(date: .abbreviated, time: .shortened)
        }
        return date.timeIntervalSince(items[index - 1].timestamp) > 300
            ? date.formatted(date: .omitted, time: .shortened) : nil
    }

    private func findNext() {
        guard !matches.isEmpty else { return }
        scrollPos.scrollTo(id: matches[findIndex % matches.count].id, anchor: .center)
        findIndex += 1
    }

    private func isLastAssistantMessage(at index: Int, in items: [ChatMessage]) -> Bool {
        guard items[index].role == .assistant else { return false }
        for i in stride(from: items.count - 1, through: index + 1, by: -1) {
            if items[i].role == .assistant {
                return false
            }
        }
        return true
    }

    private func isLastUserMessage(at index: Int, in items: [ChatMessage]) -> Bool {
        guard items[index].role == .user else { return false }
        for i in stride(from: items.count - 1, through: index + 1, by: -1) {
            if items[i].role == .user {
                return false
            }
        }
        return true
    }

    private var hasResubmittableUserMessage: Bool {
        appState.messages.contains(where: { $0.role == .user })
    }

    /// Resubmit any user prompt, or retry the last assistant reply (which rewinds
    /// the latest user turn). Returns nil when resubmit isn't available.
    private func resubmitAction(for message: ChatMessage, canResubmit: Bool, isLastAssistant: Bool) -> (() -> Void)? {
        guard canResubmit else { return nil }
        if message.role == .user {
            // 0-based user-turn ordinal: count user messages before this one.
            guard let idx = appState.messages.firstIndex(where: { $0.id == message.id }) else { return nil }
            let ordinal = appState.messages[..<idx].lazy.filter { $0.role == .user }.count
            let id = message.id
            return { Task { await appState.resubmit(promptIndex: ordinal, messageId: id) } }
        }
        if message.role == .assistant && isLastAssistant {
            return { Task { await appState.resubmitLast(text: nil, attachments: nil) } }
        }
        return nil
    }

    private func beginEditingLastUserMessage() {
        guard let original = appState.lastUserMessage else { return }
        draft.beginEditing(.init(text: original.textContent, attachments: appState.draftAttachments(from: original)))
    }

    private var visibleMessages: [ChatMessage] {
        if showToolActivity { return appState.messages }
        return appState.messages.filter { message in
            // Always show the currently streaming message
            if appState.isStreaming && message.id == appState.streamingMessageId {
                return true
            }
            // Keep if message has any non-tool-call blocks, or any still-running tool calls
            return message.blocks.contains { block in
                if case .toolCall(_, _, let status, _) = block {
                    return status == .running || status == .failed
                }
                return true
            }
        }
    }

    private var canRename: Bool {
        appState.currentSessionId != nil && appState.connectionStatus == .connected
    }

    private func beginRename() {
        renameText = conversationTitle
        showRename = true
    }

    private func bubblePosition(for index: Int, in items: [ChatMessage]) -> BubblePosition {
        let current = items[index].role

        let prevRole: MessageRole? = index > 0 ? items[index - 1].role : nil
        let nextRole: MessageRole? = index + 1 < items.count ? items[index + 1].role : nil

        let sameAsPrev = prevRole == current
        let sameAsNext = nextRole == current

        if sameAsPrev && sameAsNext { return .middle }
        if sameAsPrev { return .last }
        if sameAsNext { return .first }
        return .alone
    }
}

/// Wraps onScrollGeometryChange with an availability check for iOS 18+/macOS 15+.
private struct ScrollBottomDetector: ViewModifier {
    @Binding var isAtBottom: Bool

    func body(content: Content) -> some View {
        if #available(iOS 18.0, macOS 15.0, *) {
            content.onScrollGeometryChange(for: Bool.self) { geometry in
                let offset = geometry.contentSize.height - geometry.contentOffset.y - geometry.containerSize.height
                return offset < 100
            } action: { _, newValue in
                isAtBottom = newValue
            }
        } else {
            content
        }
    }
}
