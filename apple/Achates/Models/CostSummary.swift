import Foundation

struct CostSummary: Sendable {
    let totalCost: Double
    let completions: Int
    let inputTokens: Int
    let outputTokens: Int
    let byDay: [DayCost]
    let byModel: [ModelCost]

    struct DayCost: Identifiable, Sendable {
        var id: String { date }
        let date: String
        let cost: Double
        let completions: Int
    }

    struct ModelCost: Identifiable, Sendable {
        var id: String { model }
        let model: String
        let cost: Double
        let completions: Int
    }

    static func from(_ payload: [String: JSONValue]) -> CostSummary? {
        let totalCost = payload["total_cost"]?.doubleValue ?? 0
        let completions = payload["completions"]?.intValue ?? 0
        let inputTokens = payload["input_tokens"]?.intValue ?? 0
        let outputTokens = payload["output_tokens"]?.intValue ?? 0

        let byDay: [DayCost] = (payload["by_day"]?.arrayValue ?? []).compactMap { item in
            guard let dict = item.objectValue,
                  let date = dict["date"]?.stringValue else { return nil }
            return DayCost(
                date: date,
                cost: dict["cost"]?.doubleValue ?? 0,
                completions: dict["completions"]?.intValue ?? 0
            )
        }

        let byModel: [ModelCost] = (payload["by_model"]?.arrayValue ?? []).compactMap { item in
            guard let dict = item.objectValue,
                  let model = dict["model"]?.stringValue else { return nil }
            return ModelCost(
                model: model,
                cost: dict["cost"]?.doubleValue ?? 0,
                completions: dict["completions"]?.intValue ?? 0
            )
        }

        return CostSummary(
            totalCost: totalCost,
            completions: completions,
            inputTokens: inputTokens,
            outputTokens: outputTokens,
            byDay: byDay,
            byModel: byModel
        )
    }
}
