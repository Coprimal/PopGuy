// CommandSuggestionList.swift
// PopGuy — TerminalComposer
//
// The composer's command suggestion popup: a SwiftUI list of the calling agent's commands
// (CommandSuggestions ranks them), hosted in a non-focusable view that floats over the
// editor's scroll view, anchored to the trigger token at the caret.
//
// CommandSuggestionPopup re-evaluates on every text and selection change of its text view,
// shows the list below the caret line (above it when there is more room there), follows
// scrolling and resizing, and applies an accepted item as one edit of the text view.
// Catalog text is third-party content: rendered verbatim, never as Markdown.
//
// Isolation: MainActor (AppKit/SwiftUI).

import AppKit
import Combine
import SwiftUI

/// Items and keyboard selection shown by `CommandSuggestionList`.
final class CommandSuggestionListState: ObservableObject {
    @Published var items: [ComposerCatalogItem] = []
    @Published var selectedIndex = 0
    /// Bumped when a new list is shown, to scroll back to the selection.
    @Published var generation = 0
}

struct CommandSuggestionList: View {
    @ObservedObject var state: CommandSuggestionListState
    let onAccept: (Int) -> Void

    static let rowHeight: CGFloat = 40
    static let maxVisibleRows = 6
    static let padding: CGFloat = 4
    private static let cornerRadius: CGFloat = 8

    /// Height that shows `count` rows, up to `maxVisibleRows` (the list scrolls beyond that).
    static func height(forRows count: Int) -> CGFloat {
        CGFloat(min(count, maxVisibleRows)) * rowHeight + 2 * padding
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(spacing: 0) {
                    ForEach(Array(state.items.enumerated()), id: \.offset) { index, item in
                        row(item, isSelected: index == state.selectedIndex)
                            .id(index)
                            .onTapGesture { onAccept(index) }
                    }
                }
                .padding(Self.padding)
            }
            .onChange(of: state.selectedIndex) { index in proxy.scrollTo(index) }
            .onChange(of: state.generation) { _ in proxy.scrollTo(state.selectedIndex) }
        }
        .background(shape.fill(Color(nsColor: .controlBackgroundColor)))
        .clipShape(shape)
        .overlay(shape.strokeBorder(Color(nsColor: .separatorColor)))
    }

    private func row(_ item: ComposerCatalogItem, isSelected: Bool) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(verbatim: item.trigger + item.name)
                .font(.body)
                .lineLimit(1)
                .truncationMode(.tail)
            if !item.description.isEmpty {
                Text(verbatim: item.description)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.tail)
            }
        }
        .padding(.horizontal, 8)
        .frame(maxWidth: .infinity, minHeight: Self.rowHeight, maxHeight: Self.rowHeight, alignment: .leading)
        .background {
            if isSelected {
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(Color.accentColor.opacity(0.25))
            }
        }
        .contentShape(Rectangle())
    }
}

/// Never takes the keyboard focus: the text view keeps it while a row is clicked.
private final class SuggestionHostingView: NSHostingView<CommandSuggestionList> {
    override var acceptsFirstResponder: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Suggestion controller of one composer text view.
final class CommandSuggestionPopup: NSObject {
    private weak var textView: NSTextView?
    private let state = CommandSuggestionListState()
    private var token: CommandSuggestions.Token?
    private var hostingView: SuggestionHostingView?
    /// Inside a SwiftUI update: re-evaluate after it, so the list is not published during it.
    private var isRefreshDeferred = false

    private static let width: CGFloat = 340
    /// Space between the caret line and the list.
    private static let gap: CGFloat = 2
    /// Minimum space between the list and the edges of the visible text.
    private static let margin: CGFloat = 4

    /// The agent's commands; nil hides the list. A new catalog re-evaluates the token at the caret.
    var catalog: ComposerSuggestionCatalog? {
        didSet {
            guard catalog != oldValue else { return }
            guard catalog != nil else {
                hide()
                return
            }
            // Set from a SwiftUI update: publish the list after it.
            DispatchQueue.main.async { [weak self] in self?.refresh() }
        }
    }

    init(textView: NSTextView) {
        self.textView = textView
        super.init()
    }

    /// The list is on screen: the arrow keys, Return, Tab and Esc act on it.
    var isVisible: Bool {
        token != nil && hostingView?.isHidden == false
    }

    /// Runs `body` (text view changes made from a SwiftUI update): hides the list now and
    /// re-evaluates once after the update.
    func deferringRefresh(_ body: () -> Void) {
        hide()
        isRefreshDeferred = true
        body()
        isRefreshDeferred = false
        DispatchQueue.main.async { [weak self] in self?.refresh() }
    }

    /// Re-evaluates the token at the caret: shows the matching items, or hides the list.
    func refresh() {
        guard !isRefreshDeferred else { return }
        guard let textView, let catalog, !textView.hasMarkedText(),
              textView.window?.firstResponder === textView,
              textView.selectedRanges.count == 1, textView.selectedRange().length == 0 else {
            hide()
            return
        }
        let rule = CommandSuggestions.triggers(forAgent: catalog.agent)
        guard let newToken = CommandSuggestions.activeToken(
            in: textView.string, caret: textView.selectedRange().location,
            triggers: rule.triggers, slashOnlyAtStart: rule.slashOnlyAtStart) else {
            hide()
            return
        }
        let items = CommandSuggestions.rank(catalog.items, trigger: newToken.trigger, query: newToken.query)
        guard !items.isEmpty else {
            hide()
            return
        }
        if newToken != token || items != state.items {
            state.items = items
            state.selectedIndex = 0
            state.generation += 1
        }
        token = newToken
        guard installHostingView() != nil else {
            hide()
            return
        }
        reposition()
    }

