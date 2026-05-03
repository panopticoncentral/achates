import Foundation

struct ModelInfo: Identifiable, Equatable, Hashable {
    let id: String
    let name: String
    let contextWindow: Int

    var provider: String {
        let parts = id.split(separator: "/")
        return parts.first.map(String.init) ?? id
    }

    var providerDisplay: String {
        provider
            .split(separator: "-")
            .map { $0.prefix(1).uppercased() + $0.dropFirst() }
            .joined(separator: " ")
    }

    static func fromList(_ payload: [String: JSONValue]) -> [ModelInfo] {
        guard let arr = payload["models"]?.arrayValue else { return [] }
        return arr.compactMap { item -> ModelInfo? in
            guard let dict = item.objectValue,
                  let id = dict["id"]?.stringValue,
                  let name = dict["name"]?.stringValue else { return nil }
            let ctx = dict["context_window"]?.intValue ?? 0
            return ModelInfo(id: id, name: name, contextWindow: ctx)
        }
    }
}

/// Render a model id as a short display name. "anthropic/claude-sonnet-4.6"
/// → "Claude Sonnet 4.6". Falls back to the id for unparseable values.
func shortModelName(_ id: String) -> String {
    let trimmed: Substring
    if let slash = id.lastIndex(of: "/") {
        trimmed = id[id.index(after: slash)...]
    } else {
        trimmed = Substring(id)
    }
    return trimmed
        .split(separator: "-")
        .map { $0.prefix(1).uppercased() + $0.dropFirst() }
        .joined(separator: " ")
}
