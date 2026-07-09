import XCTest
@testable import Achates

/// Pins that persistence/destructive RPCs FAIL LOUDLY when there is no client
/// (disconnected), instead of silently "succeeding" via optional chaining.
/// Before the guards, a disconnected Save greyed out as if persisted and a
/// disconnected job delete dismissed as if deleted.
@MainActor
final class DisconnectedRPCGuardTests: XCTestCase {
    func testSaveMemoryThrowsWhenDisconnected() async {
        let state = AppState()
        do {
            try await state.saveMemory(scope: "agent:maya", content: "hello")
            XCTFail("saveMemory should throw when no client is connected")
        } catch {
            // expected
        }
    }

    func testSetJobEnabledThrowsWhenDisconnected() async {
        let state = AppState()
        do {
            try await state.setJobEnabled(agent: "maya", jobId: "job1", enabled: false)
            XCTFail("setJobEnabled should throw when no client is connected")
        } catch {}
    }

    func testDeleteJobThrowsWhenDisconnected() async {
        let state = AppState()
        do {
            try await state.deleteJob(agent: "maya", jobId: "job1")
            XCTFail("deleteJob should throw when no client is connected")
        } catch {}
    }

    func testRunJobThrowsWhenDisconnected() async {
        let state = AppState()
        do {
            try await state.runJob(agent: "maya", jobId: "job1")
            XCTFail("runJob should throw when no client is connected")
        } catch {}
    }

    func testLoadMemoryThrowsWhenDisconnected() async {
        let state = AppState()
        do {
            _ = try await state.loadMemory(scope: "shared")
            XCTFail("loadMemory should throw when no client is connected")
        } catch {}
    }
}
