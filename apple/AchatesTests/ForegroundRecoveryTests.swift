import XCTest
import SwiftUI
@testable import Achates

@MainActor
final class ForegroundRecoveryTests: XCTestCase {
    private func state() -> AppState {
        let state = AppState()
        state.serverURL = URL(string: "https://foreground-test.invalid")
        state.currentAgent = Agent(id: "maya", name: "maya", displayName: "Maya", description: "",
                                   tools: [], lastMessage: nil, lastActivity: nil, unreadCount: 0, avatarData: nil)
        state.currentSessionId = "older-session"
        state.isStreaming = true
        state.streamingMessageId = "partial"
        state.messages = [ChatMessage(id: "partial", role: .assistant, text: "Before suspension")]
        return state
    }

    private func history(running: Bool = false) -> [String: JSONValue] {
        ["is_running": .bool(running), "messages": .array([
            .object(["role": .string("assistant"), "content": .array([
                .object(["type": .string("text"), "text": .string("Completed while away")])
            ])])
        ])]
    }

    private func server(_ socket: SuspendedSocket, history: [String: JSONValue]) {
        socket.onRequest = { request in
            switch request.method {
            case "sessions.get": return history
            case "sessions.list": return ["sessions": .array([]), "has_more": .bool(true)]
            default: return ["agents": .array([.object(["name": .string("maya")])])]
            }
        }
    }

    private func eventually(_ predicate: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<200 {
            if predicate() { return }
            try? await Task.sleep(for: .milliseconds(10))
        }
        XCTFail("Expected state was not reached", file: file, line: line)
    }

    func testForegroundReplacesSilentSocketAndRestoresCompletedReplyAndDraft() async {
        let state = state()
        let oldSocket = SuspendedSocket() // Neither returns frames nor reports failure.
        let freshSocket = SuspendedSocket()
        server(freshSocket, history: history())
        var sockets = [oldSocket, freshSocket]
        let client = WebSocketClient(appState: state, makeSocket: { _ in sockets.removeFirst() })
        state.client = client
        defer { client.disconnect() }
        state.connectToServer()
        await eventually { oldSocket.requests.contains(where: { $0.method == "connect" }) }
        state.connectionStatus = .connected // Status left over from before suspension.
        let draft = state.draft(for: "maya", sessionID: "older-session")
        draft.text = "Next question"
        draft.attachments = [DraftAttachment(data: Data([1]), mime: "text/plain", displayName: "note.txt")]

        state.handleScenePhaseChange(.background)
        XCTAssertFalse(oldSocket.cancelled) // Backgrounding itself does not stop the server turn.
        state.handleScenePhaseChange(.inactive)
        state.handleScenePhaseChange(.active)

        await eventually { !state.isStreaming && freshSocket.requests.contains(where: { $0.method == "sessions.list" }) }
        XCTAssertTrue(oldSocket.cancelled)
        XCTAssertEqual(state.connectionStatus, .connected)
        XCTAssertEqual(state.currentSessionId, "older-session")
        XCTAssertEqual(state.messages.last?.textContent, "Completed while away")
        XCTAssertNil(state.streamingMessageId)
        XCTAssertTrue(state.canSubmitMessage)
        XCTAssertEqual(draft.text, "Next question")
        XCTAssertEqual(draft.attachments.count, 1)
        XCTAssertFalse(freshSocket.requests.contains(where: { $0.method.hasPrefix("chat.") }))
        XCTAssertEqual(freshSocket.requests.filter { $0.method == "sessions.get" }.first?.params?["session_id"], .string("older-session"))
    }

    func testInactiveWithoutBackgroundDoesNotReplaceSocket() async {
        let state = state()
        let socket = SuspendedSocket()
        server(socket, history: history(running: true))
        var creations = 0
        let client = WebSocketClient(appState: state, makeSocket: { _ in creations += 1; return socket })
        state.client = client
        defer { client.disconnect() }
        state.connectToServer()
        await eventually { socket.requests.contains(where: { $0.method == "sessions.list" }) }
        state.handleScenePhaseChange(.inactive)
        state.handleScenePhaseChange(.active)
        await eventually { socket.requests.filter { $0.method == "sessions.get" }.count == 2 }
        XCTAssertEqual(creations, 1)
        XCTAssertFalse(socket.cancelled)
        XCTAssertTrue(state.isStreaming)
        XCTAssertEqual(state.messages.last?.textContent, "Before suspension")
    }

