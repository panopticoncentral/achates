import SwiftUI

struct ToolsEditView: View {
    @Binding var tools: [String]
    let availableTools: [String]

    var body: some View {
        List {
            ForEach(availableTools, id: \.self) { tool in
                Toggle(tool, isOn: toolBinding(tool))
            }
        }
        .navigationTitle("Tools")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    private func toolBinding(_ tool: String) -> Binding<Bool> {
        Binding(
            get: { tools.contains(tool) },
            set: { enabled in
                if enabled {
                    if !tools.contains(tool) { tools.append(tool) }
                } else {
                    tools.removeAll { $0 == tool }
                }
            }
        )
    }
}
