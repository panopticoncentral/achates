import SwiftUI

struct MemoryListView: View {
    @Environment(AppState.self) private var appState
    @State private var hasLoaded = false

    var body: some View {
        List {
            if !hasLoaded {
                HStack {
                    Spacer()
                    ProgressView()
                    Spacer()
                }
                .listRowSeparator(.hidden)
            } else if appState.memories.isEmpty {
                Text("No memory files.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(appState.memories) { memory in
                    NavigationLink {
                        MemoryEditView(memory: memory)
                    } label: {
                        MemoryRow(memory: memory)
                    }
                }
            }
        }
        .navigationTitle("Memory")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable { await appState.loadMemories() }
        .task {
            await appState.loadMemories()
            hasLoaded = true
        }
    }
}

private struct MemoryRow: View {
    let memory: MemoryInfo

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(memory.displayName)
                    .fontWeight(memory.isShared ? .semibold : .regular)
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
