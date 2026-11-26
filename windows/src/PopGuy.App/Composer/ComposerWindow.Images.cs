using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PopGuy.Controls;
using PopGuy.Core.Composer;

namespace PopGuy.Composer;

/// <summary>
/// The composer's image strip and preview (macOS ComposerImageStrip.swift, ComposerImagePreview.swift,
/// ComposerThumbnailLoader.swift): a 48 px tile per "@path" image mention whose file is a local regular file, plus a
/// chip per CLI "[Image #N]" placeholder while <see cref="ShowsClaudeImageTokens"/>. A click selects the mention in the
/// editor (a file also opens the in-window preview); the hover x removes it as one undoable edit. Nothing in the strip
/// takes the focus from the editor. Mention paths are checked by <see cref="NormalizedLocalImagePath"/> before any file
/// system call, so a mention never reaches the network. The text and paths are never logged. UI thread only.
/// </summary>
public sealed partial class ComposerWindow
{
    private const double TileSize = 48;
    private const double RemoveButtonSize = 18;

    /// <summary>Largest file the strip and the preview decode.</summary>
    private const long MaxImageBytes = 50L * 1024 * 1024;

    /// <summary>The preview card's chrome, as in the XAML (margin, padding, border, header row and row spacing).</summary>
    private const double PreviewCardMargin = 24;
    private const double PreviewCardPadding = 12;
    private const double PreviewCardBorder = 1;
    private const double PreviewHeaderHeight = 32;
    private const double PreviewRowSpacing = 8;

    /// <summary>The image area while the image loads or failed (bounded by the window), and the caption's least width.</summary>
    private const double PreviewPlaceholderWidth = 320;
    private const double PreviewPlaceholderHeight = 220;
    private const double PreviewMinWidth = 200;

    /// <summary>The preview's largest decode height (DIPs).</summary>
    private const int PreviewMaxDecodeHeight = 2048;

    private const string PictureGlyph = "\uE91B";
    private const string CantPreview = "Can't preview";

    /// <summary>The strip follows the text after a pause in typing (macOS: 150 ms).</summary>
    private static readonly TimeSpan StripDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The preview's ring shows only for a load slower than this, so fast loads don't flash it.</summary>
    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a selection made by a tile click counts as the composer's own.</summary>
    private const long OwnSelectionMilliseconds = 1000;

    /// <summary>One strip tile: the item, its index among the items with the same token, its checked local path (files).</summary>
    private sealed record StripEntry(ComposerAttachment Item, int Occurrence, string? LocalPath);

    private sealed record OwnSelection(string Text, long Ticks);

    private bool _showsClaudeImageTokens;
    private DispatcherQueueTimer? _stripTimer;
    private DispatcherQueueTimer? _spinnerTimer;

    /// <summary>Bumped on every refresh: a file check for an older text is dropped.</summary>
    private int _stripGeneration;
    private string? _stripText;
    private List<StripEntry> _stripEntries = [];

    /// <summary>Mention paths (as written) whose normalized file was found, from the last check: shown at once next time.</summary>
    private Dictionary<string, string> _knownImages = new(StringComparer.Ordinal);

    /// <summary>Thumbnails, loads in flight and failed loads, keyed by normalized path.</summary>
    private readonly Dictionary<string, BitmapImage> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<BitmapImage?>> _thumbnailLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedThumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every open and close: a decode for an older preview is dropped.</summary>
    private int _previewGeneration;
    private bool _previewLoading;
    private readonly ScalarTransition _ringFade = new() { Duration = TimeSpan.FromMilliseconds(150) };

    private OwnSelection? _ownSelection;

#if DEBUG
    /// <summary>From the tile click, for the preview's timing log.</summary>
    private Stopwatch? _previewClock;
#endif

    /// <summary>Where Debug builds write the preview's timing lines (no text, no paths); null: not written.</summary>
    internal Action<string>? Log { get; set; }

    /// <summary>The CLI puts "[Image #N]" placeholders in its prompt (Claude Code): they show as chips.</summary>
    internal bool ShowsClaudeImageTokens
    {
        get => _showsClaudeImageTokens;
        set
        {
            _showsClaudeImageTokens = value;
            RefreshImageStrip();
        }
    }

