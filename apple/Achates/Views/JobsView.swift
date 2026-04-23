import SwiftUI

struct JobsView: View {
    @Environment(AppState.self) private var appState
    @State private var jobs: [CronJobInfo] = []
    @State private var isLoading = true

    private var sortedJobs: [CronJobInfo] {
        jobs.sorted { lhs, rhs in
            switch (lhs.state.nextRunAt, rhs.state.nextRunAt) {
            case let (l?, r?): return l < r
            case (nil, _?): return false
            case (_?, nil): return true
            default: return lhs.name < rhs.name
            }
        }
    }

    var body: some View {
        List {
            if isLoading {
                HStack { Spacer(); ProgressView(); Spacer() }
                    .listRowSeparator(.hidden)
            } else if jobs.isEmpty {
                Text("No scheduled jobs.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(sortedJobs) { job in
                    NavigationLink {
                        JobDetailView(job: job)
                    } label: {
                        JobRow(job: job)
                    }
                }
            }
        }
        .navigationTitle("Scheduled Jobs")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable { await load() }
        .task { await load() }
        .onChange(of: appState.jobsUpdateEvent) { _, _ in
            Task { await load() }
        }
    }

    private func load() async {
        isLoading = true
        jobs = await appState.listJobs()
        isLoading = false
    }
}

private struct JobRow: View {
    let job: CronJobInfo

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(job.name)
                        .fontWeight(.semibold)
                    if job.kind == .dreamtime {
                        Text("system")
                            .font(.caption2)
                            .padding(.horizontal, 4)
                            .padding(.vertical, 1)
                            .background(.gray.opacity(0.2), in: Capsule())
                    }
                }
                Text("\(job.agent) · \(job.schedule.displayString)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let next = job.state.nextRunAt, job.enabled {
                    Text("next: \(relative(next))")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
            statusBadge
        }
    }

    @ViewBuilder
    private var statusBadge: some View {
        if !job.enabled {
            badge("disabled", color: .gray)
        } else if job.state.lastStatus == "error" {
            badge("error", color: .red)
        } else if job.state.lastStatus == "ok" {
            badge("ok", color: .green)
        } else {
            badge("pending", color: .blue)
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.2), in: Capsule())
            .foregroundStyle(color)
    }

    private func relative(_ date: Date) -> String {
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .short
        return f.localizedString(for: date, relativeTo: Date())
    }
}
