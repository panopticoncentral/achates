import SwiftUI

struct PromptEditView: View {
    @Binding var prompt: String

    var body: some View {
        TextEditor(text: $prompt)
            .font(.system(.body, design: .monospaced))
            .accessibilityLabel("System prompt")
            .safeAreaInset(edge: .bottom) {
                Text("Changes are saved when you save the agent.")
                    .font(.footnote).foregroundStyle(.secondary).padding(8)
            }
            #if os(iOS)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            #endif
            .navigationTitle("System Prompt")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
    }
}
