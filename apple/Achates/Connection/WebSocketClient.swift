import Foundation

enum ConnectionStatus: String, Sendable {
    case disconnected
    case connecting
    case connected
    case reconnecting
}

@MainActor
final class WebSocketClient {
    private let appState: AppState
    private let router: DeviceCommandRouter

    private let pendingRequests = PendingRequestStore()
    private let webSocketHolder = WebSocketTaskHolder()
    private let reconnectAttemptsHolder = AtomicInt()
    private let shouldReconnectHolder = AtomicBool(true)

    init(appState: AppState) {
        self.appState = appState
        self.router = DeviceCommandRouter()
    }

    func connect(url: URL, agent: String) {
        // Idempotent: a second connect() while a socket is already in flight would
        // leave two receive loops alive and double-deliver every streaming event.
        // The reconnect path in handleDisconnect() nils out the holder before
        // calling us, so it still proceeds.
        guard webSocketHolder.get() == nil else { return }
        shouldReconnectHolder.set(true)
        reconnectAttemptsHolder.set(0)
        appState.connectionStatus = .connecting

        // Build WebSocket URL: convert http(s) to ws(s) and append /ws
        let wsURL = url.appendingPathComponent("ws")
        var components = URLComponents(url: wsURL, resolvingAgainstBaseURL: false)!
        if components.scheme == "http" { components.scheme = "ws" }
        else if components.scheme == "https" { components.scheme = "wss" }

        let session = URLSession(configuration: .default)
        let task = session.webSocketTask(with: components.url!)
        task.maximumMessageSize = 16 * 1024 * 1024 // 16 MB
        webSocketHolder.set(task)
        task.resume()

        Task { [weak self] in
            await self?.receiveLoop(task: task)
        }

        Task { [weak self] in
            await self?.performConnect()
        }
    }

