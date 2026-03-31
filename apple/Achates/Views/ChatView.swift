import SwiftUI

struct ChatView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var speechService = SpeechService()
    @State private var showAgentEditor = false
    @State private var breakToRemove: String? = nil
    @State private var showClearConfirmation = false
    @State private var isAtBottom = true

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

            ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 2) {
                            if appState.timeline.isEmpty && !appState.isStreaming {
                                emptyState
                            }

                            if appState.hasMoreHistory {
                                Button("Load earlier messages") {
                                    Task { await appState.loadMoreHistory() }
                                }
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .padding(.top, 8)
                                .padding(.bottom, 4)
                            }

                            let items = appState.timeline
                            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                                switch item {
                                case .message(let message):
                                    // Show timestamp if >5 min gap from previous message
                                    if let gap = timeGap(at: index, in: items), gap {
                                        if case .message(let msg) = items[index] {
                                            Text(msg.timestamp.formatted(date: .omitted, time: .shortened))
                                                .font(.caption2)
                                                .foregroundStyle(.quaternary)
                                                .padding(.top, 8)
                                                .padding(.bottom, 2)
                                        }
                                    }

                                    let position = bubblePosition(for: index, in: items)
                                    let isLast = isLastAssistantMessage(at: index, in: items)
                                    let isStreamingMsg = appState.isStreaming && appState.streamingMessageId == message.id
                                    MessageBubble(
                                        message: message,
                                        position: position,
                                        agent: agent,
                                        isLastAssistantMessage: isLast,
                                        isStreaming: isStreamingMsg,
                                        onRetry: isLast ? {
                                            if let lastUserText = lastUserMessageText(in: items) {
                                                Task { await appState.sendMessage(lastUserText) }
                                            }
                                        } : nil
                                    )
                                    .id(item.id)
                                    .padding(.top, position.topPadding)
                                    .transition(.opacity.animation(.easeIn(duration: 0.15)))
                                    .contextMenu {
                                        Button("Start new conversation here") {
                                            Task { await appState.addBreak(afterMessage: message) }
                                        }
                                    }

                                case .sessionBreak(_, let segmentId, let date):
                                    SessionBreakDivider(date: date) {
                                        #if os(iOS)
                                        breakToRemove = segmentId
                                        #else
                                        Task { await appState.removeBreak(segmentId: segmentId) }
                                        #endif
                                    }
                                    .id(item.id)
                                    .padding(.vertical, 4)
                                }
                            }

                            // Invisible anchor for scroll tracking
                            Color.clear.frame(height: 1)
                                .id("bottom-anchor")
                        }
                        .padding(.horizontal, 8)
                        .padding(.vertical, 8)
                    }
                    #if os(iOS)
                    .refreshable {
                        await appState.loadMoreHistory()
                    }
                    #endif
                    .onChange(of: appState.timeline.last?.id) { _, _ in
                        withAnimation(.easeOut(duration: 0.2)) {
                            proxy.scrollTo("bottom-anchor", anchor: .bottom)
                        }
                        isAtBottom = true
                    }
                    .onChange(of: lastMessageText) { _, _ in
                        proxy.scrollTo("bottom-anchor", anchor: .bottom)
                        isAtBottom = true
                    }
                    .onScrollGeometryChange(for: Bool.self) { geometry in
                        // Consider "at bottom" if within 100pt of the bottom
                        let offset = geometry.contentSize.height - geometry.contentOffset.y - geometry.containerSize.height
                        return offset < 100
                    } action: { _, newValue in
                        isAtBottom = newValue
                    }
                    .overlay(alignment: .bottomTrailing) {
                        if !isAtBottom {
                            Button {
                                withAnimation(.easeOut(duration: 0.2)) {
                                    proxy.scrollTo("bottom-anchor", anchor: .bottom)
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
                }

            ComposerView(speechService: speechService) { text in
                Task { await appState.sendMessage(text) }
            } onCancel: {
                Task { await appState.cancelStreaming() }
            }
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .alert("Remove Break", isPresented: .init(
            get: { breakToRemove != nil },
            set: { if !$0 { breakToRemove = nil } }
        )) {
            Button("Remove", role: .destructive) {
                if let segmentId = breakToRemove {
                    Task { await appState.removeBreak(segmentId: segmentId) }
                }
                breakToRemove = nil
            }
            Button("Cancel", role: .cancel) {
                breakToRemove = nil
            }
        } message: {
            Text("Merge these conversations?")
        }
        #endif
        .alert("Clear All Messages", isPresented: $showClearConfirmation) {
            Button("Delete", role: .destructive) {
                Task { await appState.clearTimeline() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Delete all messages with \(agent.displayName)? This cannot be undone.")
        }
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
                Button {
                    showAgentEditor = true
                } label: {
                    HStack(spacing: 8) {
                        AgentAvatar(agent: agent, size: 32)
                        VStack(alignment: .leading, spacing: 0) {
                            Text(agent.displayName)
                                .font(.system(size: 15, weight: .semibold))
                                .foregroundStyle(.primary)
                            Text(connectionLabel)
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(agent.displayName), \(connectionLabel)")
            }
            #else
            ToolbarItem(placement: .principal) {
                Button {
                    showAgentEditor = true
                } label: {
                    HStack(spacing: 8) {
                        AgentAvatar(agent: agent, size: 24)
                        VStack(alignment: .leading, spacing: 0) {
                            Text(agent.displayName)
                                .font(.system(size: 13, weight: .semibold))
                                .foregroundStyle(.primary)
                            Text(connectionLabel)
                                .font(.system(size: 10))
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(agent.displayName), \(connectionLabel)")
            }
            #endif

            ToolbarItem(placement: .automatic) {
                Menu {
                    Button {
                        showAgentEditor = true
                    } label: {
                        Label("Edit Agent", systemImage: "pencil")
                    }

                    Button(role: .destructive) {
                        showClearConfirmation = true
                    } label: {
                        Label("Clear All", systemImage: "trash")
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
        .task {
            await appState.selectAgent(agent)
        }
        .sheet(isPresented: $showAgentEditor) {
            NavigationStack {
                AgentEditView(agent: agent)
            }
            #if os(macOS)
            .frame(minWidth: 500, minHeight: 600)
            #endif
        }
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            Spacer()
            AgentAvatar(agent: agent, size: 72)
            Text(agent.displayName)
                .font(.title3.weight(.semibold))
            if !agent.description.isEmpty {
                Text(agent.description)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 32)
            }
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.vertical, 40)
    }

    /// Returns true if the message at this index has a >5 min gap from the previous message.
    private func timeGap(at index: Int, in items: [TimelineItem]) -> Bool? {
        guard index > 0,
              case .message(let current) = items[index],
              case .message(let previous) = items[index - 1] else { return nil }
        return current.timestamp.timeIntervalSince(previous.timestamp) > 300
    }

    /// Check if this is the last assistant message in the timeline.
    private func isLastAssistantMessage(at index: Int, in items: [TimelineItem]) -> Bool {
        guard case .message(let msg) = items[index], msg.role == .assistant else { return false }
        for i in stride(from: items.count - 1, through: index + 1, by: -1) {
            if case .message(let later) = items[i], later.role == .assistant {
                return false
            }
        }
        return true
    }

    /// Find the last user message text for retry.
    private func lastUserMessageText(in items: [TimelineItem]) -> String? {
        for item in items.reversed() {
            if case .message(let msg) = item, msg.role == .user {
                return msg.textContent
            }
        }
        return nil
    }

    /// Track the text content of the last message for auto-scrolling during streaming
    private var lastMessageText: String {
        guard let last = appState.timeline.last,
              case .message(let msg) = last else { return "" }
        return msg.textContent
    }

    private var connectionLabel: String {
        switch appState.connectionStatus {
        case .connected: return "Active now"
        case .connecting, .reconnecting: return "Connecting..."
        case .disconnected: return "Offline"
        }
    }

    /// Compute bubble grouping position for consecutive same-role messages.
    private func bubblePosition(for index: Int, in items: [TimelineItem]) -> BubblePosition {
        let current: MessageRole
        if case .message(let msg) = items[index] { current = msg.role } else { return .alone }

        let prevRole: MessageRole? = {
            guard index > 0, case .message(let msg) = items[index - 1] else { return nil }
            return msg.role
        }()
        let nextRole: MessageRole? = {
            guard index + 1 < items.count, case .message(let msg) = items[index + 1] else { return nil }
            return msg.role
        }()

        let sameAsPrev = prevRole == current
        let sameAsNext = nextRole == current

        if sameAsPrev && sameAsNext { return .middle }
        if sameAsPrev { return .last }
        if sameAsNext { return .first }
        return .alone
    }
}

/// A subtle date/time divider between sessions, like iMessage date headers.
private struct SessionBreakDivider: View {
    let date: Date
    let onRemove: () -> Void

    var body: some View {
        HStack {
            line
            Text(formatted)
                .font(.caption2)
                .foregroundStyle(.secondary)
                #if os(macOS)
                .contextMenu {
                    Button(role: .destructive) {
                        onRemove()
                    } label: {
                        Label("Remove Break", systemImage: "xmark")
                    }
                }
                #else
                .onLongPressGesture {
                    onRemove()
                }
                #endif
            line
        }
        .padding(.vertical, 8)
    }

    private var line: some View {
        Rectangle()
            .fill(.separator)
            .frame(height: 0.5)
    }

    private var formatted: String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) {
            return date.formatted(date: .omitted, time: .shortened)
        } else if calendar.isDateInYesterday(date) {
            return "Yesterday \(date.formatted(date: .omitted, time: .shortened))"
        } else if calendar.isDate(date, equalTo: Date(), toGranularity: .weekOfYear) {
            let formatter = DateFormatter()
            formatter.dateFormat = "EEEE h:mm a"
            return formatter.string(from: date)
        } else {
            return date.formatted(date: .abbreviated, time: .shortened)
        }
    }
}
