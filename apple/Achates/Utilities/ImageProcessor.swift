import Foundation
#if os(iOS)
import UIKit
#else
import AppKit
import CoreGraphics
import ImageIO
#endif

enum ImageProcessor {
    static let maxLongEdge: CGFloat = 1600
    static let jpegQuality: CGFloat = 0.8

    /// Normalizes raw image bytes into a JPEG ≤ 1600 px on the long edge,
    /// re-encoded at quality 0.8, plus a thumbnail for display.
    /// Returns nil if the input cannot be decoded as an image.
    static func normalize(_ rawData: Data) -> (data: Data, thumbnail: PlatformImage)? {
        #if os(iOS)
        guard let original = UIImage(data: rawData) else { return nil }
        let oriented = original.fixedOrientation()
        let scaled = oriented.scaledToMaxDimension(maxLongEdge)
        guard let jpeg = scaled.jpegData(compressionQuality: jpegQuality) else { return nil }
        return (jpeg, scaled)
        #else
        guard let source = CGImageSourceCreateWithData(rawData as CFData, nil) else {
            return nil
        }
        let thumbnailOptions: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxLongEdge,
        ]
        guard let thumbnailCG = CGImageSourceCreateThumbnailAtIndex(source, 0, thumbnailOptions as CFDictionary) else {
            return nil
        }

        let outData = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(outData, "public.jpeg" as CFString, 1, nil) else {
            return nil
        }
        let destOptions: [CFString: Any] = [
            kCGImageDestinationLossyCompressionQuality: jpegQuality,
            kCGImageDestinationOptimizeColorForSharing: true,
        ]
        CGImageDestinationAddImage(dest, thumbnailCG, destOptions as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { return nil }

        let nsImage = NSImage(
            cgImage: thumbnailCG,
            size: NSSize(width: thumbnailCG.width, height: thumbnailCG.height)
        )
        return (outData as Data, nsImage)
        #endif
    }
}

#if os(iOS)
private extension UIImage {
    func fixedOrientation() -> UIImage {
        guard imageOrientation != .up else { return self }
        let format = UIGraphicsImageRendererFormat()
        format.scale = scale
        let renderer = UIGraphicsImageRenderer(size: size, format: format)
        return renderer.image { _ in
            draw(in: CGRect(origin: .zero, size: size))
        }
    }

    func scaledToMaxDimension(_ maxDim: CGFloat) -> UIImage {
        let longest = max(size.width, size.height)
        guard longest > maxDim else { return self }
        let factor = maxDim / longest
        let newSize = CGSize(width: size.width * factor, height: size.height * factor)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let renderer = UIGraphicsImageRenderer(size: newSize, format: format)
        return renderer.image { _ in
            draw(in: CGRect(origin: .zero, size: newSize))
        }
    }
}
#endif
