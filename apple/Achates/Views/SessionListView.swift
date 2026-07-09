import SwiftUI

struct SessionListView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var sessionToRename: SessionInfo?
    @State private var renameText = ""
    @State private var showDeleteAll = false
    @State private var showCosts = false
    @ScaledMetric(relativeTo: .subheadline) private var unreadDotSize: CGFloat = 8

    var body: some View {
        Group {
            if appState.sessions.isEmpty && appState.connectionStatus == .connected {
                VStack(spacing: 16) {
                    Image(systemName: "bubble.left.and.text.bubble.right")
                        .font(.system(size: 48))
                        .foregroundStyle(.secondary)
                    Text("No conversations yet")
                        .foregroundStyle(.secondary)
                    Button("Start a Conversation") {
                        Task { await appState.startNewConversation(for: agent) }
                    }
                    .buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                sessionList
            }
        }
        .navigationTitle(agent.displayName)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    Task { await appState.startNewConversation(for: agent) }
                } label: {
                    Image(systemName: "square.and.pencil")
                }
                .accessibilityLabel("New Chat")

                Menu {
                    Button {
                        showCosts = true
                    } label: {
                        Label("Costs…", systemImage: "chart.bar")
                    }

                    Divider()

                    Button(role: .destructive) {
                        showDeleteAll = true
                    } label: {
                        Label("Delete All Conversations…", systemImage: "trash")
                    }
                    .disabled(appState.sessions.isEmpty)
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .accessibilityLabel("More actions")
            }
        }
        .sheet(isPresented: $showCosts) {
            NavigationStack {
                CostsView(agent: agent)
            }
            #if os(macOS)
            .frame(minWidth: 450, minHeight: 500)
            #endif
        }
        .task {
            await appState.selectAgent(agent)
        }
        .alert("Rename Conversation", isPresented: .init(
            get: { sessionToRename != nil },
            set: { if !$0 { sessionToRename = nil } }
        )) {
            TextField("Title", text: $renameText)
            Button("Rename") {
                if let session = sessionToRename {
                    Task { await appState.renameSession(session.id, title: renameText, for: agent) }
                }
                sessionToRename = nil
            }
            Button("Cancel", role: .cancel) { sessionToRename = nil }
        }
        .alert("Delete All Conversations", isPresented: $showDeleteAll) {
            Button("Delete All", role: .destructive) {
                Task { await appState.deleteAllSessions(for: agent) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Delete all conversations with \(agent.displayName)? This cannot be undone.")
        }
    }

    @ViewBuilder
    private var sessionList: some View {
        List(selection: Binding<String?>(
            get: { appState.currentSessionId },
            set: { newValue in
                if let id = newValue {
                    appState.currentSessionId = id
                    Task { await appState.openSession(id, for: agent) }
                }
            }
        )) {
            let grouped = groupedSessions
            ForEach(grouped, id: \.label) { group in
                Section(group.label) {
                    ForEach(group.sessions) { session in
                        #if os(macOS)
                        sessionRow(session)
                            .tag(session.id)
                        #else
                        NavigationLink(value: SessionSelection(agent: agent, sessionId: session.id)) {
                            sessionRow(session)
                        }
                        #endif
                    }
                    .onDelete { offsets in
                        let sessionsInGroup = group.sessions
                        for offset in offsets {
                            let session = sessionsInGroup[offset]
                            Task { await appState.deleteSession(session.id, for: agent) }
                        }
                    }
                }
            }

            if appState.hasMoreSessions {
                Button("Load more...") {
                    Task { await appState.loadMoreSessions(for: agent) }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
        #if os(iOS)
        .listStyle(.insetGrouped)
        #endif
        #if os(iOS)
        .refreshable {
            await appState.loadSessions(for: agent)
        }
        .navigationDestination(for: SessionSelection.self) { selection in
            ChatView(agent: selection.agent)
                .task { await appState.openSession(selection.sessionId, for: agent) }
        }
        #endif
    }

    @ViewBuilder
    private func sessionRow(_ session: SessionInfo) -> some View {
        HStack(spacing: 8) {
            if session.unread > 0 {
                Circle()
                    .fill(.tint)
                    .frame(width: unreadDotSize, height: unreadDotSize)
            }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(session.title ?? "New conversation")
                        .font(.listRowTitle)
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    Text(session.updated.chatListLabel())
                        .font(.listRowCaption)
                        .foregroundStyle(session.unread > 0 ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                }
                if let preview = session.preview, !preview.isEmpty {
                    Text(preview)
                        .font(.listRowSubtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(session.title ?? "New conversation")
        .accessibilityValue(session.unread > 0 ? "unread" : "")
        .contextMenu {
            Button {
                sessionToRename = session
                renameText = session.title ?? ""
            } label: {
                Label("Rename", systemImage: "pencil")
            }

            Button(role: .destructive) {
                Task { await appState.deleteSession(session.id, for: agent) }
            } label: {
                Label("Delete", systemImage: "trash")
            }
        }
    }

    // MARK: - Grouping

    private struct SessionGroup {
        let label: String
        let sessions: [SessionInfo]
    }

    private var groupedSessions: [SessionGroup] {
        let calendar = Calendar.current
        var today: [SessionInfo] = []
        var yesterday: [SessionInfo] = []
        var thisWeek: [SessionInfo] = []
        var earlier: [SessionInfo] = []

        for session in appState.sessions {
            if calendar.isDateInToday(session.updated) {
                today.append(session)
            } else if calendar.isDateInYesterday(session.updated) {
                yesterday.append(session)
            } else if let weekAgo = calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: Date())),
                      session.updated >= weekAgo {
                thisWeek.append(session)
            } else {
                earlier.append(session)
            }
        }

        var groups: [SessionGroup] = []
        if !today.isEmpty { groups.append(SessionGroup(label: "Today", sessions: today)) }
        if !yesterday.isEmpty { groups.append(SessionGroup(label: "Yesterday", sessions: yesterday)) }
        if !thisWeek.isEmpty { groups.append(SessionGroup(label: "This Week", sessions: thisWeek)) }
        if !earlier.isEmpty { groups.append(SessionGroup(label: "Earlier", sessions: earlier)) }
        return groups
    }

}
