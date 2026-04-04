import SwiftUI

struct CostsView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let agent: Agent
    @State private var selectedPeriod = "month"
    @State private var summary: CostSummary?
    @State private var isLoading = false

    private let periods = ["today", "week", "month", "all"]
    private let periodLabels = ["Today", "Week", "Month", "All"]

    var body: some View {
        List {
            Section {
                Picker("Period", selection: $selectedPeriod) {
                    ForEach(Array(zip(periods, periodLabels)), id: \.0) { value, label in
                        Text(label).tag(value)
                    }
                }
                .pickerStyle(.segmented)
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())
            }

            if let summary {
                Section("Summary") {
                    summaryRow("Total Cost", value: formatCurrency(summary.totalCost))
                    summaryRow("Completions", value: "\(summary.completions)")
                    summaryRow("Input Tokens", value: formatTokens(summary.inputTokens))
                    summaryRow("Output Tokens", value: formatTokens(summary.outputTokens))
                }

                if !summary.byDay.isEmpty {
                    Section("By Day") {
                        ForEach(summary.byDay) { day in
                            HStack {
                                Text(day.date)
                                Spacer()
                                Text("\(day.completions) msgs")
                                    .foregroundStyle(.secondary)
                                    .font(.caption)
                                Text(formatCurrency(day.cost))
                                    .monospacedDigit()
                            }
                        }
                    }
                }

                if !summary.byModel.isEmpty {
                    Section("By Model") {
                        ForEach(summary.byModel) { model in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(formatModelName(model.model))
                                    .lineLimit(1)
                                HStack {
                                    Text("\(model.completions) completions")
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                    Spacer()
                                    Text(formatCurrency(model.cost))
                                        .monospacedDigit()
                                }
                            }
                        }
                    }
                }
            } else if isLoading {
                Section {
                    HStack {
                        Spacer()
                        ProgressView()
                        Spacer()
                    }
                }
            }
        }
        .navigationTitle("Costs")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button("Done") { dismiss() }
            }
        }
        .task(id: selectedPeriod) {
            await loadSummary()
        }
    }

    private func loadSummary() async {
        isLoading = true
        summary = await appState.fetchCostSummary(agent: agent.id, period: selectedPeriod)
        isLoading = false
    }

    private func summaryRow(_ label: String, value: String) -> some View {
        HStack {
            Text(label)
            Spacer()
            Text(value)
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
    }

    private func formatCurrency(_ value: Double) -> String {
        if value < 0.01 {
            return String(format: "$%.4f", value)
        } else if value < 1.0 {
            return String(format: "$%.3f", value)
        } else {
            return String(format: "$%.2f", value)
        }
    }

    private func formatTokens(_ count: Int) -> String {
        if count >= 1_000_000 {
            return String(format: "%.1fM", Double(count) / 1_000_000)
        } else if count >= 1_000 {
            return String(format: "%.1fk", Double(count) / 1_000)
        }
        return "\(count)"
    }

    private func formatModelName(_ model: String) -> String {
        // Strip provider prefix (e.g. "anthropic/claude-sonnet-4" -> "claude-sonnet-4")
        if let slashIndex = model.lastIndex(of: "/") {
            return String(model[model.index(after: slashIndex)...])
        }
        return model
    }
}
