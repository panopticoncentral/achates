import Foundation

struct MemoryInfo: Identifiable, Sendable, Hashable {
    var id: String { scope }
    let scope: String      // "shared" or agent name
    let size: Int
    let updated: Date

    var displayName: String {
        scope == "shared" ? "Shared" : scope
    }

    var isShared: Bool { scope == "shared" }

    static func fromList(_ payload: [String: JSONValue]) -> [MemoryInfo] {
        (payload["memories"]?.arrayValue ?? []).compactMap { item in
            guard let obj = item.objectValue,
                  let scope = obj["scope"]?.stringValue else { return nil }
            let size = obj["size"]?.intValue ?? 0
            let updatedMs = obj["updated"]?.intValue ?? 0
            return MemoryInfo(
                scope: scope,
                size: size,
                updated: Date(timeIntervalSince1970: Double(updatedMs) / 1000)
            )
        }
    }
}