    func disconnect() {
        shouldReconnectHolder.set(false)
        webSocketHolder.get()?.cancel(with: .normalClosure, reason: nil)
        webSocketHolder.set(nil)
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

    func sendMessage(_ text: String, attachments: [DraftAttachment] = []) async {
        guard let agent = appState.currentAgent,
              let sessionId = appState.currentSessionId else {
            print("Cannot send message: no agent or session selected")
            return
        }
        var params: [String: JSONValue] = [
            "text": .string(text),
            "agent": .string(agent.id),
            "session_id": .string(sessionId),
        ]
        if !attachments.isEmpty {
            let items: [JSONValue] = attachments.map { att in
                var fields: [String: JSONValue] = [
                    "mime": .string(att.mime),
                    "data": .string(att.data.base64EncodedString()),
                ]
                if let name = att.displayName {
                    fields["filename"] = .string(name)
                }
                return .object(fields)
            }
            params["attachments"] = .array(items)
        }
        do {
            _ = try await sendRequest(method: "chat.send", params: params)
        } catch {
            print("Failed to send message: \(error)")
        }
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

    private func performConnect() async {
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
            reconnectAttemptsHolder.set(0)
            appState.connectionStatus = .connected

            // Step 2: Fetch agent list
            if let agentsPayload = try await sendRequest(method: "agents.list") {
                appState.agents = Agent.fromList(agentsPayload)
                appState.updateAppBadge()
            }

            // Step 3: Re-sync the user's view. Events fired while we were
            // disconnected are not replayed by the server, so re-fetch what's
            // currently on screen.
            await appState.resyncCurrentView()
        } catch {
            print("Connect handshake failed: \(error)")
            appState.connectionStatus = .disconnected
        }
    }

    private func receiveLoop(task: URLSessionWebSocketTask) async {
        while shouldReconnectHolder.get() {
            do {
                let message = try await task.receive()
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
                if shouldReconnectHolder.get() {
                    await handleDisconnect()
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

    private func handleEvent(_ evt: EventFrame) {
        let payload = evt.payload ?? [:]

        // Streaming events are broadcast to all clients. Only apply them to the
        // currently-visible session — otherwise a cron job (or another tab/session)
        // streaming on the same connection would corrupt the user's open chat.
        let matchesCurrentSession: Bool = {
            guard let agentId = payload["agent"]?.stringValue,
                  let sessionId = payload["session_id"]?.stringValue else { return false }
            return agentId == appState.currentAgent?.id && sessionId == appState.currentSessionId
        }()

        switch evt.event {
        case "text.delta":
            guard matchesCurrentSession else { break }
            let delta = payload["delta"]?.stringValue ?? ""
            appState.appendTextDelta(delta)

        case "text.end":
            break

        case "thinking.delta":
            guard matchesCurrentSession else { break }
            let delta = payload["delta"]?.stringValue ?? ""
            let thinkingId = payload["id"]?.stringValue ?? "default"
            appState.appendThinkingDelta(delta, thinkingId: thinkingId)

        case "thinking.end":
            guard matchesCurrentSession else { break }
            let thinkingId = payload["id"]?.stringValue ?? "default"
            appState.collapseThinking(thinkingId: thinkingId)

        case "image.block":
            guard matchesCurrentSession else { break }
            if let b64 = payload["data"]?.stringValue,
               let data = Data(base64Encoded: b64) {
                let mimeType = payload["mime_type"]?.stringValue ?? "image/jpeg"
                appState.appendImage(data: data, mimeType: mimeType)
            }

        case "tool.start":
            guard matchesCurrentSession else { break }
            let toolId = payload["tool_call_id"]?.stringValue ?? UUID().uuidString
            let name = payload["tool_name"]?.stringValue ?? "unknown"
            appState.addToolCall(toolId: toolId, name: name)

        case "tool.end":
            guard matchesCurrentSession else { break }
            let toolId = payload["tool_call_id"]?.stringValue ?? ""
            let result = payload["result"]?.stringValue
            let success = !(payload["is_error"]?.boolValue ?? false)
            appState.completeToolCall(toolId: toolId, result: result, success: success)

        case "message.end":
            guard matchesCurrentSession else { break }
            var messageUsage: MessageUsage?
            if let usageDict = payload["usage"]?.objectValue,
               let input = usageDict["input"]?.intValue,
               let output = usageDict["output"]?.intValue {
                let cost = usageDict["cost"]?.doubleValue ?? Double(usageDict["cost"]?.intValue ?? 0)
                messageUsage = MessageUsage(inputTokens: input, outputTokens: output, cost: cost)
            }
            appState.finalizeStreamingMessage(usage: messageUsage)

        case "done":
            // Only clear the streaming spinner when the done is for what we're showing;
            // otherwise a cron's done would yank the spinner off a real in-flight chat.
            if matchesCurrentSession {
                appState.isStreaming = false
                appState.streamingMessageId = nil
                appState.markCurrentAgentAsRead()
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

    private func handleDisconnect() async {
        webSocketHolder.set(nil)
        appState.connectionStatus = .reconnecting

        let attempts = reconnectAttemptsHolder.increment()
        let delay = min(0.5 * pow(2.0, Double(attempts - 1)), 30.0)

        try? await Task.sleep(for: .milliseconds(Int(delay * 1000)))

        guard shouldReconnectHolder.get(), let url = appState.serverURL, let agent = appState.currentAgent else {
            appState.connectionStatus = .disconnected
            return
        }

        connect(url: url, agent: agent.id)
    }

}

// MARK: - Thread-safe helpers

private final class PendingRequestStore: Sendable {
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
}

private final class WebSocketTaskHolder: Sendable {
    private let lock = NSLock()
    private nonisolated(unsafe) var task: URLSessionWebSocketTask?

    func set(_ newTask: URLSessionWebSocketTask?) {
        lock.lock()
        task = newTask
        lock.unlock()
    }

    func get() -> URLSessionWebSocketTask? {
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
