import SwiftUI

struct JobDetailView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let job: CronJobInfo

    @State private var liveJob: CronJobInfo
    @State private var isBusy = false
    @State private var showDeleteConfirm = false
    @State private var errorMessage: String?
    @State private var showError = false

    init(job: CronJobInfo) {
        self.job = job
        _liveJob = State(initialValue: job)
    }

    var body: some View {
        Form {
            Section("Identity") {
                row("Name", value: liveJob.name)
                row("Agent", value: liveJob.agent)
                row("Kind", value: liveJob.kind.rawValue)
                row("ID", value: liveJob.jobId, monospaced: true)
            }

            Section("Schedule") {
                row("Schedule", value: liveJob.schedule.displayString)
                if let next = liveJob.state.nextRunAt {
                    row("Next Run", value: formatted(next))
                }
                if let last = liveJob.state.lastRunAt {
                    row("Last Run", value: formatted(last))
                }
                if let status = liveJob.state.lastStatus {
                    row("Last Status", value: status)
                }
                if liveJob.state.consecutiveErrors > 0 {
                    row("Consecutive Errors", value: "\(liveJob.state.consecutiveErrors)")
                }
                if let err = liveJob.state.lastError {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Last Error").foregroundStyle(.secondary)
                        Text(err)
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
            }

            Section("Message") {
                Text(liveJob.message.isEmpty ? "(none)" : liveJob.message)
                    .font(.callout)
                    .textSelection(.enabled)
            }

            Section {
                Button {
                    Task { await runNow() }
                } label: {
                    if isBusy {
                        ProgressView()
                    } else {
                        Text("Run Now")
                    }
                }
                .disabled(isBusy)

                Toggle("Enabled", isOn: Binding(
                    get: { liveJob.enabled },
                    set: { toggleEnabled($0) }
                ))
                .disabled(isBusy)

                if liveJob.kind != .dreamtime {
                    Button(role: .destructive) {
                        showDeleteConfirm = true
                    } label: {
                        Text("Delete Job")
                    }
                    .disabled(isBusy)
                } else {
                    Text("System-managed — delete via the agent's Dreamtime setting.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        #if os(macOS)
        .formStyle(.grouped)
        #endif
        .navigationTitle(liveJob.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .alert("Delete this job?", isPresented: $showDeleteConfirm) {
            Button("Delete", role: .destructive) {
                Task { await deleteJob() }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage ?? "Unknown error")
        }
        .onChange(of: appState.jobsUpdateEvent) { _, _ in
            Task { await refresh() }
        }
    }

    private func row(_ label: String, value: String, monospaced: Bool = false) -> some View {
        HStack {
            Text(label).foregroundStyle(.secondary)
            Spacer()
            Text(value)
                .multilineTextAlignment(.trailing)
                .if(monospaced) { $0.font(.system(.caption, design: .monospaced)) }
        }
    }

    private func formatted(_ date: Date) -> String {
        let f = DateFormatter()
        f.dateStyle = .short
        f.timeStyle = .short
        return f.string(from: date)
    }

    private func runNow() async {
        isBusy = true
        do {
            try await appState.runJob(agent: liveJob.agent, jobId: liveJob.jobId)
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isBusy = false
    }

    private func toggleEnabled(_ newValue: Bool) {
        Task {
            isBusy = true
            do {
                try await appState.setJobEnabled(agent: liveJob.agent, jobId: liveJob.jobId, enabled: newValue)
                await refresh()
            } catch {
                errorMessage = error.localizedDescription
                showError = true
            }
            isBusy = false
        }
    }

    private func deleteJob() async {
        isBusy = true
        do {
            try await appState.deleteJob(agent: liveJob.agent, jobId: liveJob.jobId)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isBusy = false
    }

    private func refresh() async {
        await appState.loadJobs()
        if let updated = appState.jobs.first(where: { $0.id == liveJob.id }) {
            liveJob = updated
        } else {
            // Job was deleted (e.g. by another client) — leave the screen.
            dismiss()
        }
    }
}

private extension View {
    @ViewBuilder
    func `if`<Content: View>(_ condition: Bool, transform: (Self) -> Content) -> some View {
        if condition { transform(self) } else { self }
    }
}