    func testDoneDuringHistoryRequestStillRestoresMissingText() async {
        let state = state()
        let socket = SuspendedSocket()
        let client = WebSocketClient(appState: state, makeSocket: { _ in socket })
        state.client = client
        defer { client.disconnect() }
        let completed = history()
        socket.onRequest = { request in
            if request.method == "sessions.get" {
                client.handleEvent(EventFrame(type: .evt, event: "done", payload: [
                    "agent": .string("maya"), "session_id": .string("older-session")
                ], seq: nil))
                return completed
            }
            return ["agents": .array([.object(["name": .string("maya")])])]
        }
        state.connectToServer()
        await eventually { socket.requests.contains(where: { $0.method == "sessions.list" }) }
        XCTAssertEqual(state.messages.last?.textContent, "Completed while away")
        XCTAssertFalse(state.isStreaming)
    }

    func testHistoryCannotOverwriteNewerTurnEvenIfItAlreadyFinished() async {
        let state = state()
        let socket = SuspendedSocket()
        let client = WebSocketClient(appState: state, makeSocket: { _ in socket })
        state.client = client
        defer { client.disconnect() }
        let completed = history()
        socket.onRequest = { request in
            if request.method == "sessions.get" {
                state.streamingMessageId = "newer-turn"
                state.messages = [ChatMessage(role: .assistant, text: "Newer reply")]
                state.isStreaming = false
                state.streamingMessageId = nil
                return completed
            }
            return ["agents": .array([.object(["name": .string("maya")])])]
        }
        state.connectToServer()
        await eventually { socket.requests.contains(where: { $0.method == "sessions.list" }) }
        XCTAssertEqual(state.messages.last?.textContent, "Newer reply")
        XCTAssertFalse(state.isStreaming)
    }

    func testRunningSnapshotCannotReplaceReplyThatFinishesDuringFetch() async {
        let state = state()
        let socket = SuspendedSocket()
        let client = WebSocketClient(appState: state, makeSocket: { _ in socket })
        state.client = client
        defer { client.disconnect() }
        let stale = history(running: true)
        socket.onRequest = { request in
            if request.method == "sessions.get" {
                state.currentConversation.appendTextDelta(" and finished live")
                client.handleEvent(EventFrame(type: .evt, event: "done", payload: [
                    "agent": .string("maya"), "session_id": .string("older-session")
                ], seq: nil))
                return stale
            }
            return ["agents": .array([.object(["name": .string("maya")])])]
        }
        state.connectToServer()
        await eventually { socket.requests.contains(where: { $0.method == "sessions.list" }) }
        XCTAssertEqual(state.messages.last?.textContent, "Before suspension and finished live")
        XCTAssertFalse(state.isStreaming)
    }

    func testDelayedReconnectCannotChangeStatusAfterExplicitDisconnect() async {
        let state = state()
        let socket = SuspendedSocket()
        let client = WebSocketClient(appState: state, makeSocket: { _ in socket })
        state.client = client
        state.connectToServer()
        await eventually { socket.requests.contains(where: { $0.method == "connect" }) }
        socket.failReceive()
        await eventually { state.connectionStatus == .reconnecting }
        client.disconnect()
        state.connectionStatus = .connecting // A newer connection now owns the status.
        try? await Task.sleep(for: .milliseconds(650))
        XCTAssertEqual(state.connectionStatus, .connecting)
    }
}

/// Models a socket that can silently stop delivering frames while still held by the client.
@MainActor
private final class SuspendedSocket: WebSocketTransport {
    var maximumMessageSize = 0
    var closeCode: URLSessionWebSocketTask.CloseCode = .invalid
    var cancelled = false
    var requests: [RequestFrame] = []
    var onRequest: ((RequestFrame) -> [String: JSONValue]?)?
    private var buffered: [URLSessionWebSocketTask.Message] = []
    private var receiver: CheckedContinuation<URLSessionWebSocketTask.Message, Error>?

    func resume() {}

    func cancel(with closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        cancelled = true
        self.closeCode = closeCode
        failReceive()
    }

    func failReceive() {
        let pending = receiver
        receiver = nil
        pending?.resume(throwing: URLError(.networkConnectionLost))
    }

    func send(_ message: URLSessionWebSocketTask.Message) async throws {
        guard !cancelled else { throw URLError(.cancelled) }
        guard case .data(let data) = message else { return }
        let request = try JSONDecoder().decode(RequestFrame.self, from: data)
        requests.append(request)
        guard let payload = onRequest?(request) else { return }
        let response = ResponseFrame(id: request.id, ok: true, payload: payload)
        let message = URLSessionWebSocketTask.Message.data(try JSONEncoder().encode(response))
        if let pending = receiver {
            receiver = nil
            pending.resume(returning: message)
        } else {
            buffered.append(message)
        }
    }

    func receive() async throws -> URLSessionWebSocketTask.Message {
        if !buffered.isEmpty { return buffered.removeFirst() }
        if cancelled { throw URLError(.cancelled) }
        return try await withCheckedThrowingContinuation { receiver = $0 }
    }
}
