import SwiftUI

/// Settings → System → Default Models. Edits the global `models.base` /
/// `models.thinking` in ~/.achates/config.yaml. A nil value means "no
/// server-wide default" and renders as "None".
struct DefaultModelsView: View {
    @Environment(AppState.self) private var appState

    @State private var base: String?
    @State private var thinking: String?
    @State private var originalBase: String?
    @State private var originalThinking: String?
    @State private var isLoading = true
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var showError = false

    private var isDirty: Bool { base != originalBase || thinking != originalThinking }

    var body: some View {
        Group {
            if isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Form {
                    Section {
                        NavigationLink {
                            ModelBrowseView(
                                selectedModel: $base,
                                title: "Default Model",
                                defaultModel: nil,
                                nilMeansNone: true)
                        } label: {
                            row(label: "Default Model", value: base)
                        }

                        NavigationLink {
                            ModelBrowseView(
                                selectedModel: $thinking,
                                title: "Default Thinking Model",
                                defaultModel: nil,
                                nilMeansNone: true)
                        } label: {
                            row(label: "Default Thinking Model", value: thinking)
                        }
                    } footer: {
                        Text("Defaults for agents that don't set their own model. Stored in ~/.achates/config.yaml.")
                    }
                }
            }
        }
        .navigationTitle("Default Models")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    Task { await save() }
                } label: {
                    if isSaving {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Save")
                    }
                }
                .disabled(!isDirty || isSaving)
            }
        }
        .alert("Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage ?? "Unknown error")
        }
        .task { await load() }
    }

    @ViewBuilder
    private func row(label: String, value: String?) -> some View {
        HStack {
            Text(label)
            Spacer()
            Text(value.map(shortModelName) ?? "None")
                .foregroundStyle(.secondary)
        }
    }

    private func load() async {
        isLoading = true
        do {
            let loaded = try await appState.loadDefaultModels()
            base = loaded.base
            thinking = loaded.thinking
            originalBase = loaded.base
            originalThinking = loaded.thinking
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isLoading = false
    }

    private func save() async {
        isSaving = true
        do {
            try await appState.saveDefaultModels(base: base, thinking: thinking)
            originalBase = base
            originalThinking = thinking
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isSaving = false
    }
}
