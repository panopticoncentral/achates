import Foundation

struct AgentEditModel: Equatable {
    var displayName: String
    var description: String
    var tools: [String]
    var reasoningEffort: String?
    var temperature: Double?
    var maxTokens: Int?
    var allowedChats: [String]
    var prompt: String
    var hasAvatar: Bool
    var newAvatarData: Data?
    var removeAvatar: Bool = false

    static func from(_ payload: [String: JSONValue]) -> AgentEditModel? {
        AgentEditModel(
            displayName: payload["display_name"]?.stringValue ?? "",
            description: payload["description"]?.stringValue ?? "",
            tools: payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? [],
            reasoningEffort: payload["reasoning_effort"]?.stringValue,
            temperature: payload["temperature"]?.doubleValue,
            maxTokens: payload["max_tokens"]?.intValue,
            allowedChats: payload["allowed_chats"]?.arrayValue?.compactMap(\.stringValue) ?? [],
            prompt: payload["prompt"]?.stringValue ?? "",
            hasAvatar: payload["has_avatar"]?.boolValue ?? false
        )
    }

    func toParams(agentId: String) -> [String: JSONValue] {
        var params: [String: JSONValue] = [
            "agent": .string(agentId),
            "description": .string(description),
            "tools": .array(tools.map { .string($0) }),
            "allowed_chats": .array(allowedChats.map { .string($0) }),
            "prompt": .string(prompt),
        ]
        if let re = reasoningEffort {
            params["reasoning_effort"] = .string(re)
        }
        if let t = temperature {
            params["temperature"] = .double(t)
        }
        if let mt = maxTokens {
            params["max_tokens"] = .int(mt)
        }
        if let data = newAvatarData {
            params["avatar"] = .string(data.base64EncodedString())
        }
        if removeAvatar {
            params["avatar_remove"] = .bool(true)
        }
        return params
    }
}

struct ToolInfo: Identifiable, Equatable {
    var id: String { name }
    let name: String
    let label: String
}
