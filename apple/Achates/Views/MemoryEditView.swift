import SwiftUI

struct MemoryEditView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let memory: MemoryInfo

    @State private var content = ""
    @State private var original = ""
    @State private var isLoading = true
    @State private var loadFailed = false
    @State private var isSaving = false
    @State private var showConflictBanner = false
    @State private var errorMessage: String?
    @State private var showError = false

    private var isDirty: Bool { content != original }

    var body: some View {
        Group {
            if isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if loadFailed {
                // A failed load must not fall through to an empty editor: Save would
                // full-replace the real server-side file with whatever is typed here.
                ContentUnavailableView {
                    Label("Couldn't Load Memory", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(errorMessage ?? "The memory file couldn't be loaded.")
                } actions: {
                    Button("Retry") {
                        Task { await load() }
                    }
                    .buttonStyle(.borderedProminent)
                }
            } else {
                VStack(spacing: 0) {
                    if showConflictBanner {
                        conflictBanner
                    }
                    TextEditor(text: $content)
                        .font(.system(.body, design: .monospaced))
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                }
            }
        }
        .navigationTitle(memory.displayName)
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
                .disabled(!isDirty || isSaving || loadFailed)
            }
        }
        .alert("Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage ?? "Unknown error")
        }
        .task { await load() }
        .onChange(of: appState.memoryUpdateEvent) { _, new in
            guard let new, new.scope == memory.scope else { return }
            if isDirty {
                showConflictBanner = true
            } else {
                Task { await load() }
            }
        }
    }

    @ViewBuilder
    private var conflictBanner: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Text("This memory was changed elsewhere.")
                .font(.footnote)
            Spacer()
            Button("Reload") {
                Task {
                    await load()
                    showConflictBanner = false
                }
            }
            .font(.footnote)
            Button {
                showConflictBanner = false
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color.orange.opacity(0.15))
    }

    private func load() async {
        isLoading = true
        do {
            let loaded = try await appState.loadMemory(scope: memory.scope)
            content = loaded
            original = loaded
            loadFailed = false
        } catch {
            loadFailed = true
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }

    private func save() async {
        isSaving = true
        do {
            try await appState.saveMemory(scope: memory.scope, content: content)
            original = content
            showConflictBanner = false
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isSaving = false
    }
}
