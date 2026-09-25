import XCTest
@testable import Achates

@MainActor
final class WebSocketFailureTests: XCTestCase {
    func testMessageTooLongStopsAutomaticRetry() {
        // This is the error emitted by URLSession for the observed 16,879,366-byte response.
        let error = NSError(domain: NSPOSIXErrorDomain, code: Int(EMSGSIZE))
        XCTAssertTrue(WebSocketClient.isOversizedMessage(error, closeCode: .invalid))
        XCTAssertTrue(WebSocketClient.isOversizedMessage(URLError(.networkConnectionLost), closeCode: .messageTooBig))
    }

    func testTransientNetworkFailuresCanStillReconnect() {
        XCTAssertFalse(WebSocketClient.isOversizedMessage(URLError(.networkConnectionLost), closeCode: .invalid))
        XCTAssertFalse(WebSocketClient.isOversizedMessage(
            NSError(domain: NSURLErrorDomain, code: Int(EMSGSIZE)), closeCode: .invalid))
    }

    func testDisconnectFailsEveryPendingRequestAndRemovesContinuations() async {
        let store = PendingRequestStore()
        let registered = expectation(description: "Both requests registered")
        registered.expectedFulfillmentCount = 2
        let requests = ["history", "agents"].map { id in
            Task {
                do {
                    _ = try await withCheckedThrowingContinuation { continuation in
                        store.set(id, continuation: continuation)
                        registered.fulfill()
                    }
                    XCTFail("Disconnected requests must fail")
                } catch {
                    guard case FrameError.messageTooLarge = error else {
                        return XCTFail("Expected the receive failure, got \(error)")
                    }
                }
            }
        }
        await fulfillment(of: [registered], timeout: 2)
        store.failAll(with: FrameError.messageTooLarge)
        // A later timeout or duplicate disconnect must not resume them again.
        XCTAssertNil(store.remove("history"))
        XCTAssertNil(store.remove("agents"))
        store.failAll(with: FrameError.notConnected)
        for request in requests { await request.value }
    }
}
