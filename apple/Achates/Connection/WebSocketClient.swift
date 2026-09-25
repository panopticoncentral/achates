import Foundation

enum ConnectionStatus: String, Sendable {
    case disconnected
    case connecting
    case connected
    case reconnecting
}

/// The socket boundary also lets lifecycle tests simulate a suspended connection.
@MainActor
protocol WebSocketTransport: AnyObject {
    var maximumMessageSize: Int { get set }
    var closeCode: URLSessionWebSocketTask.CloseCode { get }
    func resume()
    func cancel(with closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?)
    func send(_ message: URLSessionWebSocketTask.Message) async throws
    func receive() async throws -> URLSessionWebSocketTask.Message
}

extension URLSessionWebSocketTask: WebSocketTransport {}

@MainActor
final class WebSocketClient {
    // History responses include inline attachments and can exceed 16 MiB.
    static let maximumMessageSize = 64 * 1024 * 1024

    private let appState: AppState
    private let router: DeviceCommandRouter
    private let makeSocket: (URL) -> any WebSocketTransport
    private var connectionGeneration = UUID()

    private let pendingRequests = PendingRequestStore()
    private let webSocketHolder = WebSocketTaskHolder()
    private let reconnectAttemptsHolder = AtomicInt()
    private let shouldReconnectHolder = AtomicBool(true)

    init(appState: AppState, makeSocket: @escaping (URL) -> any WebSocketTransport = {
        URLSession(configuration: .default).webSocketTask(with: $0)
    }) {
        self.appState = appState
        self.router = DeviceCommandRouter()
        self.makeSocket = makeSocket
    }

    func connect(url: URL, agent: String) {
        // Idempotent: a second connect() while a socket is already in flight would
        // leave two receive loops alive and double-deliver every streaming event.
        // The reconnect path in handleDisconnect() nils out the holder before
        // calling us, so it still proceeds.
        guard webSocketHolder.get() == nil else { return }
        connectionGeneration = UUID()
        shouldReconnectHolder.set(true)
        reconnectAttemptsHolder.set(0)
        appState.connectionStatus = .connecting

        // Build WebSocket URL: convert http(s) to ws(s) and append /ws
        let wsURL = url.appendingPathComponent("ws")
        var components = URLComponents(url: wsURL, resolvingAgainstBaseURL: false)!
        if components.scheme == "http" { components.scheme = "ws" }
        else if components.scheme == "https" { components.scheme = "wss" }

        let task = makeSocket(components.url!)
        task.maximumMessageSize = Self.maximumMessageSize
        webSocketHolder.set(task)
        task.resume()

        Task { [weak self] in
            await self?.receiveLoop(task: task)
        }

        Task { [weak self] in
            await self?.performConnect(task: task)
        }
    }

    func disconnect() {
        connectionGeneration = UUID()
        shouldReconnectHolder.set(false)
        webSocketHolder.get()?.cancel(with: .normalClosure, reason: nil)
        webSocketHolder.set(nil)
        pendingRequests.failAll(with: FrameError.notConnected)
        appState.connectionStatus = .disconnected
    }

    func sendRequest(method: String, params: [String: JSONValue]? = nil, timeout: Duration = .seconds(30)) async throws -> [String: JSONValue]? {
        guard let task = webSocketHolder.get() else {
            throw FrameError.notConnected
        }

        let frame = RequestFrame(method: method, params: params)
        let data = try JSONEncoder().encode(frame)
        let frameId = frame.id

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<[String: JSONValue]?, Error>) in
            self.pendingRequests.set(frameId, continuation: continuation)

            Task { [weak self] in
                guard let self else { return }
                do {
                    try await task.send(.data(data))
                } catch {
                    if let cont = self.pendingRequests.remove(frameId) {
                        cont.resume(throwing: error)
                    }
                }
            }

