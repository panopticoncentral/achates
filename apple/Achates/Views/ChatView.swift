import SwiftUI

struct ChatView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var speechService = SpeechService()
    @State private var isAtBottom = true
    @State private var scrollPos = ScrollPosition(idType: String.self)
    @State private var pendingEdit: ComposerView.PendingEdit?
    @State private var showConversation = false
    @State private var priorSpeechEnabled = false

    /// Live agent data from AppState, falls back to the navigation snapshot.
    private var liveAgent: Agent {
        appState.agents.first { $0.id == agent.id } ?? agent
    }
    @AppStorage("show_tool_activity") private var showToolActivity = false

    var body: some View {
        VStack(spacing: 0) {
            // Connection status banner
            if appState.connectionStatus == .disconnected {
                HStack(spacing: 6) {
                    Image(systemName: "wifi.slash")
                        .font(.caption)
                    Text("No connection")
                        .font(.caption.weight(.medium))
                }
                .foregroundStyle(.white)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
                .background(.red.opacity(0.85))
                .accessibilityLabel("Disconnected from server")
            } else if appState.connectionStatus == .reconnecting {
                HStack(spacing: 6) {
                    ProgressView()
                        .controlSize(.mini)
                        .tint(.white)
                    Text("Reconnecting...")
                        .font(.caption.weight(.medium))
                }
                .foregroundStyle(.white)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
                .background(.orange.opacity(0.85))
                .accessibilityLabel("Reconnecting to server")
            }

            ScrollView {
                        LazyVStack(spacing: 2) {
                            let items = visibleMessages
                            if items.isEmpty && !appState.isStreaming {
                                emptyState
                            }

                            ForEach(Array(items.enumerated()), id: \.element.id) { index, message in
                                // Show timestamp if >5 min gap from previous message
                                if let gap = timeGap(at: index, in: items), gap {
                                    Text(items[index].timestamp.formatted(date: .omitted, time: .shortened))
                                        .font(.caption2)
                                        .foregroundStyle(.quaternary)
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
                        .padding(.horizontal, 8)
                        .padding(.vertical, 8)
                    }
                    .scrollPosition($scrollPos)
                    .defaultScrollAnchor(.bottom, for: .sizeChanges)
                    .onChange(of: appState.messages.count) { _, _ in
                        // New message added — always scroll to bottom.
                        // Defer one tick so the LazyVStack lays out the new
                        // bubbles before the animation captures its target.
                        guard appState.messages.last != nil else { return }
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(16))
                            withAnimation(.easeOut(duration: 0.2)) {
                                scrollPos.scrollTo(edge: .bottom)
                            }
                            isAtBottom = true
                        }
                    }
                    .modifier(ScrollBottomDetector(isAtBottom: $isAtBottom))
                    .overlay(alignment: .bottomTrailing) {
                        if !isAtBottom {
                            Button {
                                withAnimation(.easeOut(duration: 0.2)) {
                                    scrollPos.scrollTo(edge: .bottom)
                                }
                                isAtBottom = true
                            } label: {
                                Image(systemName: "chevron.down.circle.fill")
                                    .font(.title2)
                                    .symbolRenderingMode(.palette)
                                    .foregroundStyle(.primary, Color(.systemGray5))
                                    .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                                }
                            .buttonStyle(.plain)
                            .padding(12)
                            .transition(.opacity.animation(.easeInOut(duration: 0.2)))
                            .accessibilityLabel("Scroll to bottom")
                        }
                    }

            ComposerView(
                speechService: speechService,
                pendingEdit: $pendingEdit,
                onSend: { text, attachments in
                    Task { await appState.sendMessage(text, attachments: attachments) }
                },
                onResubmit: { text, attachments in
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
            #if os(iOS)
            ToolbarItem(placement: .principal) {
                HStack(spacing: 8) {
                    AgentAvatar(agent: liveAgent, size: 32)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(liveAgent.displayName)
                            .font(.system(size: 15, weight: .semibold))
                            .foregroundStyle(.primary)
                        if let label = connectionLabel {
                            Text(label)
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .accessibilityLabel(connectionLabel.map { "\(liveAgent.displayName), \($0)" } ?? liveAgent.displayName)
            }
            ToolbarItem(placement: .topBarTrailing) {
                HStack(spacing: 12) {
                    conversationButton
                    speechToggleButton
                }
            }
            #else
            ToolbarItem(placement: .principal) {
                HStack(spacing: 8) {
                    AgentAvatar(agent: liveAgent, size: 24)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(liveAgent.displayName)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.primary)
                        if let label = connectionLabel {
                            Text(label)
                                .font(.system(size: 10))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .accessibilityLabel(connectionLabel.map { "\(liveAgent.displayName), \($0)" } ?? liveAgent.displayName)
            }
            ToolbarItem(placement: .primaryAction) {
                speechToggleButton
            }
            #endif
        }
        #if os(iOS)
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
    private func startConversation() {
        Task {
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
                .accessibilityLabel(on ? "Disable speech for this session" : "Enable speech for this session")
        }
        .disabled(appState.currentSessionId == nil)
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

    private func timeGap(at index: Int, in items: [ChatMessage]) -> Bool? {
        guard index > 0 else { return nil }
        let current = items[index]
        let previous = items[index - 1]
        return current.timestamp.timeIntervalSince(previous.timestamp) > 300
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
        pendingEdit = ComposerView.PendingEdit(
            text: original.textContent,
            attachments: appState.draftAttachments(from: original)
        )
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
                    return status == .running
                }
                return true
            }
        }
    }

    private var connectionLabel: String? {
        switch appState.connectionStatus {
        case .connected: return nil
        case .connecting, .reconnecting: return "Connecting..."
        case .disconnected: return "Offline"
        }
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
