import SwiftUI

struct ChatView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var speechService = SpeechService()
    @State private var showAgentEditor = false

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 2) {
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
                                let position = bubblePosition(for: index, in: items)
                                MessageBubble(message: message, position: position, agent: agent)
                                    .id(item.id)
                                    .padding(.top, position.topPadding)
                                    .contextMenu {
                                        Button("Start new conversation here") {
                                            Task { await appState.addBreak(afterMessage: message) }
                                        }
                                    }

                            case .sessionBreak(_, let segmentId, let date):
                                SessionBreakDivider(date: date) {
                                    Task { await appState.removeBreak(segmentId: segmentId) }
                                }
                                .id(item.id)
                                .padding(.vertical, 4)
                            }
                        }
                    }
                    .padding(.horizontal, 8)
                    .padding(.vertical, 8)
                }
                .onChange(of: appState.timeline.last?.id) { _, _ in
                    withAnimation(.easeOut(duration: 0.2)) {
                        if let lastId = appState.timeline.last?.id {
                            proxy.scrollTo(lastId, anchor: .bottom)
                        }
                    }
                }
                .onChange(of: lastMessageText) { _, _ in
                    if let lastId = appState.timeline.last?.id {
                        proxy.scrollTo(lastId, anchor: .bottom)
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
            }
            #else
            ToolbarItem(placement: .automatic) {
                Text(agent.displayName)
                    .font(.headline)
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
                        Task { await appState.clearTimeline() }
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
        }
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
                .onLongPressGesture {
                    onRemove()
                }
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
