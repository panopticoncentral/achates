import SwiftUI

/// Searchable, provider-grouped model picker. Pushed from `AgentEditView` for both
/// Base Model and Thinking Model rows.
///
/// Selecting the "Default" row (or the model that matches `defaultModel`) sets the
/// binding to `nil`, meaning "fall back to the global default in config.yaml".
struct ModelBrowseView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    @Binding var selectedModel: String?
    let title: String
    let defaultModel: String?

    @State private var models: [ModelInfo] = []
    @State private var isLoading = true
    @State private var searchText = ""
    @State private var loadError: String?

    private var filteredModels: [ModelInfo] {
        guard !searchText.isEmpty else { return models }
        return models.filter {
            $0.id.localizedCaseInsensitiveContains(searchText) ||
            $0.name.localizedCaseInsensitiveContains(searchText)
        }
    }

    private var groupedModels: [(provider: String, models: [ModelInfo])] {
        let grouped = Dictionary(grouping: filteredModels, by: \.providerDisplay)
        return grouped
            .sorted { $0.key < $1.key }
            .map { (provider: $0.key, models: $0.value.sorted { $0.name < $1.name }) }
    }

    var body: some View {
        Group {
            if isLoading {
                ProgressView("Loading models…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let loadError {
                ContentUnavailableView(
                    "Couldn't load models",
                    systemImage: "exclamationmark.triangle",
                    description: Text(loadError)
                )
            } else {
                List {
                    Section {
                        Button {
                            selectedModel = nil
                            dismiss()
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("Default")
                                        .foregroundStyle(.primary)
                                    if let d = defaultModel {
                                        Text(shortModelName(d))
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                    } else {
                                        Text("No default configured")
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                    }
                                }
                                Spacer()
                                if selectedModel == nil {
                                    Image(systemName: "checkmark")
                                        .foregroundStyle(.blue)
                                }
                            }
                        }
                    } footer: {
                        Text("Falls back to models.base in ~/.achates/config.yaml.")
                    }

                    ForEach(groupedModels, id: \.provider) { group in
                        Section(group.provider) {
                            ForEach(group.models) { model in
                                Button {
                                    selectedModel = model.id
                                    dismiss()
                                } label: {
                                    HStack {
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text(model.name)
                                                .foregroundStyle(.primary)
                                            Text("\(model.id) · \(formatContext(model.contextWindow))")
                                                .font(.caption)
                                                .foregroundStyle(.secondary)
                                        }
                                        Spacer()
                                        if model.id == selectedModel {
                                            Image(systemName: "checkmark")
                                                .foregroundStyle(.blue)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle(title)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .searchable(text: $searchText, prompt: "Search models")
        .task { await loadModels() }
    }

    private func loadModels() async {
        do {
            models = try await appState.loadAvailableModels()
            loadError = nil
        } catch {
            loadError = error.localizedDescription
        }
        isLoading = false
    }

    private func formatContext(_ tokens: Int) -> String {
        if tokens >= 1_000_000 { return "\(tokens / 1_000_000)M ctx" }
        if tokens >= 1_000 { return "\(tokens / 1_000)K ctx" }
        return "\(tokens) ctx"
    }
}
