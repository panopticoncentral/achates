import Foundation
import SwiftUI
import UserNotifications

/// A display item in the unified timeline — either a chat message or a session break divider.
enum TimelineItem: Identifiable, Equatable {
    case message(ChatMessage)
    case sessionBreak(id: String, segmentId: String, date: Date)

    var id: String {
        switch self {
        case .message(let m): return m.id
        case .sessionBreak(let id, _, _): return id
        }
    }
}

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

@Observable
@MainActor
final class AppState {
    var connectionStatus: ConnectionStatus = .disconnected
    var serverURL: URL?
    var agents: [Agent] = []
    var currentAgent: Agent?
    var timeline: [TimelineItem] = []
    var isStreaming = false
    var streamingMessageId: String?
    var client: WebSocketClient?
    var error: String?

    /// The current (latest) session ID, tracked from chat.send responses
    var currentSessionId: String?

    /// Segments loaded from the server, kept for break add/remove operations
    private var segments: [TimelineSegment] = []

    /// Whether there are older segments to load when scrolling up
    var hasMoreHistory = true

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
        timeline = []
        segments = []
        currentSessionId = nil
    }

    func selectAgent(_ agent: Agent) async {
        currentAgent = agent
        timeline = []
        segments = []
        currentSessionId = nil
        hasMoreHistory = true

        await loadTimeline()
        markCurrentAgentAsRead()
    }

    func markCurrentAgentAsRead() {
        guard let agent = currentAgent, client != nil else { return }

        // Find the latest message timestamp in the timeline
        let latestTimestamp: Int? = timeline.reversed().compactMap { item -> Int? in
            if case .message(let msg) = item {
                return Int(msg.timestamp.timeIntervalSince1970 * 1000)
            }
            return nil
        }.first

        guard let ts = latestTimestamp else { return }

        // Optimistic local update
        if let index = agents.firstIndex(where: { $0.id == agent.id }), agents[index].unreadCount > 0 {
            agents[index].unreadCount = 0
            updateAppBadge()
        }

        // Fire and forget to server
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

    // MARK: - Timeline loading

    func loadTimeline() async {
        guard let agent = currentAgent, client != nil else { return }

        do {
            let payload = try await client?.sendRequest(method: "timeline.load", params: [
                "agent": .string(agent.id),
            ])
            if let payload {
                let newSegments = TimelineSegment.fromTimeline(payload, agentId: agent.id)
                segments = newSegments
                hasMoreHistory = newSegments.count >= 50
                rebuildTimeline()

                // Track the latest session
                currentSessionId = segments.last?.id
            }
        } catch {
            self.error = "Failed to load timeline: \(error.localizedDescription)"
        }
    }

    func loadMoreHistory() async {
        guard let agent = currentAgent, client != nil, hasMoreHistory else { return }
        guard let oldestSegment = segments.first else { return }

        let beforeMs = Int(oldestSegment.created.timeIntervalSince1970 * 1000)

        do {
            let payload = try await client?.sendRequest(method: "timeline.load", params: [
                "agent": .string(agent.id),
                "before": .int(beforeMs),
            ])
            if let payload {
                let olderSegments = TimelineSegment.fromTimeline(payload, agentId: agent.id)
                hasMoreHistory = olderSegments.count >= 50
                segments.insert(contentsOf: olderSegments, at: 0)
                rebuildTimeline()
            }
        } catch {
            self.error = "Failed to load history: \(error.localizedDescription)"
        }
    }

    // MARK: - Break management

    func addBreak(afterMessage message: ChatMessage) async {
        guard let agent = currentAgent, client != nil else { return }

        // Find which segment this message belongs to
        guard let segment = segments.first(where: { seg in
            seg.messages.contains(where: { $0.id == message.id })
        }) else { return }

        do {
            let payload = try await client?.sendRequest(method: "timeline.break.add", params: [
                "agent": .string(agent.id),
                "session_id": .string(segment.id),
                "after_message_timestamp": .int(Int(message.timestamp.timeIntervalSince1970 * 1000)),
            ])
            if let _ = payload?["new_segment_id"]?.stringValue {
                // Reload the timeline to get the updated segments
                await loadTimeline()
            }
        } catch {
            self.error = "Failed to add break: \(error.localizedDescription)"
        }
    }

    func removeBreak(segmentId: String) async {
        guard let agent = currentAgent, client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "timeline.break.remove", params: [
                "agent": .string(agent.id),
                "segment_id": .string(segmentId),
            ])
            // Reload to get updated segments
            await loadTimeline()
        } catch {
            self.error = "Failed to remove break: \(error.localizedDescription)"
        }
    }

    func clearTimeline() async {
        guard let agent = currentAgent, client != nil else { return }

        do {
            _ = try await client?.sendRequest(method: "timeline.clear", params: [
                "agent": .string(agent.id),
            ])
            timeline = []
            segments = []
            currentSessionId = nil
        } catch {
            self.error = "Failed to clear timeline: \(error.localizedDescription)"
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

        // Handle rename first if display name changed
        if config.displayName != original.displayName && !config.displayName.isEmpty {
            guard let renamePayload = try await client?.sendRequest(method: "agent.rename", params: [
                "agent": .string(agent.id),
                "name": .string(config.displayName),
            ]) else {
                throw AgentEditError.notConnected
            }
            // Update agent ID for subsequent update call
            if let newId = renamePayload["id"]?.stringValue {
                currentAgentId = newId
            }
            await refreshAgents()
        }

        // Save other config changes
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
        guard client != nil, currentAgent != nil else { return }

        let userMessage = ChatMessage(role: .user, text: text)
        timeline.append(.message(userMessage))
        isStreaming = true

        let assistantId = UUID().uuidString
        streamingMessageId = assistantId
        let assistantMessage = ChatMessage(id: assistantId, role: .assistant, blocks: [])
        timeline.append(.message(assistantMessage))

        await client?.sendMessage(text)
    }

    func handleChatSendResponse(sessionId: String, isNewSession: Bool) {
        if isNewSession && currentSessionId != nil {
            // Server auto-created a new session — insert a break divider before our latest messages
            // Find where to insert: before the user message we just appended
            let insertIndex = max(0, timeline.count - 2)
            let breakItem = TimelineItem.sessionBreak(
                id: "break-\(sessionId)",
                segmentId: sessionId,
                date: Date()
            )
            timeline.insert(breakItem, at: insertIndex)
        }
        currentSessionId = sessionId
    }

    func cancelStreaming() async {
        await client?.cancelStreaming()
    }

    // MARK: - Streaming updates

    func appendTextDelta(_ delta: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        if case .message(var msg) = timeline[index] {
            msg.appendText(delta)
            timeline[index] = .message(msg)
        }
    }

    func appendThinkingDelta(_ delta: String, thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        if case .message(var msg) = timeline[index] {
            msg.appendThinking(delta, thinkingId: thinkingId)
            timeline[index] = .message(msg)
        }
    }

    func collapseThinking(thinkingId: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        if case .message(var msg) = timeline[index] {
            msg.collapseThinking(thinkingId)
            timeline[index] = .message(msg)
        }
    }

    func addToolCall(toolId: String, name: String) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        if case .message(var msg) = timeline[index] {
            msg.addToolCall(toolId: toolId, name: name)
            timeline[index] = .message(msg)
        }
    }

    func completeToolCall(toolId: String, result: String?, success: Bool) {
        guard let id = streamingMessageId,
              let index = lastMessageIndex(id: id) else { return }
        if case .message(var msg) = timeline[index] {
            msg.completeToolCall(toolId: toolId, result: result, success: success)
            timeline[index] = .message(msg)
        }
    }

    func finalizeStreamingMessage() {
        // Message is complete but stream might continue (e.g., tool results leading to more text)
    }

    // MARK: - Private helpers

    private func lastMessageIndex(id: String) -> Int? {
        timeline.lastIndex(where: {
            if case .message(let msg) = $0 { return msg.id == id }
            return false
        })
    }

    /// Rebuild the flat timeline from segments, inserting break dividers between them.
    private func rebuildTimeline() {
        var items: [TimelineItem] = []

        for (i, segment) in segments.enumerated() {
            // Insert a break divider between segments (not before the first one)
            if i > 0 {
                items.append(.sessionBreak(
                    id: "break-\(segment.id)",
                    segmentId: segment.id,
                    date: segment.created
                ))
            }

            for message in segment.messages {
                items.append(.message(message))
            }
        }

        timeline = items
    }
}
