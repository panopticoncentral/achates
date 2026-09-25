import Foundation
import UniformTypeIdentifiers
#if os(iOS)
import UIKit
typealias PlatformImage = UIImage
#else
import AppKit
typealias PlatformImage = NSImage
#endif

struct DraftAttachment: Identifiable, Equatable {
    static let workbookMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    static let workbookType = UTType(filenameExtension: "xlsx") ?? UTType(importedAs: "org.openxmlformats.spreadsheetml.sheet")
    static let maxWorkbookBytes = 8 * 1024 * 1024
    static let documentTypes: [UTType] = [.pdf, .text, workbookType]
    let id: UUID
    let data: Data              // original document bytes; JPEG for normalized images
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

    static func documentLabel(for mime: String) -> String {
        switch mime {
        case workbookMime: return "Excel workbook"
        case "application/pdf": return "PDF"
        default: return "Text"
        }
    }

    static func documentSymbol(for mime: String) -> String {
        switch mime {
        case workbookMime: return "tablecells"
        case "application/pdf": return "doc.richtext"
        default: return "doc.text"
        }
    }

    static func == (lhs: DraftAttachment, rhs: DraftAttachment) -> Bool {
        lhs.id == rhs.id
    }
}
