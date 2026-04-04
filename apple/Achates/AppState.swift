import Foundation
import SwiftUI
import UserNotifications

enum AgentEditError: LocalizedError {
    case notConnected
    case invalidResponse
    case reloadWarning(String)

    var errorDescription: String? {
        switch self {
        case .notConnected: return "Not connected to server."
        case .invalidResponse: return "Invalid response from server."
        case .reloadWarning(let msg): return msg
        }
    }
}

/// Navigation target for opening a specific session.
struct SessionSelection: Hashable {
    let agent: Agent
    let sessionId: String
}

@Observable
@MainActor
final class AppState {
    var connectionStatus: ConnectionStatus = .disconnected
    var serverURL: URL?
    var agents: [Agent] = []
    var currentAgent: Agent?
    var isStreaming = false
    var streamingMessageId: String?
    var client: WebSocketClient?
    var error: String?

    /// Sessions for the currently selected agent
    var sessions: [SessionInfo] = []
    var hasMoreSessions = false

    /// Messages for the currently open session
    var messages: [ChatMessage] = []
    var currentSessionId: String?

    init() {
        if let urlString = UserDefaults.standard.string(forKey: "achates_server_url"),
           let url = URL(string: urlString) {
            serverURL = url
        }
    }

    func saveServerURL(_ url: URL) {
        serverURL = url
        UserDefaults.standard.set(url.absoluteString, forKey: "achates_server_url")
    }

    func connectToServer() {
        guard let url = serverURL else { return }
        if client == nil {
            client = WebSocketClient(appState: self)
        }
        let agentId = currentAgent?.id ?? "default"
        client?.connect(url: url, agent: agentId)
    }

    func disconnect() {
        client?.disconnect()
        connectionStatus = .disconnected
        agents = []
        currentAgent = nil
        sessions = []
        messages = []
        currentSessionId = nil
    }

    func markCurrentAgentAsRead() {
        guard let agent = currentAgent, client != nil else { return }

        let latestTimestamp: Int? = messages.reversed().compactMap { msg -> Int? in
            return Int(msg.timestamp.timeIntervalSince1970 * 1000)
        }.first

        guard let ts = latestTimestamp else { return }

        if let index = agents.firstIndex(where: { $0.id == agent.id }), agents[index].unreadCount > 0 {
            agents[index].unreadCount = 0
            updateAppBadge()
        }

        Task {
            _ = try? await client?.sendRequest(method: "chat.read", params: [
                "agent": .string(agent.id),
                "timestamp": .int(ts),
            ])
        }
    }

    func updateAppBadge() {
        let total = agents.reduce(0) { $0 + $1.unreadCount }
        Task {
            try? await UNUserNotificationCenter.current().setBadgeCount(total)
        }
    }

    // MARK: - Session management

