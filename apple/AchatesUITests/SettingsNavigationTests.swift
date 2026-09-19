#if os(iOS)
import XCTest
import UIKit

@MainActor
final class SettingsNavigationTests: XCTestCase {
    func testSettingsCanBeDismissedAndReopened() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["--ui-test-agent-navigation"]
        defer {
            app.terminate()
            XCUIDevice.shared.orientation = .portrait
        }

        let isPad = UIDevice.current.userInterfaceIdiom == .pad
        for orientation in [UIDeviceOrientation.landscapeLeft, .portrait] {
            XCUIDevice.shared.orientation = orientation
            app.launch()
            let settings = app.buttons["Settings"].firstMatch
            let sidebar = app.buttons["Show Sidebar"].firstMatch
            if sidebar.exists { sidebar.tap() }
            XCTAssertTrue(settings.waitForExistence(timeout: 5))
            settings.tap()

            let serverURL = app.textFields["http://192.168.1.100:5000"]
            XCTAssertTrue(serverURL.waitForExistence(timeout: 5))
            let close = isPad ? app.buttons["Done"] : app.navigationBars.buttons["Agents"]
            XCTAssertTrue(close.waitForExistence(timeout: 5))
            XCTAssertTrue(close.isHittable)

            let screenshot = XCTAttachment(screenshot: app.screenshot())
            screenshot.name = "Settings dismissal — \(isPad ? "iPad" : "iPhone") — \(orientation.rawValue)"
            screenshot.lifetime = .keepAlways
            add(screenshot)

            close.tap()
            XCTAssertTrue(serverURL.waitForNonExistence(timeout: 5))
            XCTAssertTrue(settings.isHittable, "Closing Settings must return to usable agent navigation")
            settings.tap()
            XCTAssertTrue(close.waitForExistence(timeout: 5))
            close.tap()
            XCTAssertTrue(serverURL.waitForNonExistence(timeout: 5))
            app.terminate()
        }
    }
}
#endif
