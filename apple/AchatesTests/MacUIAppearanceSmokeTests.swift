#if os(macOS)
import XCTest
import SwiftUI
import AppKit
@testable import Achates

@MainActor
final class MacUIAppearanceSmokeTests: XCTestCase {
    func testConversationAndSettingsAppearances() async throws {
        for dark in [false, true] {
            let state = AppState()
            state.serverURL = nil
            let agent = Agent(id: "maya", name: "maya", displayName: "Maya", description: "Assistant", tools: [], lastMessage: nil, lastActivity: nil, unreadCount: 0, avatarData: nil)
            state.agents = [agent]
            state.currentAgent = agent
            state.currentSessionId = "preview"
            state.connectionStatus = .connected
            state.sessions = [SessionInfo(id: "preview", title: "A calmer week", preview: nil, created: Date(), updated: Date())]
            state.messages = [
                ChatMessage(role: .user, blocks: [.text(id: "u", "Help me plan a calmer week.")]),
                ChatMessage(role: .assistant, blocks: [
                    .toolCall(id: "completed-tool", name: "cron", status: .completed, result: nil),
                    .thinking(id: "reasoning", text: "Considering a balanced schedule.", collapsed: true),
                    .toolCall(id: "running-tool", name: "calendar", status: .running, result: nil),
                    .text(id: "a", "## Start with what matters\nChoose **three priorities** and leave room between them.\n\n- Protect one hour for focused work.\n- Take a short walk at lunch.\n- Keep Friday afternoon open.\n\nWhat would you like to make time for?")])
            ]
            try await capture("mac-conversation-\(dark ? "dark" : "light")", size: NSSize(width: 1000, height: 700), view:
                NavigationStack { ChatView(agent: agent) }.environment(state)
                    .background(dark ? Color.black : Color.white)
                    .preferredColorScheme(dark ? .dark : .light))
        }
        let state = AppState()
        state.serverURL = nil
        try await capture("mac-settings", size: NSSize(width: 480, height: 320), view: MacSettingsView().environment(state))
    }

    private func capture<V: View>(_ name: String, size: NSSize, view: V) async throws {
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: view)
        window.contentView = host
        window.setContentSize(size)
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        try await Task.sleep(for: .milliseconds(350))
        host.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        let data = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        let attachment = XCTAttachment(data: data, uniformTypeIdentifier: "public.png")
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
#endif