            Task { [weak self] in
                guard let self else { return }
                try? await Task.sleep(for: timeout)
                if let cont = self.pendingRequests.remove(frameId) {
                    cont.resume(throwing: FrameError.timeout)
                }
            }
        }
    }

    func sendMessage(_ text: String, attachments: [DraftAttachment] = []) async throws {
        guard let agent = appState.currentAgent,
              let sessionId = appState.currentSessionId else {
            throw FrameError.notConnected
        }
        var params: [String: JSONValue] = [
            "text": .string(text),
            "agent": .string(agent.id),
            "session_id": .string(sessionId),
        ]
        if !attachments.isEmpty {
            params["attachments"] = .array(attachments.map(encodeAttachment))
        }
        _ = try await sendRequest(method: "chat.send", params: params)
    }

    /// Resubmit a user prompt. `promptIndex` is the 0-based user-turn ordinal to
    /// rewind to; pass `nil` to rewind the latest user turn. Pass `text: nil` to
    /// keep the original text; pass `attachments: nil` to keep the original
    /// attachments. An empty `attachments` array explicitly clears them.
    func resubmit(promptIndex: Int?, text: String?, attachments: [DraftAttachment]?) async throws {
        guard let agent = appState.currentAgent,
              let sessionId = appState.currentSessionId else {
            print("Cannot resubmit: no agent or session selected")
            throw FrameError.notConnected
        }
        var params: [String: JSONValue] = [
            "agent": .string(agent.id),
            "session_id": .string(sessionId),
        ]
        if let promptIndex {
            params["prompt_index"] = .int(promptIndex)
        }
        if let text {
            params["text"] = .string(text)
        }
        if let attachments {
            params["attachments"] = .array(attachments.map(encodeAttachment))
        }
        _ = try await sendRequest(method: "chat.resubmit", params: params)
    }

    private func encodeAttachment(_ att: DraftAttachment) -> JSONValue {
        var fields: [String: JSONValue] = [
            "mime": .string(att.mime),
            "data": .string(att.data.base64EncodedString()),
        ]
        if let name = att.displayName {
            fields["filename"] = .string(name)
        }
        return .object(fields)
    }

    func cancelStreaming() async {
        guard let agent = appState.currentAgent else { return }
        do {
            _ = try await sendRequest(method: "chat.cancel", params: [
                "agent": .string(agent.id),
            ])
        } catch {
            print("Failed to cancel: \(error)")
        }
    }

    // MARK: - Private

    private func performConnect(task: any WebSocketTransport) async {
        guard webSocketHolder.get() === task else { return }
        do {
            // Step 1: Handshake
            #if os(macOS)
            let clientId = "macos"
            let capabilities: [JSONValue] = [.string("location")]
            #else
            let clientId = "ios"
            let capabilities: [JSONValue] = [.string("location"), .string("camera")]
            #endif
            _ = try await sendRequest(method: "connect", params: [
                "client": .string(clientId),
                "version": .string("1.0"),
                "capabilities": .array(capabilities)
            ])
            guard webSocketHolder.get() === task else { return }
            reconnectAttemptsHolder.set(0)
            appState.connectionStatus = .connected
            appState.lastConnectionError = nil

            // Step 2: Fetch agent list
            if let agentsPayload = try await sendRequest(method: "agents.list") {
                guard webSocketHolder.get() === task else { return }
                appState.agents = Agent.fromList(agentsPayload)
                appState.updateAppBadge()
            }

            // Step 3: Re-sync the user's view. Events fired while we were
            // disconnected are not replayed by the server, so re-fetch what's
            // currently on screen.
            await appState.resyncCurrentView()
        } catch {
            guard webSocketHolder.get() === task else { return }
            // Keep the reason around: the settings/onboarding surfaces show it so a
            // failed connect isn't just a form that silently did nothing.
            appState.lastConnectionError = error.localizedDescription
            appState.connectionStatus = .disconnected
        }
    }

    private func receiveLoop(task: any WebSocketTransport) async {
        while shouldReconnectHolder.get() {
            do {
                let message = try await task.receive()
                guard webSocketHolder.get() === task else { return }
                let data: Data
                switch message {
                case .data(let d):
                    data = d
                case .string(let s):
                    data = Data(s.utf8)
                @unknown default:
                    continue
                }

                let frame = try Frame.parse(data)
                await handleFrame(frame)

            } catch {
                if shouldReconnectHolder.get(), webSocketHolder.get() === task {
                    await handleDisconnect(error: error, task: task)
                }
                return
            }
        }
    }

    private func handleFrame(_ frame: Frame) async {
        switch frame {
        case .response(let res):
            if let cont = pendingRequests.remove(res.id) {
                if res.ok {
                    cont.resume(returning: res.payload)
                } else {
                    let msg = res.error?.message ?? "Unknown error"
                    cont.resume(throwing: FrameError.serverError(msg))
                }
            }

        case .event(let evt):
            handleEvent(evt)

        case .request(let req):
            await handleServerRequest(req)
        }
    }

    func handleEvent(_ evt: EventFrame) {
        let payload = evt.payload ?? [:]

        // Text and reply lifecycle updates follow their owning conversation,
        // including while it is offscreen. Audio playback stays with the visible chat.
        let conversation: ChatSessionState? = {
            guard let agentID = payload["agent"]?.stringValue,
                  let sessionID = payload["session_id"]?.stringValue else { return nil }
            return appState.conversation(agentID: agentID, sessionID: sessionID)
        }()
        let matchesCurrentSession: Bool = {
            guard let agentId = payload["agent"]?.stringValue,
                  let sessionId = payload["session_id"]?.stringValue else { return false }
            return agentId == appState.currentAgent?.id && sessionId == appState.currentSessionId
        }()

        switch evt.event {
        case "text.delta":
            guard let conversation else { break }
            let delta = payload["delta"]?.stringValue ?? ""
            conversation.appendTextDelta(delta)

        case "text.end":
            break

        case "thinking.delta":
            guard let conversation else { break }
            let delta = payload["delta"]?.stringValue ?? ""
            let thinkingId = payload["id"]?.stringValue ?? "default"
            conversation.appendThinkingDelta(delta, thinkingId: thinkingId)

        case "thinking.end":
            guard let conversation else { break }
            let thinkingId = payload["id"]?.stringValue ?? "default"
            conversation.collapseThinking(thinkingId: thinkingId)

        case "image.block":
            guard let conversation else { break }
            if let b64 = payload["data"]?.stringValue,
               let data = Data(base64Encoded: b64) {
                let mimeType = payload["mime_type"]?.stringValue ?? "image/jpeg"
                conversation.appendImage(data: data, mimeType: mimeType)
            }

        case "tool.start":
            guard let conversation else { break }
            let toolId = payload["tool_call_id"]?.stringValue ?? UUID().uuidString
            let name = payload["tool_name"]?.stringValue ?? "unknown"
            conversation.addToolCall(toolId: toolId, name: name)

        case "tool.end":
            guard let conversation else { break }
            let toolId = payload["tool_call_id"]?.stringValue ?? ""
            let result = payload["result"]?.stringValue
            let success = !(payload["is_error"]?.boolValue ?? false)
            conversation.completeToolCall(toolId: toolId, result: result, success: success)

        case "agent_turn.start":
            guard let conversation else { break }
            // Mint a fresh, unique block id per utterance. The payload `id` is
            // the speaker agent id, which is constant across multiple rounds by
            // the same speaker in one initiator turn — reusing it would merge
            // distinct utterances into one bubble live (self-heals only on
            // reload). A fresh UUID guarantees each utterance gets its own block.
            let turnId = UUID().uuidString
            let name = payload["agent_name"]?.stringValue ?? "agent"
            conversation.startAgentTurn(agentTurnId: turnId, agentName: name)

        case "agent_turn.delta":
            guard let conversation else { break }
            conversation.appendAgentTurnDelta(payload["delta"]?.stringValue ?? "")

        case "agent_turn.end":
            guard let conversation else { break }
            // `text` is the full final text of this utterance. Apply it as the
            // authoritative content (fills the no-delta initiator line and
            // reconciles the streamed target line) before collapsing.
            let text = payload["text"]?.stringValue ?? ""
            conversation.endAgentTurn(text)

        case "audio.block":
            guard matchesCurrentSession, let conversation,
                  let turnId = payload["turn_id"]?.stringValue,
                  let sentenceIndex = payload["sentence_index"]?.intValue,
                  let dataString = payload["data"]?.stringValue,
                  let data = Data(base64Encoded: dataString) else { break }
            appState.speechPlayer.enqueue(turnId: turnId, sentenceIndex: sentenceIndex, mp3Data: data)
            let speechText = payload["text"]?.stringValue ?? ""
            conversation.recordAudioMetadata(turnId: turnId, sentenceIndex: sentenceIndex, text: speechText)

        case "audio.error":
            guard matchesCurrentSession, let conversation else { break }
            let turnId = payload["turn_id"]?.stringValue ?? "<unknown>"
            let message = payload["message"]?.stringValue ?? "Speech unavailable."
            conversation.recordAudioError(turnId: turnId, message: message)

        case "message.end":
            guard let conversation else { break }
            var messageUsage: MessageUsage?
            if let usageDict = payload["usage"]?.objectValue,
               let input = usageDict["input"]?.intValue,
               let output = usageDict["output"]?.intValue {
                let cost = usageDict["cost"]?.doubleValue ?? Double(usageDict["cost"]?.intValue ?? 0)
                messageUsage = MessageUsage(inputTokens: input, outputTokens: output, cost: cost)
            }
            conversation.finalizeStreamingMessage(usage: messageUsage)

        case "done":
            conversation?.applyTurnOutcome(payload)
            conversation?.isStreaming = false
            conversation?.streamingMessageId = nil
            if matchesCurrentSession {
                appState.markCurrentSessionAsRead()
            }
            // The cost ledger just changed — drop any cached summaries so the
            // next read goes back to the server.
            appState.invalidateCostSummaries()
            Task { await appState.refreshAgents() }

        case "session.updated":
            guard let agentId = payload["agent"]?.stringValue else { break }
            if let sessionDict = payload["session"]?.objectValue,
               let info = SessionInfo.from(dict: sessionDict) {
                appState.upsertSession(agentId: agentId, info: info)
            } else if let sessionId = payload["session_id"]?.stringValue,
                      let title = payload["title"]?.stringValue {
                // Backwards-compatible title-only payload
                appState.updateSessionTitle(sessionId: sessionId, title: title)
            }

        case "cron.result":
            // session.updated is now broadcast alongside cron.result, so the list
            // already has the new session. Nothing else to do here today.
            break

        case "agents.changed":
            Task { await appState.refreshAgents() }

        case "agent.renamed":
            let oldId = payload["old_id"]?.stringValue
            let newId = payload["new_id"]?.stringValue
            Task { await appState.handleAgentRenamed(oldId: oldId, newId: newId) }

        case "memory.updated":
            if let scope = payload["scope"]?.stringValue {
                appState.handleMemoryUpdated(scope: scope)
            }

        case "jobs.updated":
            appState.handleJobsUpdated()

        default:
            print("Unknown event: \(evt.event)")
        }
    }

    private func handleServerRequest(_ req: RequestFrame) async {
        let response = await router.handle(method: req.method, params: req.params ?? [:])

        let resFrame: ResponseFrame
        switch response {
        case .success(let payload):
            resFrame = ResponseFrame(id: req.id, ok: true, payload: payload)
        case .failure(let error):
            resFrame = ResponseFrame(id: req.id, ok: false, error: ResponseError(code: "error", message: error.localizedDescription))
        }

        if let task = webSocketHolder.get() {
            do {
                let data = try JSONEncoder().encode(resFrame)
                try await task.send(.data(data))
            } catch {
                print("Failed to send response: \(error)")
            }
        }
    }

    static func isOversizedMessage(_ error: Error, closeCode: URLSessionWebSocketTask.CloseCode) -> Bool {
        let error = error as NSError
        return closeCode == .messageTooBig
            || (error.domain == NSPOSIXErrorDomain && error.code == Int(EMSGSIZE))
    }

    private func handleDisconnect(error: Error, task: any WebSocketTransport) async {
        let generation = connectionGeneration
        let oversized = Self.isOversizedMessage(error, closeCode: task.closeCode)
        webSocketHolder.set(nil)
        task.cancel(with: .goingAway, reason: nil)
        let failure: Error = oversized ? FrameError.messageTooLarge : error
        pendingRequests.failAll(with: failure)
        appState.lastConnectionError = failure.localizedDescription

        // Retrying the same history cannot fix a size limit. Keep the error
        // visible and let the user choose when to reconnect.
        if oversized {
            shouldReconnectHolder.set(false)
            appState.connectionStatus = .disconnected
            if appState.currentSessionId != nil {
                appState.historyLoadError = failure.localizedDescription
                appState.isLoadingHistory = false
            }
            return
        }
        appState.connectionStatus = .reconnecting

        let attempts = reconnectAttemptsHolder.increment()
        let delay = min(0.5 * pow(2.0, Double(attempts - 1)), 30.0)

        try? await Task.sleep(for: .milliseconds(Int(delay * 1000)))

        // A foreground reconnect or explicit disconnect supersedes this retry.
        guard connectionGeneration == generation else { return }
        guard shouldReconnectHolder.get(), let url = appState.serverURL, let agent = appState.currentAgent else {
            appState.connectionStatus = .disconnected
            return
        }

        connect(url: url, agent: agent.id)
    }

}

