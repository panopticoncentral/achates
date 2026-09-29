#if os(iOS)
import XCTest

@MainActor
final class ComposerKeyboardUITests: XCTestCase {
    func testReturnSubmitsAndKeepsComposerReady() {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["--ui-test-composer"]
        app.launch()
        defer { app.terminate() }

        let composer = app.textViews["Message"]
        XCTAssertTrue(composer.waitForExistence(timeout: 5))
        composer.tap()
        for message in ["first", "second"] {
            composer.typeText(message)
            // This simulator requires a line feed to inject Return. Its
            // carriage-return constant is ignored by native text views too.
            // Modifier delivery is not reliable with this injection; Shift+
            // Return is covered at the native command level in the unit tests.
            composer.typeKey("\n", modifierFlags: [])
            let submitted = app.staticTexts["submitted-message"]
            XCTAssertTrue(submitted.waitForExistence(timeout: 5))
            XCTAssertEqual(submitted.label, message)
            XCTAssertEqual(composer.value as? String, "")
        }
    }
}
#endif
