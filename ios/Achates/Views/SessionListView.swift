import SwiftUI

struct SessionListView: View {
    @Environment(AppState.self) private var appState
    let agent: Agent

    var body: some View {
        List {
            if appState.sessions.isEmpty {
                ContentUnavailableView {
                    Label("No Sessions", systemImage: "bubble.left.and.text.bubble.right")
                } description: {
                    Text("Start a new conversation with \(agent.name.capitalized)")
                }
            } else {
                ForEach(appState.sessions) { session in
                    NavigationLink(destination: ChatView(session: session)) {
                        SessionRow(session: session)
                    }
                }
                .onDelete(perform: deleteSessions)
            }
        }
        .navigationTitle(agent.name.capitalized)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button(action: {
                    Task { await appState.createNewSession() }
                }) {
                    Image(systemName: "square.and.pencil")
                }
            }
        }
        .task {
            await appState.selectAgent(agent)
        }
    }

    private func deleteSessions(at offsets: IndexSet) {
        for index in offsets {
            let session = appState.sessions[index]
            Task { await appState.deleteSession(session) }
        }
    }
}

private struct SessionRow: View {
    let session: Session

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(session.title)
                    .font(.headline)
                    .lineLimit(1)
                Spacer()
                if let date = session.lastActivity {
                    Text(date, style: .relative)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            if !session.preview.isEmpty {
                Text(session.preview)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }

            HStack {
                Image(systemName: "message")
                    .font(.caption2)
                Text("\(session.messageCount)")
                    .font(.caption)
            }
            .foregroundStyle(.tertiary)
        }
        .padding(.vertical, 4)
    }
}
