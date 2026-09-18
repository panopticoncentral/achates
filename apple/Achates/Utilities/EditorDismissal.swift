import SwiftUI

private struct EditorDismissal: ViewModifier {
    @Environment(\.dismiss) private var dismiss
    let isDirty: Bool
    let isSaving: Bool
    @State private var showDiscard = false

    func body(content: Content) -> some View {
        content
            .interactiveDismissDisabled(isDirty || isSaving)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(isDirty ? "Cancel" : "Done", action: requestDismiss)
                        .keyboardShortcut(.cancelAction)
                        .disabled(isSaving)
                }
            }
            .confirmationDialog("Discard unsaved changes?", isPresented: $showDiscard, titleVisibility: .visible) {
                Button("Discard Changes", role: .destructive) { dismiss() }
                Button("Keep Editing", role: .cancel) {}
            } message: { Text("Your changes haven’t been saved.") }
    }

    private func requestDismiss() {
        if isDirty { showDiscard = true } else { dismiss() }
    }
}

extension View {
    func editorDismissal(isDirty: Bool, isSaving: Bool = false) -> some View {
        modifier(EditorDismissal(isDirty: isDirty, isSaving: isSaving))
    }
}
