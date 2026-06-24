import SwiftUI

struct SessionListView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent
    @State private var sessionToRename: SessionInfo?
    @State private var renameText = ""
    @State private var showDeleteAll = false
    @State private var showCosts = false

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
                        Task {
                            if let sessionId = await appState.createSession(for: agent) {
                                #if os(iOS)
                                appState.navigationPath.append(SessionSelection(agent: agent, sessionId: sessionId))
                                #else
                                appState.currentSessionId = sessionId
                                appState.messages = []
                                #endif
                            }
                        }
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
            ToolbarItem(placement: .automatic) {
                HStack(spacing: 12) {
                    Button {
                        showCosts = true
                    } label: {
                        Image(systemName: "chart.bar")
                    }
                    .accessibilityLabel("Costs")

                    Button {
                        Task {
                            if let sessionId = await appState.createSession(for: agent) {
                                #if os(iOS)
                                appState.navigationPath.append(SessionSelection(agent: agent, sessionId: sessionId))
                                #else
                                appState.currentSessionId = sessionId
                                appState.messages = []
                                #endif
                            }
                        }
                    } label: {
                        Image(systemName: "square.and.pencil")
                    }
                    .accessibilityLabel("New Chat")
                }
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
        HStack {
            if session.unread > 0 {
                Circle()
                    .fill(.blue)
                    .frame(width: 8, height: 8)
            }
            Text(session.title ?? "New conversation")
                .font(.system(size: 15, weight: .medium))
                .lineLimit(1)
            Spacer(minLength: 4)
            Text(formatTimestamp(session.updated))
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
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

    private func formatTimestamp(_ date: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) {
            let formatter = DateFormatter()
            formatter.dateFormat = "h:mm a"
            return formatter.string(from: date)
        } else if calendar.isDateInYesterday(date) {
            return "Yesterday"
        } else if let weekAgo = calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: Date())),
                  date >= weekAgo {
            let formatter = DateFormatter()
            formatter.dateFormat = "EEE"
            return formatter.string(from: date)
        } else {
            let formatter = DateFormatter()
            formatter.dateStyle = .short
            return formatter.string(from: date)
        }
    }
}
