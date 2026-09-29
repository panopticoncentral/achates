import XCTest
@testable import Achates

final class AgentEditModelTests: XCTestCase {
    func testEffortsAreIndependentAndModelDefaultIsSentExplicitly() throws {
        var model = try XCTUnwrap(AgentEditModel.from([
            "reasoning_effort": .string("low"),
            "thinking_reasoning_effort": .string("high"),
        ]))
        XCTAssertEqual(model.reasoningEffort, "low")
        XCTAssertEqual(model.thinkingReasoningEffort, "high")
        model.thinkingReasoningEffort = "default"
        var params = model.toParams(agentId: "test")
        XCTAssertEqual(params["reasoning_effort"]?.stringValue, "low")
        XCTAssertEqual(params["thinking_reasoning_effort"]?.stringValue, "default")
        model.reasoningEffort = "default"
        model.thinkingReasoningEffort = "high"
        params = model.toParams(agentId: "test")
        XCTAssertEqual(params["reasoning_effort"]?.stringValue, "default")
        XCTAssertEqual(params["thinking_reasoning_effort"]?.stringValue, "high")
    }

    func testOlderServerPayloadLeavesThinkingAtModelDefault() throws {
        let model = try XCTUnwrap(AgentEditModel.from(["reasoning_effort": .string("medium")]))
        XCTAssertNil(model.thinkingReasoningEffort)
        let params = model.toParams(agentId: "test")
        XCTAssertEqual(params["reasoning_effort"]?.stringValue, "medium")
        XCTAssertEqual(params["thinking_reasoning_effort"]?.stringValue, "default")
    }
}
