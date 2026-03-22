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
                    LazyVStack(spacing: 12) {
                        if appState.hasMoreHistory {
                            Button("Load earlier messages") {
                                Task { await appState.loadMoreHistory() }
                            }
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .padding(.top, 8)
                        }

                        ForEach(appState.timeline) { item in
                            switch item {
                            case .message(let message):
                                MessageBubble(message: message)
                                    .id(item.id)
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
                            }
                        }
                    }
                    .padding(.horizontal, 12)
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

            Divider()

            ComposerView(speechService: speechService) { text in
                Task { await appState.sendMessage(text) }
            } onCancel: {
                Task { await appState.cancelStreaming() }
            }
        }
        .navigationTitle(agent.name.capitalized)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .automatic) {
                HStack(spacing: 12) {
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

                    connectionStatusIndicator
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

    @ViewBuilder
    private var connectionStatusIndicator: some View {
        switch appState.connectionStatus {
        case .connected:
            Circle()
                .fill(.green)
                .frame(width: 8, height: 8)
        case .connecting, .reconnecting:
            ProgressView()
                .controlSize(.mini)
        case .disconnected:
            Circle()
                .fill(.red)
                .frame(width: 8, height: 8)
        }
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
