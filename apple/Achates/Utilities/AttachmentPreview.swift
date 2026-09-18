import SwiftUI
import QuickLook
import ImageIO
import UniformTypeIdentifiers

@Observable
@MainActor
final class AttachmentPreview {
    var url: URL?
    var error: String?
    var isLoading = false
    private var directory: URL?

    func show(data: Data, name: String?, mime: String) {
        do {
            clearFiles()
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            directory = folder
            let imageType = CGImageSourceCreateWithData(data as CFData, nil).flatMap { CGImageSourceGetType($0) }.flatMap { UTType($0 as String) }
            let fallback = "Attachment." + ((imageType ?? UTType(mimeType: mime))?.preferredFilenameExtension ?? "dat")
            let filename = name.map { URL(fileURLWithPath: $0).lastPathComponent }.flatMap { $0.isEmpty ? nil : $0 } ?? fallback
            let file = folder.appendingPathComponent(filename)
            try data.write(to: file, options: .atomic)
            url = file
        } catch {
            clearFiles()
            self.error = "Couldn’t preview this attachment. \(error.localizedDescription)"
        }
    }

    func show(remoteURL: URL) async {
        guard !isLoading else { return }
        isLoading = true
        defer { isLoading = false }
        do {
            let (data, response) = try await URLSession.shared.data(from: remoteURL)
            guard let response = response as? HTTPURLResponse, (200...299).contains(response.statusCode) else {
                throw URLError(.badServerResponse)
            }
            show(data: data, name: nil, mime: response.mimeType ?? "image/jpeg")
        } catch {
            self.error = "Couldn’t load this image. Try opening it again. \(error.localizedDescription)"
        }
    }

    func clearFiles() {
        if let directory { try? FileManager.default.removeItem(at: directory) }
        directory = nil
    }
}

extension Image {
    init(platformImage: PlatformImage) {
        #if os(macOS)
        self.init(nsImage: platformImage)
        #else
        self.init(uiImage: platformImage)
        #endif
    }
}
