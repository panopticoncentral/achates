import SwiftUI

struct ToolsEditView: View {
    @Binding var config: AgentEditModel?
    let availableTools: [ToolInfo]

    var body: some View {
        List {
            ForEach(availableTools) { tool in
                Toggle(tool.label, isOn: toolBinding(tool.name))
            }
        }
        .navigationTitle("Tools")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private func toolBinding(_ tool: String) -> Binding<Bool> {
        Binding(
            get: { config?.tools.contains(tool) ?? false },
            set: { enabled in
                guard config != nil else { return }
                if enabled {
                    if !(config!.tools.contains(tool)) {
                        config!.tools.append(tool)
                    }
                } else {
                    config!.tools.removeAll { $0 == tool }
                }
            }
        )
    }
}
