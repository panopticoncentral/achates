import XCTest
@testable import Achates

final class SessionInfoParsingTests: XCTestCase {
    func testParsesUnreadCount() {
        let dict: [String: JSONValue] = [
            "id": .string("s1"),
            "title": .string("Hello"),
            "updated": .int(1_700_000_000_000),
            "created": .int(1_700_000_000_000),
            "unread": .int(3),
        ]
        let info = SessionInfo.from(dict: dict)
        XCTAssertEqual(info?.unread, 3)
    }

    func testUnreadDefaultsToZeroWhenAbsent() {
        let dict: [String: JSONValue] = [
            "id": .string("s1"),
            "updated": .int(1_700_000_000_000),
            "created": .int(1_700_000_000_000),
        ]
        let info = SessionInfo.from(dict: dict)
        XCTAssertEqual(info?.unread, 0)
    }
}
