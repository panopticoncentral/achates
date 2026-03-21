import SwiftUI

struct ModelPickerView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    @Binding var selectedModel: String
    let agentModels: [String]

    @State private var searchText = ""

    private var favoriteModels: [String] {
        agentModels.filter { $0 != selectedModel }
    }

    private var filteredFavorites: [String] {
        if searchText.isEmpty { return favoriteModels }
        return favoriteModels.filter { $0.localizedCaseInsensitiveContains(searchText) }
    }

    var body: some View {
        List {
            Section("Current") {
                HStack {
                    VStack(alignment: .leading) {
                        Text(shortName(selectedModel))
                        Text(selectedModel)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Image(systemName: "checkmark")
                        .foregroundStyle(.blue)
                }
            }

            if !filteredFavorites.isEmpty {
                Section("Used by Agents") {
                    ForEach(filteredFavorites, id: \.self) { modelId in
                        Button {
                            selectedModel = modelId
                            dismiss()
                        } label: {
                            VStack(alignment: .leading) {
                                Text(shortName(modelId))
                                    .foregroundStyle(.primary)
                                Text(modelId)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }

            Section {
                NavigationLink {
                    ModelBrowseView(selectedModel: $selectedModel)
                } label: {
                    VStack(alignment: .leading) {
                        Text("Browse All Models")
                            .fontWeight(.medium)
                        Text("Grouped by provider")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .navigationTitle("Model")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .searchable(text: $searchText, prompt: "Search models")
    }

    private func shortName(_ id: String) -> String {
        if let slash = id.lastIndex(of: "/") {
            return String(id[id.index(after: slash)...])
                .replacingOccurrences(of: "-", with: " ")
                .capitalized
        }
        return id
    }
}
