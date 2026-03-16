import Foundation

struct Session: Identifiable, Sendable, Equatable {
    let id: String
    let agentId: String
    var title: String
    var preview: String
    var messageCount: Int
    var lastActivity: Date?

    static func from(_ payload: [String: JSONValue], agentId: String) -> Session? {
        guard let id = payload["id"]?.stringValue else { return nil }
        let title = payload["title"]?.stringValue ?? "New Session"
        let preview = payload["preview"]?.stringValue ?? ""
        let messageCount = payload["message_count"]?.intValue ?? 0
        var lastActivity: Date?
        if let ts = payload["last_activity"]?.stringValue {
            lastActivity = ISO8601DateFormatter().date(from: ts)
        }
        return Session(
            id: id,
            agentId: agentId,
            title: title,
            preview: preview,
            messageCount: messageCount,
            lastActivity: lastActivity
        )
    }

    static func fromList(_ payload: [String: JSONValue], agentId: String) -> [Session] {
        guard let sessionArray = payload["sessions"]?.arrayValue else { return [] }
        return sessionArray.compactMap { value -> Session? in
            guard let dict = value.objectValue else { return nil }
            return Session.from(dict, agentId: agentId)
        }
    }
}
