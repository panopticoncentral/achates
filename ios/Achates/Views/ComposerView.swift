import SwiftUI

struct ComposerView: View {
    @Environment(AppState.self) private var appState
    @Bindable var speechService: SpeechService
    @State private var text = ""
    @FocusState private var isFocused: Bool

    let onSend: (String) -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            if speechService.isRecording {
                HStack {
                    Image(systemName: "waveform")
                        .foregroundStyle(.red)
                        .symbolEffect(.variableColor)
                    Text(speechService.transcript.isEmpty ? "Listening..." : speechService.transcript)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                    Spacer()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color(.systemGray6))
            }

            HStack(alignment: .bottom, spacing: 8) {
                TextField("Message", text: $text, axis: .vertical)
                    .textFieldStyle(.plain)
                    .lineLimit(1...6)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .background(
                        RoundedRectangle(cornerRadius: 20, style: .continuous)
                            .fill(Color(.systemGray6))
                    )
                    .focused($isFocused)

                if appState.isStreaming {
                    Button(action: onCancel) {
                        Image(systemName: "stop.circle.fill")
                            .font(.system(size: 32))
                            .foregroundStyle(.red)
                    }
                } else if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    Button(action: toggleRecording) {
                        Image(systemName: speechService.isRecording ? "mic.fill" : "mic")
                            .font(.system(size: 20))
                            .foregroundStyle(speechService.isRecording ? .red : .blue)
                            .frame(width: 36, height: 36)
                            .background(
                                Circle()
                                    .fill(speechService.isRecording ? Color.red.opacity(0.15) : Color(.systemGray6))
                            )
                    }
                } else {
                    Button(action: send) {
                        Image(systemName: "arrow.up.circle.fill")
                            .font(.system(size: 32))
                            .foregroundStyle(.blue)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
        .background(.bar)
    }

    private func send() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        onSend(trimmed)
        text = ""
    }

    private func toggleRecording() {
        if speechService.isRecording {
            let transcript = speechService.stopRecording()
            if !transcript.isEmpty {
                text = transcript
            }
        } else {
            Task {
                do {
                    try await speechService.startRecording()
                } catch {
                    print("Failed to start recording: \(error)")
                }
            }
        }
    }
}
