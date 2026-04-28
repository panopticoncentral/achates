#if os(iOS)
import SwiftUI
import UIKit

/// Presents the system camera and delivers the captured photo as JPEG `Data`.
///
/// The parent is responsible for dismissing the presentation (typically by flipping
/// the `.fullScreenCover` `isPresented` binding) inside both `onCapture` and `onCancel` —
/// `UIImagePickerController` does not auto-dismiss.
struct CameraPicker: UIViewControllerRepresentable {
    let onCapture: (Data) -> Void
    let onCancel: () -> Void

    func makeUIViewController(context: Context) -> UIImagePickerController {
        let picker = UIImagePickerController()
        picker.sourceType = .camera
        picker.delegate = context.coordinator
        picker.allowsEditing = false
        return picker
    }

    func updateUIViewController(_ uiViewController: UIImagePickerController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    final class Coordinator: NSObject, UIImagePickerControllerDelegate, UINavigationControllerDelegate {
        let parent: CameraPicker
        init(_ parent: CameraPicker) { self.parent = parent }

        func imagePickerController(_ picker: UIImagePickerController, didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]) {
            if let image = info[.originalImage] as? UIImage,
               let data = image.jpegData(compressionQuality: 1.0) {
                parent.onCapture(data)
            } else {
                parent.onCancel()
            }
        }

        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) {
            parent.onCancel()
        }
    }
}

extension CameraPicker {
    /// `true` when the device has a camera available. False on most simulators and
    /// some iPad configurations. Callers should hide their camera entry point when
    /// this is `false`.
    static var isAvailable: Bool {
        UIImagePickerController.isSourceTypeAvailable(.camera)
    }
}
#endif
