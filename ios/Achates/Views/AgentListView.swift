import SwiftUI

struct AgentListView: View {
    @Environment(AppState.self) private var appState

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
                List(appState.agents) { agent in
                    NavigationLink(value: agent) {
                        AgentRow(agent: agent)
                    }
                }
            }
        }
        .navigationTitle("Agents")
        .navigationBarTitleDisplayMode(.large)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                NavigationLink(destination: SettingsView()) {
                    Image(systemName: "gear")
                }
            }
        }
        .navigationDestination(for: Agent.self) { agent in
            ChatView(agent: agent)
        }
        .onAppear {
            if appState.connectionStatus == .disconnected && appState.serverURL != nil {
                appState.connectToServer()
            }
        }
    }
}

private struct AgentRow: View {
    let agent: Agent

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle()
                    .fill(.blue.gradient)
                    .frame(width: 60, height: 60)
                Text(agent.initials)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(.white)
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(agent.name.capitalized)
                        .font(.system(size: 17, weight: .bold))
                    Spacer()
                    if let date = agent.lastActivity {
                        Text(formatTimestamp(date))
                            .font(.system(size: 14))
                            .foregroundStyle(.secondary)
                    }
                    if agent.unreadCount > 0 {
                        Text("\(agent.unreadCount)")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 7)
                            .padding(.vertical, 2)
                            .background(Color.blue, in: Capsule())
                    }
                }

                Text(previewText)
                    .font(.system(size: 15))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
        }
        .padding(.vertical, 6)
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
            formatter.dateFormat = "EEEE"
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
