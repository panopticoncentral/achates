import SwiftUI

struct AgentListView: View {
    @Environment(AppState.self) private var appState
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    #endif
    @State private var searchText = ""
    @State private var agentToEdit: Agent?
    #if os(iOS)
    @State private var showingSettings = false
    #endif

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
                selectableAgentList
                #else
                if appState.usesSplitNavigation {
                    selectableAgentList
                } else {
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
                .safeAreaInset(edge: .top, spacing: 0) { ConnectionStatusBanner() }
                .overlay {
                    if filteredAgents.isEmpty { ContentUnavailableView.search(text: searchText) }
                }
                .searchable(text: $searchText, placement: .navigationBarDrawer(displayMode: .automatic), prompt: "Search agents")
                }
                #endif
            }
        }
        .navigationTitle("Agents")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .toolbar {
            #if os(macOS)
            ToolbarItemGroup(placement: .automatic) {
                Button {
                    openWindow(id: "system")
                } label: {
                    Label("Manage", systemImage: "slider.horizontal.3")
                }
                .accessibilityLabel("Manage memory, jobs, and models")
                .help("Memory, scheduled jobs, and default models")

            }
            #else
            ToolbarItem(placement: .automatic) {
                if appState.usesSplitNavigation {
                    Button {
                        showingSettings = true
                    } label: {
                        Label("Settings", systemImage: "gear")
                    }
                } else {
                    NavigationLink(destination: SettingsView()) {
                        Label("Settings", systemImage: "gear")
                    }
                }
            }
            #endif
        }
        #if os(iOS)
        .sheet(isPresented: $showingSettings) {
            NavigationStack {
                SettingsView()
                    .toolbar {
                        ToolbarItem(placement: .confirmationAction) {
                            Button("Done") { showingSettings = false }
                        }
                    }
            }
        }
        #endif
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

    @ViewBuilder
    private var selectableAgentList: some View {
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
        .searchable(text: $searchText, prompt: "Search agents")

    }
}

struct AgentAvatar: View {
    let agent: Agent
    var size: CGFloat = 60
    var showsPhoto = true

    private static let avatarColors: [Color] = [
        .blue, .purple, .pink, .red, .orange, .yellow,
        .green, .teal, .cyan, .indigo, .mint, .brown
    ]

    static func avatarColor(for id: String) -> Color {
        let hash = id.utf8.reduce(0) { $0 &+ Int($1) }
        return avatarColors[abs(hash) % avatarColors.count]
    }

    var body: some View {
        if showsPhoto, let avatarImage = agent.avatarImage {
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
                    .fill(Self.avatarColor(for: agent.id).opacity(0.20).gradient)
                    .frame(width: size, height: size)
                Text(agent.initials)
                    .font(.system(size: size * 0.4, weight: .semibold))
                    .foregroundStyle(.primary)
            }
        }
    }
}

private struct AgentRow: View {
    let agent: Agent
    @Environment(\.dynamicTypeSize) private var typeSize
    @ScaledMetric(relativeTo: .subheadline) private var dotSize: CGFloat = 10

    private var subtitle: String {
        agent.description
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            AgentAvatar(agent: agent, size: 44)

            VStack(alignment: .leading, spacing: 2) {
                let layout = typeSize.isAccessibilitySize ? AnyLayout(VStackLayout(alignment: .leading, spacing: 4)) : AnyLayout(HStackLayout(spacing: 6))
                layout {
                    HStack(spacing: 6) {
                        Text(agent.displayName)
                            .font(.listRowTitle)
                            .lineLimit(typeSize.isAccessibilitySize ? nil : 1)
                        if agent.unreadCount > 0 {
                            Circle().fill(.tint)
                                .frame(width: min(dotSize, 12), height: min(dotSize, 12))
                                .accessibilityHidden(true)
                        }
                    }
                    if !typeSize.isAccessibilitySize { Spacer(minLength: 4) }

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
                        .lineLimit(typeSize.isAccessibilitySize ? 3 : 1)
                }
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(subtitle.isEmpty ? agent.displayName : "\(agent.displayName). \(subtitle)")
        .accessibilityValue(agent.unreadCount > 0 ? "\(agent.unreadCount) unread" : "")
        .accessibilityIdentifier("agent-row-\(agent.id)")
    }
}

extension Agent: Hashable {
    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }
}
