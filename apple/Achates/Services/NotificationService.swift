import UserNotifications
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

@MainActor
final class NotificationService {
    static let shared = NotificationService()

    private init() {}

    func requestPermission() async -> Bool {
        do {
            let granted = try await UNUserNotificationCenter.current()
                .requestAuthorization(options: [.alert, .badge, .sound])
            if granted {
                registerForRemoteNotifications()
            }
            return granted
        } catch {
            print("Notification permission error: \(error)")
            return false
        }
    }

    func registerForRemoteNotifications() {
        #if os(iOS)
        UIApplication.shared.registerForRemoteNotifications()
        #elseif os(macOS)
        NSApplication.shared.registerForRemoteNotifications()
        #endif
    }

    func handleDeviceToken(_ token: Data) -> String {
        let tokenString = token.map { String(format: "%02.2hhx", $0) }.joined()
        UserDefaults.standard.set(tokenString, forKey: "achates_apns_token")
        return tokenString
    }

    var storedToken: String? {
        UserDefaults.standard.string(forKey: "achates_apns_token")
    }
}
