import Foundation

struct Agent: Identifiable, Sendable, Equatable {
    let id: String
    let name: String
    let description: String
    let tools: [String]
    let lastMessage: String?
    let lastActivity: Date?
    var unreadCount: Int

    var initials: String {
        let parts = name.split(separator: " ")
        if parts.count >= 2 {
            return String(parts[0].prefix(1) + parts[1].prefix(1)).uppercased()
        }
        return String(name.prefix(2)).uppercased()
    }

    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func from(_ payload: [String: JSONValue]) -> Agent? {
        guard let name = payload["name"]?.stringValue else { return nil }
        let description = payload["description"]?.stringValue ?? ""
        let tools: [String] = payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? []
        let lastMessage = payload["last_message"]?.stringValue

        let unreadCount = payload["unread_count"]?.intValue ?? 0

        var lastActivity: Date?
        if let activityStr = payload["last_activity"]?.stringValue {
            lastActivity = isoFormatter.date(from: activityStr)
                ?? ISO8601DateFormatter().date(from: activityStr)
        }

        return Agent(id: name, name: name, description: description, tools: tools,
                     lastMessage: lastMessage, lastActivity: lastActivity,
                     unreadCount: unreadCount)
    }

    static func fromList(_ payload: [String: JSONValue]) -> [Agent] {
        guard let agentArray = payload["agents"]?.arrayValue else { return [] }
        return agentArray.compactMap { value -> Agent? in
            guard let dict = value.objectValue else { return nil }
            return Agent.from(dict)
        }
    }
}
