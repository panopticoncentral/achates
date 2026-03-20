import AVFoundation
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

struct PhotoResult: Sendable {
    let base64: String
    let width: Int
    let height: Int
}

enum CameraFacing: Sendable {
    case front
    case back
}

enum CameraError: Error, LocalizedError {
    case notAuthorized
    case captureDeviceUnavailable
    case captureFailed(String)
    case compressionFailed
    case notAvailable

    var errorDescription: String? {
        switch self {
        case .notAuthorized: return "Camera access not authorized"
        case .captureDeviceUnavailable: return "Camera device unavailable"
        case .captureFailed(let msg): return msg
        case .compressionFailed: return "Failed to compress photo"
        case .notAvailable: return "Camera capture is not available on this platform"
        }
    }
}

final class CameraService: Sendable {
    func capturePhoto(facing: CameraFacing) async throws -> PhotoResult {
        #if os(iOS)
        return try await capturePhotoiOS(facing: facing)
        #else
        throw CameraError.notAvailable
        #endif
    }

    #if os(iOS)
    private func capturePhotoiOS(facing: CameraFacing) async throws -> PhotoResult {
        let status = AVCaptureDevice.authorizationStatus(for: .video)
        if status == .notDetermined {
            let granted = await AVCaptureDevice.requestAccess(for: .video)
            if !granted { throw CameraError.notAuthorized }
        } else if status != .authorized {
            throw CameraError.notAuthorized
        }

        let position: AVCaptureDevice.Position = facing == .front ? .front : .back
        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: position) else {
            throw CameraError.captureDeviceUnavailable
        }

        let session = AVCaptureSession()
        session.sessionPreset = .photo

        let input = try AVCaptureDeviceInput(device: device)
        guard session.canAddInput(input) else {
            throw CameraError.captureFailed("Cannot add camera input")
        }
        session.addInput(input)

        let output = AVCapturePhotoOutput()
        guard session.canAddOutput(output) else {
            throw CameraError.captureFailed("Cannot add photo output")
        }
        session.addOutput(output)

        session.startRunning()
        defer { session.stopRunning() }

        let photoData = try await captureWithDelegate(output: output)

        guard let image = UIImage(data: photoData) else {
            throw CameraError.captureFailed("Cannot create image from photo data")
        }

        let compressed = try compressImage(image, maxBytes: 500_000)

        return PhotoResult(
            base64: compressed.base64EncodedString(),
            width: Int(image.size.width),
            height: Int(image.size.height)
        )
    }

    private func captureWithDelegate(output: AVCapturePhotoOutput) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            let delegate = PhotoCaptureDelegate(continuation: continuation)
            let settings = AVCapturePhotoSettings()
            output.capturePhoto(with: settings, delegate: delegate)
            // delegate is retained by the capture session until processing completes
            withExtendedLifetime(delegate) {}
        }
    }

    private func compressImage(_ image: UIImage, maxBytes: Int) throws -> Data {
        var quality: CGFloat = 0.8
        while quality > 0.1 {
            if let data = image.jpegData(compressionQuality: quality) {
                if data.count <= maxBytes {
                    return data
                }
            }
            quality -= 0.1
        }

        // Final attempt with lowest quality
        if let data = image.jpegData(compressionQuality: 0.1) {
            return data
        }

        throw CameraError.compressionFailed
    }
    #endif
}

#if os(iOS)
private final class PhotoCaptureDelegate: NSObject, AVCapturePhotoCaptureDelegate, @unchecked Sendable {
    private let continuation: CheckedContinuation<Data, Error>
    private var didResume = false

    init(continuation: CheckedContinuation<Data, Error>) {
        self.continuation = continuation
    }

    func photoOutput(_ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: Error?) {
        guard !didResume else { return }
        didResume = true

        if let error {
            continuation.resume(throwing: CameraError.captureFailed(error.localizedDescription))
            return
        }

        guard let data = photo.fileDataRepresentation() else {
            continuation.resume(throwing: CameraError.captureFailed("No photo data"))
            return
        }

        continuation.resume(returning: data)
    }
}
#endif
