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
    var dreamtime: Date?
    var model: String?
    var thinkingModel: String?
    var defaultModel: String?
    var defaultThinkingModel: String?
    var sharedMemory: Bool
    /// Per-agent TTS voice id (e.g. "af_nicole" or a Kokoro blend like
    /// "af_nicole(0.7)+af_bella(0.3)"). Nil/empty makes the agent voiceless.
    var voice: String?

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
            hasAvatar: payload["has_avatar"]?.boolValue ?? false,
            dreamtime: payload["dreamtime"]?.stringValue.flatMap(parseDreamtime),
            model: nonEmpty(payload["model"]?.stringValue),
            thinkingModel: nonEmpty(payload["thinking_model"]?.stringValue),
            defaultModel: nonEmpty(payload["default_model"]?.stringValue),
            defaultThinkingModel: nonEmpty(payload["default_thinking_model"]?.stringValue),
            sharedMemory: payload["shared_memory"]?.boolValue ?? true,
            voice: nonEmpty(payload["voice"]?.stringValue)
        )
    }

    func toParams(agentId: String) -> [String: JSONValue] {
        var params: [String: JSONValue] = [
            "agent": .string(agentId),
            "description": .string(description),
            "tools": .array(tools.map { .string($0) }),
            "allowed_chats": .array(allowedChats.map { .string($0) }),
            "prompt": .string(prompt),
            // Always send model/thinking_model so the server can clear an override
            // (empty string == revert to global default).
            "model": .string(model ?? ""),
            "thinking_model": .string(thinkingModel ?? ""),
            // Always send voice so empty string clears it (makes agent voiceless).
            "voice": .string(voice ?? ""),
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
        if let d = dreamtime {
            params["dreamtime"] = .string(formatDreamtime(d))
        }
        params["shared_memory"] = .bool(sharedMemory)
        return params
    }
}

private func nonEmpty(_ s: String?) -> String? {
    guard let s, !s.isEmpty else { return nil }
    return s
}

private let dreamtimeFormatter: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "HH:mm"
    f.locale = Locale(identifier: "en_US_POSIX")
    return f
}()

private func parseDreamtime(_ s: String) -> Date? { dreamtimeFormatter.date(from: s) }
private func formatDreamtime(_ d: Date) -> String { dreamtimeFormatter.string(from: d) }

struct ToolInfo: Identifiable, Equatable {
    var id: String { name }
    let name: String
    let label: String
}
