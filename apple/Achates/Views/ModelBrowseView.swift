import SwiftUI

struct ModelBrowseView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    @Binding var selectedModel: String

    @State private var models: [ModelInfo] = []
    @State private var isLoading = true
    @State private var searchText = ""

    private var filteredModels: [ModelInfo] {
        if searchText.isEmpty { return models }
        return models.filter {
            $0.id.localizedCaseInsensitiveContains(searchText) ||
            $0.name.localizedCaseInsensitiveContains(searchText)
        }
    }

    private var groupedModels: [(provider: String, models: [ModelInfo])] {
        let grouped = Dictionary(grouping: filteredModels, by: \.providerDisplay)
        return grouped.sorted { $0.key < $1.key }
            .map { (provider: $0.key, models: $0.value.sorted { $0.name < $1.name }) }
    }

    var body: some View {
        Group {
            if isLoading {
                ProgressView("Loading models...")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(groupedModels, id: \.provider) { group in
                        Section(group.provider) {
                            ForEach(group.models) { model in
                                Button {
                                    selectedModel = model.id
                                    dismiss()
                                } label: {
                                    HStack {
                                        VStack(alignment: .leading) {
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
        .navigationTitle("All Models")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .searchable(text: $searchText, prompt: "Search models")
        .task { await loadModels() }
    }

    private func loadModels() async {
        do {
            models = try await appState.loadModels()
            isLoading = false
        } catch {
            isLoading = false
        }
    }

    private func formatContext(_ tokens: Int) -> String {
        if tokens >= 1_000_000 { return "\(tokens / 1_000_000)M ctx" }
        if tokens >= 1_000 { return "\(tokens / 1_000)K ctx" }
        return "\(tokens) ctx"
    }
}