    /// <summary>The enlarged preview shows (Esc closes it).</summary>
    internal bool IsPreviewOpen => ImagePreview.Visibility == Visibility.Visible;

    /// <summary>
    /// True for <paramref name="text"/> selected by a tile click within the last second: PopGuy's own toolbar ignores it.
    /// </summary>
    internal bool IsOwnSelection(string text)
    {
        var own = Volatile.Read(ref _ownSelection);
        return own is not null
            && Environment.TickCount64 - own.Ticks <= OwnSelectionMilliseconds
            && string.Equals(own.Text, text.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Wires the strip and the preview (from the constructor, after the XAML is loaded).</summary>
    private void InitializeImages()
    {
        _stripTimer = DispatcherQueue.CreateTimer();
        _stripTimer.Interval = StripDelay;
        _stripTimer.IsRepeating = false;
        _stripTimer.Tick += OnStripTick;
        _spinnerTimer = DispatcherQueue.CreateTimer();
        _spinnerTimer.Interval = SpinnerDelay;
        _spinnerTimer.IsRepeating = false;
        _spinnerTimer.Tick += OnSpinnerTick;

        SetIconButton(ImagePreviewClose, "Close (Esc)");
        AutomationProperties.SetName(ImagePreviewImage, "Image preview");
        // Tab stays on the close button while the preview shows.
        ImagePreview.TabFocusNavigation = KeyboardNavigationMode.Cycle;

        Editor.TextChanged += OnImageTextChanged;
        Editor.Loaded += OnImageEditorLoaded;
        ImageStrip.ActualThemeChanged += OnImageStripThemeChanged;
        ImagePreviewScrim.Tapped += OnPreviewScrimTapped;
        ImagePreviewClose.Click += OnPreviewCloseClick;
        ImagePreviewImage.ImageFailed += OnPreviewImageFailed;
        ImagePreview.SizeChanged += OnPreviewSizeChanged;
        Closed += OnImagesClosed;
    }

    private void OnImagesClosed(object sender, WindowEventArgs args)
    {
        _stripTimer?.Stop();
        _spinnerTimer?.Stop();
        _stripGeneration++;
        _previewGeneration++;
#if DEBUG
        CompositionTarget.Rendering -= OnPreviewRendering;
#endif
        Editor.TextChanged -= OnImageTextChanged;
        Editor.Loaded -= OnImageEditorLoaded;
        ImageStrip.ActualThemeChanged -= OnImageStripThemeChanged;
        ImagePreviewScrim.Tapped -= OnPreviewScrimTapped;
        ImagePreviewClose.Click -= OnPreviewCloseClick;
        ImagePreviewImage.ImageFailed -= OnPreviewImageFailed;
        ImagePreview.SizeChanged -= OnPreviewSizeChanged;
        Closed -= OnImagesClosed;
        _thumbnails.Clear();
    }

    // The strip.

    private void OnImageTextChanged(object sender, RoutedEventArgs e)
    {
        if (_formatting || _closed) return;
        _stripTimer?.Stop();
        _stripTimer?.Start();
    }

    /// <summary>The text set before the window showed (a TextChanged for it is not certain).</summary>
    private void OnImageEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (!_closed && RawText() != _stripText) RefreshImageStrip();
    }

    /// <summary>A format pass raises TextChanged too: only a changed text refreshes.</summary>
    private void OnStripTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_closed && RawText() != _stripText) RefreshImageStrip();
    }

    private void OnImageStripThemeChanged(FrameworkElement sender, object args)
    {
        if (!_closed && _stripEntries.Count > 0) BuildStripTiles();
    }

    /// <summary>
    /// Parses the text now: files already known to exist and the chips show at once, the other files once a check off
    /// the UI thread finds them (applied only while the text is unchanged).
    /// </summary>
    internal void RefreshImageStrip()
    {
        if (_closed) return;
        _stripTimer?.Stop();
        var text = RawText();
        _stripText = text;
        var generation = ++_stripGeneration;
        var items = ComposerAttachments.Parse(text);
        ApplyStrip(items, _knownImages);

        var paths = items.Where(item => item.Kind == ComposerAttachmentKind.File && item.Path is not null)
            .Select(item => item.Path!).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
        {
            _knownImages = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }
        _ = CheckImageFilesAsync(text, generation, items, paths);
    }

    private async Task CheckImageFilesAsync(
        string text, int generation, IReadOnlyList<ComposerAttachment> items, string[] paths)
    {
        var found = await Task.Run(() => ExistingImageFiles(paths));
        if (_closed || generation != _stripGeneration || RawText() != text) return;
        _knownImages = found;
        ApplyStrip(items, found);
    }

    /// <summary>The mention paths (as written) whose normalized path is a local regular file, with that path.</summary>
    private static Dictionary<string, string> ExistingImageFiles(string[] paths)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (NormalizedLocalImagePath(path) is { } normalized && IsRegularLocalFile(normalized)) found[path] = normalized;
        }
        return found;
    }

    /// <summary>The strip's entries for <paramref name="items"/>; the tiles are rebuilt only when the list changed.</summary>
    private void ApplyStrip(IReadOnlyList<ComposerAttachment> items, Dictionary<string, string> files)
    {
        var entries = new List<StripEntry>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var occurrence = occurrences.GetValueOrDefault(item.Token);
            occurrences[item.Token] = occurrence + 1;
            if (item.Kind == ComposerAttachmentKind.File)
            {
                if (item.Path is { } path && files.TryGetValue(path, out var local)) entries.Add(new StripEntry(item, occurrence, local));
            }
            else if (_showsClaudeImageTokens)
            {
                entries.Add(new StripEntry(item, occurrence, null));
            }
        }

        var changed = entries.Count != _stripEntries.Count
            || entries.Where((entry, i) => !SameTile(entry, _stripEntries[i])).Any();
        _stripEntries = entries;
        if (!changed) return;
        _failedThumbnails.IntersectWith(entries.Select(entry => entry.LocalPath).OfType<string>());
        BuildStripTiles();
    }

    private static bool SameTile(StripEntry a, StripEntry b) =>
        a.Item.Kind == b.Item.Kind && a.Item.Token == b.Item.Token && a.Occurrence == b.Occurrence
        && string.Equals(a.LocalPath, b.LocalPath, StringComparison.OrdinalIgnoreCase);

    private void BuildStripTiles()
    {
        ImageStrip.Children.Clear();
        // Opaque theme colors for the x over a thumbnail (rebuilt on a theme switch).
        var theme = RootGrid.ActualTheme;
        var removeFill = new SolidColorBrush(ThemeColor("ControlSolidFillColorDefault", theme));
        var removeStroke = new SolidColorBrush(ThemeColor("ControlStrokeColorDefault", theme));
        for (var i = 0; i < _stripEntries.Count; i++) ImageStrip.Children.Add(BuildTile(i, _stripEntries[i], removeFill, removeStroke));
        ImageStripHost.Visibility = _stripEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Started once the tiles are in the strip, so a load that is already done still finds them.
        var pending = _stripEntries.Select(entry => entry.LocalPath).OfType<string>()
            .Where(path => !_thumbnails.ContainsKey(path) && !_failedThumbnails.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in pending) _ = LoadTileThumbnailAsync(path);
    }

    /// <summary>
    /// A tile: the thumbnail (or chip) button and, beside it in the same cell, the x button shown while the pointer is
    /// over the tile. Neither takes the focus.
    /// </summary>
    private Grid BuildTile(int index, StripEntry entry, Brush removeFill, Brush removeStroke)
    {
        var tile = new Grid { Height = TileSize, Background = new SolidColorBrush(Colors.Transparent) };
        var main = new Button
        {
            Height = TileSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Tag = index,
        };
        string name;
        if (entry.LocalPath is { } path)
        {
            tile.Width = TileSize;
            main.Width = TileSize;
            name = Path.GetFileName(path);
            if (_thumbnails.TryGetValue(path, out var cached)) main.Content = ThumbnailImage(cached, main, path);
            else main.Content = PictureIcon();
            HoverTooltip.SetText(main, _failedThumbnails.Contains(path) ? CantPreview : name);
            AutomationProperties.SetName(main, $"Image {name}");
        }
        else
        {
            name = $"Image #{entry.Item.Number.ToString(CultureInfo.InvariantCulture)}";
            main.Padding = new Thickness(10, 0, 10, 0);
            main.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { PictureIcon(), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } },
            };
            SetIconButton(main, $"{name}, attached in the terminal");
        }
        main.Click += OnTileClick;

        var remove = new Button
        {
            Width = RemoveButtonSize,
            Height = RemoveButtonSize,
            Margin = new Thickness(0, 3, 3, 0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(RemoveButtonSize / 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = removeFill,
            BorderBrush = removeStroke,
            Opacity = 0,
            IsHitTestVisible = false,
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Tag = index,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 8 },
        };
        HoverTooltip.SetText(remove, "Remove from the text");
        AutomationProperties.SetName(remove, $"Remove {name} from the text");
        remove.Click += OnTileRemoveClick;

        tile.Children.Add(main);
        tile.Children.Add(remove);
        tile.PointerEntered += OnTilePointerMoved;
        tile.PointerExited += OnTilePointerMoved;
        return tile;
    }

    private static FontIcon PictureIcon() => new() { Glyph = PictureGlyph, FontSize = 16 };

    private Image ThumbnailImage(BitmapImage source, Button button, string path)
    {
        var image = new Image { Source = source, Stretch = Stretch.UniformToFill, Width = TileSize, Height = TileSize };
        image.ImageFailed += (_, _) =>
        {
            _thumbnails.Remove(path);
            _failedThumbnails.Add(path);
            button.Content = PictureIcon();
            HoverTooltip.SetText(button, CantPreview);
        };
        return image;
    }

    /// <summary>
    /// Once loaded, the thumbnail goes in place into the strip's current tiles of <paramref name="path"/>, found by path:
    /// the tiles may have been rebuilt meanwhile. A failure is marked only while a tile shows the path.
    /// </summary>
    private async Task LoadTileThumbnailAsync(string path)
    {
        var image = await LoadThumbnailAsync(path);
        if (_closed) return;
        var buttons = TileButtons(path);
        if (image is null)
        {
            if (buttons.Count == 0) return;
            _failedThumbnails.Add(path);
            foreach (var button in buttons) HoverTooltip.SetText(button, CantPreview);
            return;
        }
        foreach (var button in buttons)
        {
            if (button.Content is not Image) button.Content = ThumbnailImage(image, button, path);
        }
    }

    /// <summary>The thumbnail buttons of the strip's tiles whose file is <paramref name="path"/>.</summary>
    private List<Button> TileButtons(string path)
    {
        var buttons = new List<Button>();
        foreach (var child in ImageStrip.Children)
        {
            if (child is Grid { Children.Count: > 0 } tile && tile.Children[0] is Button { Tag: int index } button
                && index < _stripEntries.Count
                && string.Equals(_stripEntries[index].LocalPath, path, StringComparison.OrdinalIgnoreCase))
            {
                buttons.Add(button);
            }
        }
        return buttons;
    }

    /// <summary>
    /// The x shows while the pointer is inside the tile. Pointer events bubble from the two buttons, so the pointer's
    /// place decides, not which element raised the event.
    /// </summary>
    private void OnTilePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid { Children.Count: 2 } tile) return;
        var point = e.GetCurrentPoint(tile).Position;
        var inside = point.X >= 0 && point.Y >= 0 && point.X < tile.ActualWidth && point.Y < tile.ActualHeight;
        var remove = tile.Children[1];
        remove.Opacity = inside ? 1 : 0;
        remove.IsHitTestVisible = inside;
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && index < _stripEntries.Count) ActivateAttachment(_stripEntries[index].Item);
    }

    private void OnTileRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && index < _stripEntries.Count) RemoveAttachment(_stripEntries[index].Item);
    }

    /// <summary>
    /// The item in <paramref name="text"/> with the same token and the same index among equal tokens as the strip's
    /// <paramref name="item"/>, with its strip entry; null when the text no longer has it.
    /// </summary>
    private (ComposerAttachment Match, StripEntry Entry)? CurrentMatch(ComposerAttachment item, string text)
    {
        if (_stripEntries.Find(entry => entry.Item == item) is not { } stripEntry) return null;
        var occurrence = 0;
        foreach (var candidate in ComposerAttachments.Parse(text))
        {
            if (candidate.Token != item.Token) continue;
            if (occurrence == stripEntry.Occurrence) return (candidate, stripEntry);
            occurrence++;
        }
        return null;
    }

    /// <summary>
    /// Selects the item in the editor (marked as the composer's own selection, see <see cref="IsOwnSelection"/>); a file
    /// also opens the preview in the same call, a chip gives the focus back to the editor.
    /// </summary>
    internal void ActivateAttachment(ComposerAttachment item)
    {
        if (_closed) return;
#if DEBUG
        var clock = Stopwatch.StartNew();
#endif
        if (CurrentMatch(item, RawText()) is not { } found) return;
        var match = found.Match;
        Volatile.Write(ref _ownSelection, new OwnSelection(match.Token, Environment.TickCount64));
        var selection = Editor.TextDocument.Selection;
        selection.SetRange(match.Start, match.Start + match.Length);
        selection.ScrollIntoView(PointOptions.None);
        if (match.Kind == ComposerAttachmentKind.File && found.Entry.LocalPath is { } path)
        {
#if DEBUG
            _previewClock = clock;
#endif
            OpenPreview(path);
            return;
        }
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Deletes the item and one adjacent space as one range edit (one RichEdit undo step, as list edits, so Ctrl+Z brings
    /// it back), with the caret where it was. The strip follows at once; the suggestions never open from it.
    /// </summary>
    internal void RemoveAttachment(ComposerAttachment item)
    {
        if (_closed) return;
        var text = RawText();
        if (CurrentMatch(item, text) is not { } found) return;
        var (start, length) = ComposerAttachments.RemovalRange(text, found.Match.Start, found.Match.Length);
        var document = Editor.TextDocument;
        document.GetRange(start, start + length).SetText(TextSetOptions.None, "");
        document.Selection.SetRange(start, start);
        _highlightTimer.Stop();
        _highlightTimer.Start();
        RefreshSuggestions();
        if (SuggestionsVisible) DismissSuggestions();
        RefreshImageStrip();
        Editor.Focus(FocusState.Programmatic);
    }

    // The preview.

    /// <summary>
    /// Shows the overlay at once with the file name and an empty image area, then the image once decoded (or the error).
    /// The ring shows only when the decode takes longer than <see cref="SpinnerDelay"/>. The focus moves to the close
    /// button, so typing never reaches the editor under the scrim. <paramref name="path"/> is a normalized path.
    /// </summary>
    internal void OpenPreview(string path)
    {
        if (_closed) return;
        if (SuggestionsVisible) DismissSuggestions();
        var generation = ++_previewGeneration;
        _previewLoading = true;
        ImagePreviewImage.Source = null;
        ImagePreviewImage.MaxWidth = double.PositiveInfinity;
        ImagePreviewImage.MaxHeight = double.PositiveInfinity;
        ImagePreviewError.Visibility = Visibility.Collapsed;
        SetRingVisible(false);
        var name = Path.GetFileName(path);
        ImagePreviewCaption.Text = name;
        HoverTooltip.SetText(ImagePreviewCaption, name);
        ApplyPreviewBounds();
        ImagePreview.Visibility = Visibility.Visible;
        LogPreviewTiming("placeholder");
#if DEBUG
        CompositionTarget.Rendering -= OnPreviewRendering;
        CompositionTarget.Rendering += OnPreviewRendering;
#endif
        if (!ImagePreviewClose.Focus(FocusState.Programmatic))
        {
            // Not focusable until the overlay's first layout: lay it out now, else retry on the next turn.
            ImagePreview.UpdateLayout();
            if (!ImagePreviewClose.Focus(FocusState.Programmatic))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && IsPreviewOpen && generation == _previewGeneration) ImagePreviewClose.Focus(FocusState.Programmatic);
                });
            }
        }
        _spinnerTimer?.Stop();
        _spinnerTimer?.Start();

        var height = PreviewAvailableSize().Height;
        var decodeHeight = height >= 1 ? (int)Math.Min(height, PreviewMaxDecodeHeight) : PreviewMaxDecodeHeight;
        _ = LoadPreviewAsync(path, generation, decodeHeight);
    }

    /// <summary>Hides the preview (nothing when it is not open) and gives the focus back to the editor.</summary>
    internal void ClosePreview()
    {
        if (!IsPreviewOpen) return;
        _previewGeneration++;
        _previewLoading = false;
        _spinnerTimer?.Stop();
#if DEBUG
        CompositionTarget.Rendering -= OnPreviewRendering;
        _previewClock = null;
#endif
        ImagePreview.Visibility = Visibility.Collapsed;
        ImagePreviewImage.Source = null;
        ImagePreviewError.Visibility = Visibility.Collapsed;
        SetRingVisible(false);
        RefreshOwnSelection();
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Counts the tile click's selection as the composer's own again from now while the editor still selects it: the
    /// mouse-up of a click that closes the preview would otherwise show PopGuy's toolbar for it.
    /// </summary>
    private void RefreshOwnSelection()
    {
        if (Volatile.Read(ref _ownSelection) is not { } own) return;
        Editor.TextDocument.Selection.GetText(TextGetOptions.None, out var selected);
        if (string.Equals(own.Text, selected.Trim(), StringComparison.Ordinal))
            Volatile.Write(ref _ownSelection, own with { Ticks = Environment.TickCount64 });
    }

    private async Task LoadPreviewAsync(string path, int generation, int decodeHeight)
    {
        var image = await DecodeImageAsync(path, decodeHeight);
        if (_closed || generation != _previewGeneration) return;
        _previewLoading = false;
        _spinnerTimer?.Stop();
        SetRingVisible(false);
        if (image is null)
        {
            ImagePreviewError.Visibility = Visibility.Visible;
            LogPreviewTiming("failed");
            return;
        }
        // Never shown larger than its own size in DIPs.
        var scale = RootGrid.XamlRoot?.RasterizationScale is > 0 and var value ? value : 1;
        if (image.PixelWidth > 0 && image.PixelHeight > 0)
        {
            ImagePreviewImage.MaxWidth = image.PixelWidth / scale;
            ImagePreviewImage.MaxHeight = image.PixelHeight / scale;
        }
        ImagePreviewImage.Source = image;
        ApplyPreviewBounds();
        LogPreviewTiming("image");
    }

    private void OnSpinnerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_closed || !IsPreviewOpen || !_previewLoading) return;
        SetRingVisible(true);
        LogPreviewTiming("spinner");
    }

    /// <summary>The ring fades in; it goes at once (a fade-out would still show on the next open).</summary>
    private void SetRingVisible(bool visible)
    {
        ImagePreviewRing.OpacityTransition = visible ? _ringFade : null;
        ImagePreviewRing.Opacity = visible ? 1 : 0;
    }

    private void OnPreviewImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!IsPreviewOpen) return;
        ImagePreviewImage.Source = null;
        ImagePreviewError.Visibility = Visibility.Visible;
        ApplyPreviewBounds();
        LogPreviewTiming("failed");
    }

    private void OnPreviewScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ClosePreview();
    }

    private void OnPreviewCloseClick(object sender, RoutedEventArgs e) => ClosePreview();

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsPreviewOpen) ApplyPreviewBounds();
    }

    /// <summary>
    /// The room for the image inside the card: the whole window (the overlay covers every row) less the card's chrome.
    /// The overlay itself has no size while collapsed, so the root grid's is used.
    /// </summary>
    private (double Width, double Height) PreviewAvailableSize()
    {
        var chrome = 2 * (PreviewCardMargin + PreviewCardPadding + PreviewCardBorder);
        return (Math.Max(0, RootGrid.ActualWidth - chrome),
            Math.Max(0, RootGrid.ActualHeight - chrome - PreviewHeaderHeight - PreviewRowSpacing));
    }

    /// <summary>
    /// Bounds the image area by the window: the placeholder size while there is no image, else the image's own size
    /// (at least the caption's width), so the card always fits, also at the window's minimum size.
    /// </summary>
    private void ApplyPreviewBounds()
    {
        var (width, height) = PreviewAvailableSize();
        ImagePreviewArea.MaxWidth = width;
        ImagePreviewArea.MaxHeight = height;
        if (ImagePreviewImage.Source is null)
        {
            ImagePreviewArea.MinWidth = 0;
            ImagePreviewArea.Width = Math.Min(PreviewPlaceholderWidth, width);
            ImagePreviewArea.Height = Math.Min(PreviewPlaceholderHeight, height);
        }
        else
        {
            ImagePreviewArea.Width = double.NaN;
            ImagePreviewArea.Height = double.NaN;
            ImagePreviewArea.MinWidth = Math.Min(PreviewMinWidth, width);
        }
    }

    // Loading.

    /// <summary>
    /// The thumbnail of <paramref name="path"/> (a normalized path), decoded once per window; null when it cannot be
    /// read or decoded.
    /// </summary>
    internal async Task<BitmapImage?> LoadThumbnailAsync(string path)
    {
        if (_thumbnails.TryGetValue(path, out var cached)) return cached;
        if (_thumbnailLoads.TryGetValue(path, out var pending)) return await pending;
        var load = DecodeImageAsync(path, (int)TileSize);
        _thumbnailLoads[path] = load;
        var image = await load;
        _thumbnailLoads.Remove(path);
        if (image is not null && !_closed) _thumbnails[path] = image;
        return image;
    }

    /// <summary>
    /// <paramref name="path"/> decoded at <paramref name="height"/> DIPs (aspect kept; GIFs not animated), after the
    /// path rule and the regular-file and size checks off the UI thread. The bitmap is made on the UI thread.
    /// </summary>
    private async Task<BitmapImage?> DecodeImageAsync(string path, int height)
    {
        try
        {
            var readable = await Task.Run(() =>
                NormalizedLocalImagePath(path) == path && IsRegularLocalFile(path) && new FileInfo(path).Length <= MaxImageBytes);
            if (!readable || _closed) return null;
            var bitmap = new BitmapImage { DecodePixelHeight = height, DecodePixelType = DecodePixelType.Logical, AutoPlay = false };
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            // Any read or decode failure: the fallback glyph or the preview's error.
            return null;
        }
    }

    /// <summary>
    /// A mention's path as a normalized local drive path, or null. Lexical apart from the drive type, and run before any
    /// other file system call: <see cref="ComposerImagePath.NormalizedDrivePath"/> (an "X:\…" path, never UNC or a
    /// device path, no stream suffix, no reserved DOS device name), which the framework's own normalization of the
    /// mention must match, then a drive that is no network drive. The returned string is the only one used for later
    /// file system calls and as the thumbnail key. A <c>subst</c> drive of a UNC path reports a network drive.
    /// </summary>
    internal static string? NormalizedLocalImagePath(string path)
    {
        if (ComposerImagePath.NormalizedDrivePath(path) is not { } normalized) return null;
        try
        {
            // The framework's rules differ in corner cases (it trims trailing dots and spaces): any difference rejects.
            if (!string.Equals(Path.GetFullPath(path), normalized, StringComparison.Ordinal)) return null;
            var type = new DriveInfo(normalized[..3]).DriveType;
            if (type is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) return null;
            return normalized;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException
                                      or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// A regular file: neither it nor a folder on the way to it is a directory link, junction or other reparse point
    /// (which could lead to a share), and it is no directory or device. Folders are checked from the drive root down, so
    /// no link is followed. Never throws.
    /// </summary>
    private static bool IsRegularLocalFile(string path)
    {
        try
        {
            var folders = new Stack<string>();
            for (var folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
                folders.Push(folder);
            foreach (var folder in folders)
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return false;
            }
            const FileAttributes Rejected = FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint;
            return (File.GetAttributes(path) & Rejected) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Debug timing (click reacts first).

    [Conditional("DEBUG")]
    private void LogPreviewTiming(string stage)
    {
#if DEBUG
        if (_previewClock is not { } clock) return;
        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"composer: imagePreview t+{clock.Elapsed.TotalMilliseconds:F1}ms {stage}"));
#endif
    }

#if DEBUG
    /// <summary>The overlay's first frame.</summary>
    private void OnPreviewRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnPreviewRendering;
        LogPreviewTiming("overlayShown");
    }
#endif
}