    func loadSessions(for agent: Agent) async {
        guard client != nil else { return }

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
            ])
            if let payload {
                sessions = SessionInfo.fromList(payload)
                hasMoreSessions = payload["has_more"]?.boolValue ?? false
            }
        } catch {
            self.error = "Failed to load sessions: \(error.localizedDescription)"
        }
    }

    func loadMoreSessions(for agent: Agent) async {
        guard client != nil, hasMoreSessions, let oldest = sessions.last else { return }

        let beforeMs = Int(oldest.updated.timeIntervalSince1970 * 1000)

        do {
            let payload = try await client?.sendRequest(method: "sessions.list", params: [
                "agent": .string(agent.id),
                "before": .int(beforeMs),
            ])
            if let payload {
                let older = SessionInfo.fromList(payload)
                hasMoreSessions = payload["has_more"]?.boolValue ?? false
                sessions.append(contentsOf: older)
            }
        } catch {
            self.error = "Failed to load sessions: \(error.localizedDescription)"
        }
    }

    func createSession(for agent: Agent) async -> String? {
        guard client != nil else { return nil }

        do {
            let payload = try await client?.sendRequest(method: "sessions.create", params: [
                "agent": .string(agent.id),
            ])
            if let id = payload?["id"]?.stringValue {
                // Insert at the top of the list
                let newSession = SessionInfo(
                    id: id,
                    title: nil,
                    preview: nil,
                    created: Date(),
                    updated: Date()
                )
                sessions.insert(newSession, at: 0)
                return id
            }
        } catch {
            self.error = "Failed to create session: \(error.localizedDescription)"
        }
        return nil
    }

    func openSession(_ sessionId: String, for agent: Agent) async {
        guard client != nil else { return }
        currentSessionId = sessionId
        messages = []

        do {
            let payload = try await client?.sendRequest(method: "sessions.get", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            if let payload {
                messages = parseSessionMessages(payload, serverURL: serverURL)
            }
        } catch {
            self.error = "Failed to load session: \(error.localizedDescription)"
        }

        markCurrentAgentAsRead()
    }

    func deleteSession(_ sessionId: String, for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.delete", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
            ])
            sessions.removeAll { $0.id == sessionId }
            if currentSessionId == sessionId {
                currentSessionId = nil
                messages = []
            }
        } catch {
            self.error = "Failed to delete session: \(error.localizedDescription)"
        }
    }

    func renameSession(_ sessionId: String, title: String, for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.rename", params: [
                "agent": .string(agent.id),
                "session_id": .string(sessionId),
                "title": .string(title),
            ])
            if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
                sessions[index].title = title
            }
        } catch {
            self.error = "Failed to rename session: \(error.localizedDescription)"
        }
    }

    func deleteAllSessions(for agent: Agent) async {
        guard client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "sessions.delete_all", params: [
                "agent": .string(agent.id),
            ])
            sessions = []
            messages = []
            currentSessionId = nil
        } catch {
            self.error = "Failed to clear sessions: \(error.localizedDescription)"
        }
    }

    func updateSessionTitle(sessionId: String, title: String) {
        if let index = sessions.firstIndex(where: { $0.id == sessionId }) {
            sessions[index].title = title
        }
    }

    // MARK: - Agent management

    func loadAgentConfig(_ agent: Agent) async throws -> AgentEditModel {
        guard let payload = try await client?.sendRequest(method: "agent.get", params: [
            "agent": .string(agent.id),
        ]) else {
            throw AgentEditError.notConnected
        }
        guard let model = AgentEditModel.from(payload) else {
            throw AgentEditError.invalidResponse
        }
        return model
    }

    func saveAgentConfig(_ agent: Agent, config: AgentEditModel, original: AgentEditModel) async throws {
        var currentAgentId = agent.id

        if config.displayName != original.displayName && !config.displayName.isEmpty {
            guard let renamePayload = try await client?.sendRequest(method: "agent.rename", params: [
                "agent": .string(agent.id),
                "name": .string(config.displayName),
            ]) else {
                throw AgentEditError.notConnected
            }
            if let newId = renamePayload["id"]?.stringValue {
                currentAgentId = newId
            }
            await refreshAgents()
        }

        guard let payload = try await client?.sendRequest(method: "agent.update",
            params: config.toParams(agentId: currentAgentId)
        ) else {
            throw AgentEditError.notConnected
        }
        if let warning = payload["warning"]?.stringValue {
            throw AgentEditError.reloadWarning(warning)
        }
        await refreshAgents()
    }

    func generateAvatar(_ agent: Agent, prompt: String, referenceImage: Data? = nil) async throws -> Data {
        var params: [String: JSONValue] = [
            "agent": .string(agent.id),
            "prompt": .string(prompt),
        ]
        if let imageData = referenceImage {
            params["image"] = .string(imageData.base64EncodedString())
        }
        guard let payload = try await client?.sendRequest(method: "agent.generate_avatar",
            params: params,
            timeout: .seconds(120)
        ) else {
            throw AgentEditError.notConnected
        }
        guard let b64 = payload["image"]?.stringValue,
              let data = Data(base64Encoded: b64) else {
            throw AgentEditError.reloadWarning("No image data returned")
        }
        return data
    }

    func loadModels() async throws -> [ModelInfo] {
        guard let payload = try await client?.sendRequest(method: "models.list") else {
            throw AgentEditError.notConnected
        }
        return ModelInfo.fromList(payload)
    }

    func loadAvailableTools() async throws -> [String] {
        guard let payload = try await client?.sendRequest(method: "tools.list") else {
            throw AgentEditError.notConnected
        }
        guard case .array(let items) = payload["tools"] else { return [] }
        return items.compactMap { if case .string(let s) = $0 { return s } else { return nil } }
    }

    func fetchCostSummary(agent: String, period: String) async -> CostSummary? {
        guard let payload = try? await client?.sendRequest(method: "costs.summary", params: [
            "agent": .string(agent),
            "period": .string(period),
        ]) else { return nil }
        return CostSummary.from(payload)
    }

    func handleAgentRenamed(oldId: String?, newId: String?) async {
        let wasCurrentAgent = currentAgent?.id == oldId
        await refreshAgents()
        if wasCurrentAgent, let newId, let updated = agents.first(where: { $0.id == newId }) {
            currentAgent = updated
        }
    }

    func refreshAgents() async {
        guard let client, let payload = try? await client.sendRequest(method: "agents.list") else { return }
        agents = Agent.fromList(payload)
        if let current = currentAgent,
           let updated = agents.first(where: { $0.id == current.id }) {
            currentAgent = updated
        }
    }

    // MARK: - Send message

    func sendMessage(_ text: String) async {
        guard client != nil, currentAgent != nil, currentSessionId != nil else { return }

        let userMessage = ChatMessage(role: .user, text: text)
        messages.append(userMessage)
        isStreaming = true

        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        let assistantMessage = ChatMessage(id: assistantId, role: .assistant, blocks: [])
        messages.append(assistantMessage)

        await client?.sendMessage(text)
    }

    func cancelStreaming() async {
        await client?.cancelStreaming()
    }

    // MARK: - Streaming updates

    func appendTextDelta(_ delta: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendText(delta)
    }

    func appendThinkingDelta(_ delta: String, thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendThinking(delta, thinkingId: thinkingId)
    }

    func collapseThinking(thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].collapseThinking(thinkingId)
    }

    func appendImage(data: Data, mimeType: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].appendImage(data: data, mimeType: mimeType)
    }

    func addToolCall(toolId: String, name: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].addToolCall(toolId: toolId, name: name)
    }

    func completeToolCall(toolId: String, result: String?, success: Bool) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        messages[index].completeToolCall(toolId: toolId, result: result, success: success)
    }

    func finalizeStreamingMessage(usage: MessageUsage? = nil) {
        if let usage, let id = streamingMessageId, let index = lastMessageIndex(id: id) {
            messages[index].usage = usage
        }
    }

    // MARK: - Private helpers

    private func lastMessageIndex(id: String) -> Int? {
        messages.lastIndex(where: { $0.id == id })
    }
}
