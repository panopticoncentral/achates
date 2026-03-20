import Foundation

final class DeviceCommandRouter: Sendable {
    private let locationService = LocationService()
    private let cameraService = CameraService()

    func handle(method: String, params: [String: JSONValue]) async -> Result<[String: JSONValue]?, Error> {
        switch method {
        case "device.location":
            return await handleLocation()
        case "device.camera":
            let facing = params["facing"]?.stringValue ?? "back"
            return await handleCamera(facing: facing)
        default:
            return .failure(FrameError.serverError("Unknown device method: \(method)"))
        }
    }

    private func handleLocation() async -> Result<[String: JSONValue]?, Error> {
        do {
            let location = try await locationService.requestLocation()
            return .success([
                "latitude": .double(location.latitude),
                "longitude": .double(location.longitude),
                "accuracy": .double(location.accuracy),
                "timestamp": .string(ISO8601DateFormatter().string(from: location.timestamp))
            ])
        } catch {
            return .failure(error)
        }
    }

    private func handleCamera(facing: String) async -> Result<[String: JSONValue]?, Error> {
        do {
            let photo = try await cameraService.capturePhoto(facing: facing == "front" ? .front : .back)
            return .success([
                "image": .string(photo.base64),
                "width": .int(photo.width),
                "height": .int(photo.height)
            ])
        } catch {
            return .failure(error)
        }
    }
}
