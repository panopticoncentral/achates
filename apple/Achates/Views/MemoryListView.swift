import SwiftUI

struct MemoryListView: View {
    @Environment(AppState.self) private var appState
    @State private var hasLoaded = false
    @State private var editingMemory: MemoryInfo?

    var body: some View {
        List {
            if let error = appState.memoryLoadError {
                InlineNotice(message: error, actionTitle: "Retry") { Task { await appState.loadMemories() } }
            }
            if !hasLoaded {
                HStack {
                    Spacer()
                    ProgressView()
                    Spacer()
                }
                .listRowSeparator(.hidden)
            } else if appState.memories.isEmpty && appState.memoryLoadError == nil {
                ContentUnavailableView(
                    "No Memory Files",
                    systemImage: "brain",
                    description: Text("Agents build memory as you talk to them.")
                )
            } else {
                ForEach(appState.memories) { memory in
                    Button { editingMemory = memory } label: {
                        MemoryRow(memory: memory)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .navigationTitle("Memory")
        .sheet(item: $editingMemory) { memory in
            NavigationStack { MemoryEditView(memory: memory) }
                #if os(macOS)
                .frame(minWidth: 560, idealWidth: 700, minHeight: 500)
                #endif
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable { await appState.loadMemories() }
        #if os(macOS)
        // Pull-to-refresh doesn't exist on the Mac; give it a button + ⌘R.
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    Task { await appState.loadMemories() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .keyboardShortcut("r", modifiers: .command)
                    .accessibilityLabel("Refresh")
                    .help("Refresh memories")
            }
        }
        #endif
        .task {
            await appState.loadMemories()
            hasLoaded = true
        }
    }
}

private struct MemoryRow: View {
    @Environment(AppState.self) private var appState
    let memory: MemoryInfo

    private var title: String {
        if memory.isShared { return "Shared Memory" }
        return appState.agents.first { $0.name == memory.scope || $0.id == memory.scope }?.displayName ?? memory.displayName
    }

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .fontWeight(memory.isShared ? .semibold : .regular)
                if memory.isShared {
                    Text("Available to every agent").font(.caption).foregroundStyle(.secondary)
                }
                Text("\(formatSize(memory.size)) · \(relative(memory.updated))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
    }

    private func formatSize(_ bytes: Int) -> String {
        if bytes < 1024 { return "\(bytes) B" }
        if bytes < 1024 * 1024 { return String(format: "%.1f KB", Double(bytes) / 1024) }
        return String(format: "%.1f MB", Double(bytes) / (1024 * 1024))
    }

    private func relative(_ date: Date) -> String {
        if date.timeIntervalSince1970 == 0 { return "never" }
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .short
        return f.localizedString(for: date, relativeTo: Date())
    }
}
