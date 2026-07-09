import SwiftUI

struct AgentListView: View {
    @Environment(AppState.self) private var appState
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    #endif
    @State private var searchText = ""
    @State private var agentToEdit: Agent?

    private var filteredAgents: [Agent] {
        if searchText.isEmpty { return appState.agents }
        return appState.agents.filter { $0.displayName.localizedCaseInsensitiveContains(searchText) }
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
                    .contextMenu {
                        Button {
                            agentToEdit = agent
                        } label: {
                            Label("Edit Agent", systemImage: "pencil")
                        }
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
            #if os(macOS)
            ToolbarItemGroup(placement: .automatic) {
                Button {
                    openWindow(id: "system")
                } label: {
                    Image(systemName: "wrench.and.screwdriver")
                }
                .accessibilityLabel("System")
                .help("Memory, scheduled jobs, and default models")

                SettingsLink {
                    Image(systemName: "gear")
                }
                .help("Settings")
            }
            #else
            ToolbarItem(placement: .automatic) {
                NavigationLink(destination: SettingsView()) {
                    Image(systemName: "gear")
                }
                .accessibilityLabel("Settings")
            }
            #endif
        }
        .onAppear {
            if appState.connectionStatus == .disconnected && appState.serverURL != nil {
                appState.connectToServer()
            }
        }
        .sheet(item: $agentToEdit) { agent in
            NavigationStack {
                AgentEditView(agent: agent)
            }
            #if os(macOS)
            .frame(minWidth: 500, minHeight: 600)
            #endif
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
                    Task { await appState.selectAgent(agent) }
                }
            }
        )
        List(filteredAgents, selection: selection) { agent in
            AgentRow(agent: agent)
                .tag(agent.id)
                .contextMenu {
                    Button {
                        agentToEdit = agent
                    } label: {
                        Label("Edit Agent", systemImage: "pencil")
                    }
                }
        }
        .searchable(text: $searchText, prompt: "Search")
        // The lists stay populated (and stale) through drops/reconnects; without
        // this, only the open chat shows any sign the connection is down.
        .safeAreaInset(edge: .bottom, spacing: 0) {
            ConnectionStatusBanner()
        }
    }
    #endif
}

struct AgentAvatar: View {
    let agent: Agent
    var size: CGFloat = 60

    private static let avatarColors: [Color] = [
        .blue, .purple, .pink, .red, .orange, .yellow,
        .green, .teal, .cyan, .indigo, .mint, .brown
    ]

    static func avatarColor(for id: String) -> Color {
        let hash = id.utf8.reduce(0) { $0 &+ Int($1) }
        return avatarColors[abs(hash) % avatarColors.count]
    }

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
                    .fill(Self.avatarColor(for: agent.id).gradient)
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
    @ScaledMetric(relativeTo: .subheadline) private var dotSize: CGFloat = 10

    /// The most useful secondary line in a list titled "Chats": the last message
    /// if we have one, otherwise the static agent description.
    private var subtitle: String {
        if let last = agent.lastMessage, !last.isEmpty { return last }
        return agent.description
    }

    var body: some View {
        HStack(spacing: 12) {
            AgentAvatar(agent: agent, size: 44)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(agent.displayName)
                        .font(.listRowTitle)
                        .lineLimit(1)

                    if agent.unreadCount > 0 {
                        Circle()
                            .fill(.tint)
                            .frame(width: dotSize, height: dotSize)
                    }

                    Spacer(minLength: 4)

                    if let date = agent.lastActivity {
                        Text(date.chatListLabel())
                            .font(.listRowCaption)
                            .foregroundStyle(agent.unreadCount > 0 ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                    }
                }

                if !subtitle.isEmpty {
                    Text(subtitle)
                        .font(.listRowSubtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(subtitle.isEmpty ? agent.displayName : "\(agent.displayName). \(subtitle)")
        .accessibilityValue(agent.unreadCount > 0 ? "\(agent.unreadCount) unread" : "")
    }
}

extension Agent: Hashable {
    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }
}