// MARK: - Thread-safe helpers

final class PendingRequestStore: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var store: [String: CheckedContinuation<[String: JSONValue]?, Error>] = [:]

    func set(_ id: String, continuation: CheckedContinuation<[String: JSONValue]?, Error>) {
        lock.lock()
        store[id] = continuation
        lock.unlock()
    }

    func remove(_ id: String) -> CheckedContinuation<[String: JSONValue]?, Error>? {
        lock.lock()
        let cont = store.removeValue(forKey: id)
        lock.unlock()
        return cont
    }

    func failAll(with error: Error) {
        lock.lock()
        let pending = Array(store.values)
        store.removeAll()
        lock.unlock()
        for continuation in pending {
            continuation.resume(throwing: error)
        }
    }
}

private final class WebSocketTaskHolder: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var task: (any WebSocketTransport)?

    func set(_ newTask: (any WebSocketTransport)?) {
        lock.lock()
        task = newTask
        lock.unlock()
    }

    func get() -> (any WebSocketTransport)? {
        lock.lock()
        let t = task
        lock.unlock()
        return t
    }
}

private final class AtomicInt: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var value: Int = 0

    func get() -> Int {
        lock.lock()
        let v = value
        lock.unlock()
        return v
    }

    func set(_ newValue: Int) {
        lock.lock()
        value = newValue
        lock.unlock()
    }

    @discardableResult
    func increment() -> Int {
        lock.lock()
        value += 1
        let v = value
        lock.unlock()
        return v
    }
}

private final class AtomicBool: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var value: Bool

    init(_ initial: Bool) {
        self.value = initial
    }

    func get() -> Bool {
        lock.lock()
        let v = value
        lock.unlock()
        return v
    }

    func set(_ newValue: Bool) {
        lock.lock()
        value = newValue
        lock.unlock()
    }
}
