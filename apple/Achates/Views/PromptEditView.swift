import SwiftUI

struct PromptEditView: View {
    @Binding var prompt: String

    var body: some View {
        TextEditor(text: $prompt)
            .font(.system(.body, design: .monospaced))
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