    func hide() {
        token = nil
        hostingView?.isHidden = true
    }

    /// Moves the keyboard selection by `delta` rows, stopping at the first and last row.
    func moveSelection(by delta: Int) {
        guard isVisible, !state.items.isEmpty else { return }
        let index = min(max(state.selectedIndex + delta, 0), state.items.count - 1)
        if index != state.selectedIndex { state.selectedIndex = index }
    }

    /// Accepts the selected row; false when the list is not shown.
    func acceptSelected() -> Bool {
        guard isVisible else { return false }
        accept(at: state.selectedIndex)
        return true
    }

    /// A left mouse-down on the list: the text view keeps the focus for the click.
    func isMouseDownInList(_ event: NSEvent?) -> Bool {
        guard let event, event.type == .leftMouseDown, isVisible, let hostingView,
              event.window === hostingView.window else { return false }
        let point = hostingView.convert(event.locationInWindow, from: nil)
        return hostingView.bounds.contains(point)
    }

    /// Replaces the token with the item's `<trigger><name> ` as its own undo step.
    private func accept(at index: Int) {
        guard let textView, let token, state.items.indices.contains(index), let catalog else { return }
        // Only the token still at the caret (the text may have changed without a refresh).
        let rule = CommandSuggestions.triggers(forAgent: catalog.agent)
        guard textView.selectedRanges.count == 1, textView.selectedRange().length == 0,
              CommandSuggestions.activeToken(
                in: textView.string, caret: textView.selectedRange().location,
                triggers: rule.triggers, slashOnlyAtStart: rule.slashOnlyAtStart) == token else {
            hide()
            return
        }
        let edit = CommandSuggestions.acceptEdit(for: token, item: state.items[index])
        hide()
        if textView.window?.firstResponder !== textView {
            textView.window?.makeFirstResponder(textView)
        }
        textView.breakUndoCoalescing()
        textView.insertText(edit.text, replacementRange: edit.range)
        textView.breakUndoCoalescing()
    }

    // MARK: Hosting and placement

    /// The list's view, added once over the text view's scroll view.
    private func installHostingView() -> SuggestionHostingView? {
        if let hostingView { return hostingView }
        guard let scrollView = textView?.enclosingScrollView else { return nil }
        let view = SuggestionHostingView(rootView: CommandSuggestionList(state: state) { [weak self] index in
            self?.accept(at: index)
        })
        view.sizingOptions = []
        view.isHidden = true
        scrollView.addSubview(view, positioned: .above, relativeTo: nil)

        let clipView = scrollView.contentView
        clipView.postsBoundsChangedNotifications = true
        clipView.postsFrameChangedNotifications = true
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(viewportDidChange(_:)),
                           name: NSView.boundsDidChangeNotification, object: clipView)
        center.addObserver(self, selector: #selector(viewportDidChange(_:)),
                           name: NSView.frameDidChangeNotification, object: clipView)
        hostingView = view
        return view
    }

    /// Scrolling or resizing: follow the caret (a hidden list stays hidden).
    @objc private func viewportDidChange(_ notification: Notification) {
        guard token != nil else { return }
        reposition()
    }

    /// Places the list below the caret line, or above it when there is more room there,
    /// inside the visible text. Hidden while the caret line is scrolled out of view.
    private func reposition() {
        guard let textView, let token, let hostingView, let scrollView = hostingView.superview,
              let anchor = anchorRect(of: token, in: textView) else {
            hostingView?.isHidden = true
            return
        }
        let visible = textView.visibleRect
        guard anchor.maxY > visible.minY, anchor.minY < visible.maxY else {
            hostingView.isHidden = true
            return
        }

        let width = max(min(Self.width, visible.width - 2 * Self.margin), 0)
        let fullHeight = CommandSuggestionList.height(forRows: state.items.count)
        let roomBelow = visible.maxY - Self.margin - (anchor.maxY + Self.gap)
        let roomAbove = (anchor.minY - Self.gap) - (visible.minY + Self.margin)
        let placeBelow = roomBelow >= fullHeight || roomBelow >= roomAbove
        let height = max(min(fullHeight, placeBelow ? roomBelow : roomAbove), 0)
        let y = placeBelow ? anchor.maxY + Self.gap : anchor.minY - Self.gap - height
        let x = max(min(anchor.minX, visible.maxX - Self.margin - width), visible.minX + Self.margin)

        // Text view coordinates are flipped; the conversion handles the scroll view's.
        let frame = NSRect(x: x, y: y, width: width, height: height)
        hostingView.frame = scrollView.convert(frame, from: textView).integral
        hostingView.isHidden = height < CommandSuggestionList.rowHeight / 2
    }

    /// The caret line, starting at the trigger, in the text view's coordinates.
    private func anchorRect(of token: CommandSuggestions.Token, in textView: NSTextView) -> NSRect? {
        guard let layoutManager = textView.layoutManager, let container = textView.textContainer,
              token.range.length > 0,
              NSMaxRange(token.range) <= (textView.string as NSString).length else { return nil }
        func charRect(_ location: Int) -> NSRect {
            let glyphs = layoutManager.glyphRange(
                forCharacterRange: NSRange(location: location, length: 1), actualCharacterRange: nil)
            return layoutManager.boundingRect(forGlyphRange: glyphs, in: container)
        }
        let trigger = charRect(token.range.location)
        let last = charRect(NSMaxRange(token.range) - 1)
        // A token wrapped over lines: anchor to the caret's line.
        let x = abs(trigger.midY - last.midY) < 1 ? trigger.minX : last.minX
        let origin = textView.textContainerOrigin
        return NSRect(x: x + origin.x, y: last.minY + origin.y, width: 1, height: last.height)
    }
}
