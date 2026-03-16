import Foundation

struct Agent: Identifiable, Sendable, Equatable {
    let id: String
    let name: String
    let description: String
    let tools: [String]

    var initials: String {
        let parts = name.split(separator: " ")
        if parts.count >= 2 {
            return String(parts[0].prefix(1) + parts[1].prefix(1)).uppercased()
        }
        return String(name.prefix(2)).uppercased()
    }

    static func from(_ payload: [String: JSONValue]) -> Agent? {
        guard let name = payload["name"]?.stringValue else { return nil }
        let description = payload["description"]?.stringValue ?? ""
        let tools: [String] = payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? []
        return Agent(id: name, name: name, description: description, tools: tools)
    }

    static func fromList(_ payload: [String: JSONValue]) -> [Agent] {
        guard let agentArray = payload["agents"]?.arrayValue else { return [] }
        return agentArray.compactMap { value -> Agent? in
            guard let dict = value.objectValue else { return nil }
            return Agent.from(dict)
        }
    }
}
