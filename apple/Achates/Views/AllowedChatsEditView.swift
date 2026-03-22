import SwiftUI

struct AllowedChatsEditView: View {
    @Binding var allowedChats: [String]
    let allAgents: [Agent]

    var body: some View {
        List {
            if allAgents.isEmpty {
                Text("No other agents available")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(allAgents) { agent in
                    Toggle(agent.displayName, isOn: chatBinding(agent.id))
                }
            }

            Section {
            } footer: {
                Text("When no agents are selected, this agent can chat with all others.")
            }
        }
        .navigationTitle("Allowed Chats")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private func chatBinding(_ agentId: String) -> Binding<Bool> {
        Binding(
            get: { allowedChats.contains(agentId) },
            set: { enabled in
                if enabled {
                    if !allowedChats.contains(agentId) { allowedChats.append(agentId) }
                } else {
                    allowedChats.removeAll { $0 == agentId }
                }
            }
        )
    }
}
