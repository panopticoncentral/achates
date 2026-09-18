import SwiftUI
import Charts

struct CostsView: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    let agent: Agent
    @State private var selectedPeriod = "month"
    @State private var isLoading = false

    private var summary: CostSummary? {
        appState.costSummary(agent: agent.id, period: selectedPeriod)
    }

    private let periods = ["today", "week", "month", "all"]
    private let periodLabels = ["Today", "7 Days", "30 Days", "All"]

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

            if let error = appState.costLoadErrors["\(agent.id):\(selectedPeriod)"] {
                InlineNotice(message: error, actionTitle: "Retry") { Task { await loadSummary() } }
            }
            if let summary {
                Section {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(agent.displayName).font(.headline)
                        Text(formatCurrency(summary.totalCost)).font(.largeTitle.weight(.semibold)).monospacedDigit()
                        Text("Total spend · USD · \(periodLabels[periods.firstIndex(of: selectedPeriod) ?? 2])").font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 8)
                    if summary.byDay.count > 1 {
                        Chart(summary.byDay) { day in
                            BarMark(x: .value("Date", dayLabel(day.date)), y: .value("Cost in USD", day.cost))
                                .accessibilityLabel(dayLabel(day.date))
                                .accessibilityValue(formatCurrency(day.cost))
                        }
                        .frame(height: 160)
                        .chartXAxis(.hidden)
                        .accessibilityLabel("Daily spending in US dollars")
                    }
                }
                Section("Summary") {
                    summaryRow("Completions", value: "\(summary.completions)")
                    summaryRow("Input Tokens", value: formatTokens(summary.inputTokens))
                    summaryRow("Output Tokens", value: formatTokens(summary.outputTokens))
                }

                if !summary.byDay.isEmpty {
                    Section("By Day") {
                        ForEach(summary.byDay) { day in
                            HStack {
                                Text(dayLabel(day.date))
                                Spacer()
                                Text("\(day.completions) completions")
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
                Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .task(id: selectedPeriod) {
            await loadSummary()
        }
        .onChange(of: summary == nil) { _, isNowNil in
            if isNowNil { Task { await loadSummary() } }
        }
    }

    private func loadSummary() async {
        isLoading = true
        await appState.loadCostSummary(agent: agent.id, period: selectedPeriod)
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

    private func dayLabel(_ value: String) -> String {
        let components = value.split(separator: "-").compactMap { Int($0) }
        guard components.count == 3,
              let date = Calendar(identifier: .gregorian).date(from: DateComponents(year: components[0], month: components[1], day: components[2])) else { return value }
        return date.formatted(date: .abbreviated, time: .omitted)
    }

    private func formatCurrency(_ value: Double) -> String {
        let digits = value < 0.01 ? 4 : (value < 1 ? 3 : 2)
        return value.formatted(.currency(code: "USD").precision(.fractionLength(digits)))
    }

    private func formatTokens(_ count: Int) -> String {
        count.formatted(.number.notation(.compactName).precision(.fractionLength(0...1)))
    }

    private func formatModelName(_ model: String) -> String {
        // Strip provider prefix (e.g. "anthropic/claude-sonnet-4" -> "claude-sonnet-4")
        if let slashIndex = model.lastIndex(of: "/") {
            return String(model[model.index(after: slashIndex)...])
        }
        return model
    }
}
