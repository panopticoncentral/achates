import SwiftUI

struct AllowedChatsEditView: View {
    @Binding var allowedChats: [String]
    let allAgents: [Agent]
    @State private var selectedOnly = false

    var body: some View {
        Form {
            Section {
                Toggle("Allow all agents", isOn: Binding(
                    get: { !selectedOnly },
                    set: { all in
                        selectedOnly = !all
                        allowedChats = all ? [] : allAgents.map(\.id)
                    }
                ))
                .disabled(allAgents.isEmpty)
            } footer: {
                Text("Choose which other agents this agent can contact. Changes are saved with the agent.")
            }
            if selectedOnly {
                Section {
                    ForEach(allAgents) { agent in
                        Toggle(agent.displayName, isOn: Binding(
                            get: { allowedChats.contains(agent.id) },
                            set: { enabled in
                                if enabled { allowedChats.append(agent.id) }
                                else if allowedChats.count > 1 { allowedChats.removeAll { $0 == agent.id } }
                            }
                        ))
                        .disabled(allowedChats.count == 1 && allowedChats.contains(agent.id))
                    }
                } header: { Text("Selected Agents") } footer: {
                    Text("Select at least one agent. To prevent all agent communication, turn off the Chat tool in the agent’s Tools settings.")
                }
            } else if allAgents.isEmpty {
                Text("No other agents available").foregroundStyle(.secondary)
            }
        }
        #if os(macOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle("Agent Communication")
        .onAppear { selectedOnly = !allowedChats.isEmpty }
    }
}
