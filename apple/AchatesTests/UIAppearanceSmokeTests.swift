#if os(iOS)
import XCTest
import SwiftUI
@testable import Achates

/// Captures real SwiftUI surfaces with isolated in-memory data for visual review.
@MainActor
final class UIAppearanceSmokeTests: XCTestCase {
    func testConversationAppearances() async throws {
        for dark in [false, true] {
            let state = fixture()
            try await capture("conversation-\(dark ? "dark" : "light")", view:
                NavigationStack { ChatView(agent: state.agents[0]) }
                    .environment(state)
                    .preferredColorScheme(dark ? .dark : .light))
        }
    }

    func testLargeTextAndWelcomeLayout() async throws {
        let state = fixture()
        try await capture("agents-large-text", view:
            NavigationStack { AgentListView() }
                .environment(state)
                .dynamicTypeSize(.accessibility3))
        try await capture("welcome", view:
            NavigationStack { SettingsView(isOnboarding: true) }
                .environment(AppState()))
    }

    private func fixture() -> AppState {
        let state = AppState()
        let agent = Agent(id: "maya", name: "maya", displayName: "Maya", description: "A thoughtful assistant", tools: [], lastMessage: "Let’s make a plan for the week.", lastActivity: Date(), unreadCount: 2, avatarData: nil)
        state.agents = [agent]
        state.currentAgent = agent
        state.currentSessionId = "preview"
        state.connectionStatus = .connected
        state.sessions = [SessionInfo(id: "preview", title: "A calmer week", preview: "Let’s make a plan", created: Date(), updated: Date())]
        state.messages = [
            ChatMessage(role: .user, blocks: [.text(id: "u", "Help me plan a calmer week.")]),
            ChatMessage(role: .assistant, blocks: [
                    .toolCall(id: "completed-tool", name: "cron", status: .completed, result: nil),
                    .thinking(id: "reasoning", text: "Considering a balanced schedule.", collapsed: true),
                    .toolCall(id: "running-tool", name: "calendar", status: .running, result: nil),
                    .text(id: "a", "## Start with what matters\nChoose **three priorities** and leave room between them.\n\n- Protect one hour for focused work.\n- Take a short walk at lunch.\n- Keep Friday afternoon open.\n\nWhat would you like to make time for?")])
        ]
        return state
    }

    private func capture<V: View>(_ name: String, view: V) async throws {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let window = UIWindow(windowScene: scene)
        let host = UIHostingController(rootView: view)
        window.rootViewController = host
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await Task.sleep(for: .milliseconds(350))
        host.view.layoutIfNeeded()
        XCTAssertGreaterThan(host.view.bounds.width, 0)
        let image = UIGraphicsImageRenderer(bounds: host.view.bounds).image { _ in
            host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
#endif
