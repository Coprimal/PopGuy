// ComposerImagePreview.swift
// PopGuy
//
// The Terminal Composer's enlarged image preview: a scrim over the whole composer
// and a centered card with the image (aspect-fit), its file name and a close button.
// Shown by the composer while `ComposerImageStripModel.preview` is set; a click on
// the scrim or the close button closes it. Composer-local on purpose (the Settings
// modal belongs to the Settings UI).
//
// Until the decode arrives the card shows a same-size placeholder whose spinner
// fades in only when the model asks for it (after ~150 ms), so fast loads don't flash.

import AppKit
import SwiftUI

struct ComposerImagePreview: View {
    @ObservedObject var model: ComposerImageStripModel

    @Environment(\.displayScale) private var displayScale

    private static let margin: CGFloat = 32
    private static let cardPadding: CGFloat = 12
    private static let captionHeight: CGFloat = 24
    private static let placeholderSize = CGSize(width: 320, height: 220)

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                Color.black.opacity(0.35)
                    .contentShape(Rectangle())
                    .onTapGesture { model.closePreview() }
                    .accessibilityLabel("Close the preview")
                    .accessibilityAddTraits(.isButton)
                if let state = model.preview {
                    card(state, in: geometry.size)
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
        .onAppear { model.previewDidAppear() }
    }

    private func card(_ state: ComposerImageStripModel.PreviewState, in size: CGSize) -> some View {
        let maxImage = CGSize(
            width: max(0, size.width - 2 * (Self.margin + Self.cardPadding)),
            height: max(0, size.height - 2 * (Self.margin + Self.cardPadding) - Self.captionHeight)
        )
        let shown = contentSize(state, maxSize: maxImage)
        // The caption is as wide as the image (at least a short name's width), so the
        // card hugs the image instead of stretching to the window.
        let captionWidth = min(maxImage.width, max(shown.width, 200))
        return VStack(spacing: 8) {
            content(state, size: shown)
            Text((state.path as NSString).lastPathComponent)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(width: captionWidth, height: Self.captionHeight - 8)
        }
        .padding(Self.cardPadding)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color(nsColor: .windowBackgroundColor))
                .shadow(color: .black.opacity(0.3), radius: 16, y: 6)
        )
        .contentShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(alignment: .topTrailing) { closeButton }
    }

    /// The image's fitted size, or the placeholder's size while it loads or failed.
    private func contentSize(_ state: ComposerImageStripModel.PreviewState, maxSize: CGSize) -> CGSize {
        if let image = state.image {
            return fittedSize(for: image, in: maxSize)
        }
        return CGSize(
            width: min(Self.placeholderSize.width, maxSize.width),
            height: min(Self.placeholderSize.height, maxSize.height)
        )
    }

    @ViewBuilder
    private func content(_ state: ComposerImageStripModel.PreviewState, size: CGSize) -> some View {
        if let image = state.image {
            Image(decorative: image, scale: 1)
                .resizable()
                .interpolation(.high)
                .aspectRatio(contentMode: .fit)
                .frame(width: size.width, height: size.height)
                .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
                .accessibilityLabel("Image preview")
        } else {
            ZStack {
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .fill(.quaternary)
                if state.failed {
                    Text("Can't show this image")
                        .foregroundStyle(.secondary)
                } else {
                    ProgressView()
                        .controlSize(.small)
                        .opacity(state.showsSpinner ? 1 : 0)
                        .animation(.easeIn(duration: 0.15), value: state.showsSpinner)
                }
            }
            .frame(width: size.width, height: size.height)
        }
    }

    private var closeButton: some View {
        Button {
            model.closePreview()
        } label: {
            Image(systemName: "xmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.secondary)
                .frame(width: 22, height: 22)
                .background(Circle().fill(.regularMaterial))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .padding(6)
        .hoverTooltip("Close (Esc)")
        .accessibilityLabel("Close the preview")
    }

    /// The image's size in points (pixels over the display scale), scaled down to fit
    /// `maxSize`; never scaled up.
    private func fittedSize(for image: CGImage, in maxSize: CGSize) -> CGSize {
        let scale = max(displayScale, 1)
        let natural = CGSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        guard natural.width > 0, natural.height > 0 else { return .zero }
        let factor = min(1, maxSize.width / natural.width, maxSize.height / natural.height)
        return CGSize(width: natural.width * factor, height: natural.height * factor)
    }
}
