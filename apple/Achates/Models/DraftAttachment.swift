import Foundation
#if os(iOS)
import UIKit
typealias PlatformImage = UIImage
#else
import AppKit
typealias PlatformImage = NSImage
#endif

struct DraftAttachment: Identifiable, Equatable {
    let id: UUID
    let data: Data            // JPEG bytes ready for the wire
    let thumbnail: PlatformImage

    init(id: UUID = UUID(), data: Data, thumbnail: PlatformImage) {
        self.id = id
        self.data = data
        self.thumbnail = thumbnail
    }

    static func == (lhs: DraftAttachment, rhs: DraftAttachment) -> Bool {
        lhs.id == rhs.id
    }
}
