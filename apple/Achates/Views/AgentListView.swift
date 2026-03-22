import SwiftUI

struct AgentListView: View {
    @Environment(AppState.self) private var appState
    @State private var searchText = ""

    private var filteredAgents: [Agent] {
        if searchText.isEmpty { return appState.agents }
        return appState.agents.filter { $0.name.localizedCaseInsensitiveContains(searchText) }
    }

    var body: some View {
        Group {
            if appState.agents.isEmpty {
                VStack(spacing: 16) {
                    if appState.connectionStatus == .connecting || appState.connectionStatus == .reconnecting {
                        ProgressView()
                            .controlSize(.large)
                        Text("Connecting...")
                            .foregroundStyle(.secondary)
                    } else if appState.connectionStatus == .disconnected {
                        Image(systemName: "wifi.slash")
                            .font(.system(size: 48))
                            .foregroundStyle(.secondary)
                        Text("Not connected")
                            .foregroundStyle(.secondary)
                        Button("Connect") {
                            appState.connectToServer()
                        }
                        .buttonStyle(.borderedProminent)
                    } else {
                        Text("No agents available")
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                #if os(macOS)
                agentListMac
                #else
                List(filteredAgents) { agent in
                    NavigationLink(value: agent) {
                        AgentRow(agent: agent)
                    }
                    .listRowInsets(EdgeInsets(top: 4, leading: 16, bottom: 4, trailing: 16))
                }
                .listStyle(.plain)
                .searchable(text: $searchText, placement: .navigationBarDrawer(displayMode: .automatic), prompt: "Search")
                #endif
            }
        }
        .navigationTitle("Chats")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .toolbar {
            ToolbarItem(placement: .automatic) {
                NavigationLink(destination: SettingsView()) {
                    Image(systemName: "gear")
                }
            }
        }
        #if os(iOS)
        .navigationDestination(for: Agent.self) { agent in
            ChatView(agent: agent)
        }
        #endif
        .onAppear {
            if appState.connectionStatus == .disconnected && appState.serverURL != nil {
                appState.connectToServer()
            }
        }
    }

    #if os(macOS)
    @ViewBuilder
    private var agentListMac: some View {
        @Bindable var state = appState
        let selection = Binding<Agent.ID?>(
            get: { appState.currentAgent?.id },
            set: { id in
                if let agent = appState.agents.first(where: { $0.id == id }) {
                    appState.currentAgent = agent
                }
            }
        )
        List(appState.agents, selection: selection) { agent in
            AgentRow(agent: agent)
                .tag(agent.id)
        }
    }
    #endif
}

struct AgentAvatar: View {
    let agent: Agent
    var size: CGFloat = 60

    var body: some View {
        if let avatarImage = agent.avatarImage {
            #if os(macOS)
            Image(nsImage: avatarImage)
                .resizable()
                .scaledToFill()
                .frame(width: size, height: size)
                .clipShape(Circle())
            #else
            Image(uiImage: avatarImage)
                .resizable()
                .scaledToFill()
                .frame(width: size, height: size)
                .clipShape(Circle())
            #endif
        } else {
            ZStack {
                Circle()
                    .fill(.blue.gradient)
                    .frame(width: size, height: size)
                Text(agent.initials)
                    .font(.system(size: size * 0.4, weight: .semibold))
                    .foregroundStyle(.white)
            }
        }
    }
}

private struct AgentRow: View {
    let agent: Agent

    var body: some View {
        HStack(spacing: 12) {
            AgentAvatar(agent: agent, size: 56)

            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(agent.name.capitalized)
                        .font(.system(size: 16, weight: .semibold))
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    if let date = agent.lastActivity {
                        Text(formatTimestamp(date))
                            .font(.system(size: 13))
                            .foregroundStyle(agent.unreadCount > 0 ? .blue : .secondary)
                    }
                }

                HStack(spacing: 6) {
                    Text(previewText)
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)

                    if agent.unreadCount > 0 {
                        Spacer(minLength: 0)
                        Circle()
                            .fill(.blue)
                            .frame(width: 12, height: 12)
                    }
                }
            }
        }
        .padding(.vertical, 4)
    }

    private var previewText: String {
        if let msg = agent.lastMessage, !msg.isEmpty {
            return msg
        }
        if !agent.description.isEmpty {
            return agent.description
        }
        return "No messages yet"
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

extension Agent: Hashable {
    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }
}
