import Foundation

struct AgentEditModel: Equatable {
    var description: String
    var model: String
    var tools: [String]
    var reasoningEffort: String?
    var temperature: Double?
    var maxTokens: Int?
    var allowedChats: [String]
    var prompt: String
    var agentModels: [String]

    static func from(_ payload: [String: JSONValue]) -> AgentEditModel? {
        guard let model = payload["model"]?.stringValue else { return nil }
        return AgentEditModel(
            description: payload["description"]?.stringValue ?? "",
            model: model,
            tools: payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? [],
            reasoningEffort: payload["reasoning_effort"]?.stringValue,
            temperature: payload["temperature"]?.doubleValue,
            maxTokens: payload["max_tokens"]?.intValue,
            allowedChats: payload["allowed_chats"]?.arrayValue?.compactMap(\.stringValue) ?? [],
            prompt: payload["prompt"]?.stringValue ?? "",
            agentModels: payload["agent_models"]?.arrayValue?.compactMap(\.stringValue) ?? []
        )
    }

    func toParams(agentId: String) -> [String: JSONValue] {
        var params: [String: JSONValue] = [
            "agent": .string(agentId),
            "description": .string(description),
            "model": .string(model),
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
        return params
    }
}

struct ModelInfo: Identifiable, Equatable {
    let id: String
    let name: String
    let contextWindow: Int

    var provider: String {
        let parts = id.split(separator: "/")
        return parts.first.map(String.init) ?? id
    }

    var providerDisplay: String {
        provider.split(separator: "-").map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined(separator: " ")
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
