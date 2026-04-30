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
    let data: Data              // raw bytes (JPEG for images, PDF for docs)
    let mime: String            // e.g. "image/jpeg", "application/pdf"
    let displayName: String?    // shown on the composer chip for non-images
    let thumbnail: PlatformImage?

    init(
        id: UUID = UUID(),
        data: Data,
        mime: String = "image/jpeg",
        displayName: String? = nil,
        thumbnail: PlatformImage? = nil
    ) {
        self.id = id
        self.data = data
        self.mime = mime
        self.displayName = displayName
        self.thumbnail = thumbnail
    }

    var isImage: Bool { mime.hasPrefix("image/") }

    static func == (lhs: DraftAttachment, rhs: DraftAttachment) -> Bool {
        lhs.id == rhs.id
    }
}
