#if os(macOS)
import XCTest

@MainActor
final class AgentNavigationTests: XCTestCase {
    func testSelectingAgentsKeepsSearchFieldsIndependent() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["--ui-test-agent-navigation"]
        app.launch()
        defer { app.terminate() }

        let maya = app.descendants(matching: .any)["agent-row-maya"].firstMatch
        XCTAssertTrue(maya.waitForExistence(timeout: 10))
        maya.click()

        let conversationSearch = app.searchFields["Search loaded conversations"]
        XCTAssertTrue(conversationSearch.waitForExistence(timeout: 5))
        XCTAssertLessThan(conversationSearch.frame.height, 40)
        let planning = app.descendants(matching: .any)["conversation-row-maya-Planning"].firstMatch
        let research = app.descendants(matching: .any)["conversation-row-maya-Research"].firstMatch
        XCTAssertTrue(planning.waitForExistence(timeout: 5))
        XCTAssertTrue(planning.isHittable, "The search field must leave the conversation list visible")
        XCTAssertTrue(research.isHittable)
        conversationSearch.click()
        conversationSearch.typeText("Planning")
        XCTAssertTrue(planning.isHittable)
        XCTAssertFalse(research.exists)

        let atlas = app.descendants(matching: .any)["agent-row-atlas"].firstMatch
        XCTAssertTrue(atlas.exists, "Conversation search must not filter the agent sidebar")
        atlas.click()
        let atlasPlanning = app.descendants(matching: .any)["conversation-row-atlas-Planning"].firstMatch
        XCTAssertTrue(atlasPlanning.waitForExistence(timeout: 5))
        XCTAssertTrue(atlasPlanning.isHittable)
        XCTAssertTrue(conversationSearch.exists)
        XCTAssertEqual(app.toolbars.searchFields.count, 1, "Only agent search belongs in the window toolbar")
        XCTAssertEqual(app.searchFields.count, 2)

        let screenshot = XCTAttachment(screenshot: app.windows.firstMatch.screenshot())
        screenshot.name = "Mac agent selection and independent search fields"
        screenshot.lifetime = .keepAlways
        add(screenshot)
    }
}
#endif
