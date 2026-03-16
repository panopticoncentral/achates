import CoreLocation
import Foundation

struct LocationResult: Sendable {
    let latitude: Double
    let longitude: Double
    let accuracy: Double
    let timestamp: Date
}

enum LocationError: Error, LocalizedError {
    case denied
    case failed(String)
    case timeout

    var errorDescription: String? {
        switch self {
        case .denied: return "Location access denied"
        case .failed(let msg): return msg
        case .timeout: return "Location request timed out"
        }
    }
}

final class LocationService: NSObject, Sendable {
    private let manager: CLLocationManager
    private let delegateHandler: LocationDelegate

    override init() {
        self.manager = CLLocationManager()
        self.delegateHandler = LocationDelegate()
        super.init()
        manager.delegate = delegateHandler
        manager.desiredAccuracy = kCLLocationAccuracyBest
    }

    func requestLocation() async throws -> LocationResult {
        let status = manager.authorizationStatus
        if status == .notDetermined {
            manager.requestWhenInUseAuthorization()
            // Wait briefly for authorization
            try await Task.sleep(for: .seconds(1))
        }

        let updatedStatus = manager.authorizationStatus
        guard updatedStatus == .authorizedWhenInUse || updatedStatus == .authorizedAlways else {
            throw LocationError.denied
        }

        return try await withCheckedThrowingContinuation { continuation in
            delegateHandler.setContinuation(continuation)
            manager.requestLocation()
        }
    }
}

private final class LocationDelegate: NSObject, CLLocationManagerDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<LocationResult, Error>?

    func setContinuation(_ cont: CheckedContinuation<LocationResult, Error>) {
        lock.lock()
        continuation = cont
        lock.unlock()
    }

    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        lock.lock()
        let cont = continuation
        continuation = nil
        lock.unlock()

        guard let location = locations.last else {
            cont?.resume(throwing: LocationError.failed("No location data"))
            return
        }

        cont?.resume(returning: LocationResult(
            latitude: location.coordinate.latitude,
            longitude: location.coordinate.longitude,
            accuracy: location.horizontalAccuracy,
            timestamp: location.timestamp
        ))
    }

    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        lock.lock()
        let cont = continuation
        continuation = nil
        lock.unlock()

        cont?.resume(throwing: LocationError.failed(error.localizedDescription))
    }
}
