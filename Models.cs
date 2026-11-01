using System.Text.Json.Serialization;
using TemplatePilot.Infrastructure;

namespace TemplatePilot.Models;

public sealed class AppSettings
{
    // Semantic Anchor: NewSettingsSchemaDefault
    // SSOT: new settings use the current schema from AppConstants; SettingsService.Migrate handles older files.
    public int SettingsVersion { get; set; } = AppConstants.CurrentSettingsVersion;
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "System";
    // Semantic Anchor: GlassTransparencyPreference
    // SSOT: One persisted material-density code for all Glass variants; independent of theme name.
    // Invariant: legacy settings default to Standard and retain their original theme choice.
    public string GlassTransparency { get; set; } = "Standard";
    public string TemplatesPath { get; set; } = string.Empty;
    public HotkeySettings Shortcut { get; set; } = HotkeySettings.Default();
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    // Semantic Anchor: MainViewState | SSOT: persisted view layout state belongs to AppSettings.
    // Invariant: old settings remain compatible; defaults preserve the previous expanded/Templates view.
    public bool IsNavigationPaneCollapsed { get; set; }
    public int SelectedNavigationIndex { get; set; }
    public int ActiveTabIndex { get; set; }
    // Semantic Anchor: PopupTypographySettings | SSOT: One persisted size controls both popup list and preview.
    // Invariants: legacy settings default to 13 DIP; supported range 10..20; no independent duplicate font settings.
    public int SearchPopupFontSize { get; set; } = 13;
    // Semantic Anchor: ShortcutBackdropBlurPreference
    // SSOT: a single persisted radius drives both the hotkey popup and the settings live preview.
    // Invariants: 0 disables blur; 0..32 is validated on load/save; non-Glass themes remain unaffected.
    public int SearchPopupBlurRadius { get; set; } = 18;
    // Semantic Anchor: SearchPopupPreviewLayoutSettings
    // SSOT: one persisted position code and one split ratio per orientation.
    // Invariants: legacy settings open with the preview on the right; split ratios
    // are relative to available space and survive resizing without fixed pixel widths.
    public string SearchPopupPreviewPosition { get; set; } = "Right";
    public double SearchPopupPreviewRightRatio { get; set; } = 0.34;
    public double SearchPopupPreviewBottomRatio { get; set; } = 0.40;
    // Semantic Anchor: MainWindowTypographySettings | SSOT: one persisted font size for the main window; popup typography remains independent.
    // Invariants: absent settings default to 13 DIP; saved user preferences remain unchanged; valid range is 10..20 DIP.
    public int MainWindowFontSize { get; set; } = 13;
    public int SearchPopupVisibleLines { get; set; } = 3;
    public int ClipboardTimeoutMs { get; set; } = 1500;
    public int ClipboardRetryDelayMs { get; set; } = 40;
    public int ClipboardRestoreDelayMs { get; set; } = 250;
    public int MaxImageBytes { get; set; } = 10 * 1024 * 1024;
    public string UserName { get; set; } = Environment.UserName;
    public string CompanyName { get; set; } = string.Empty;
    public bool AutosaveEditor { get; set; } = true;
    public List<string> PopupCategoryFilter { get; set; } = new();
    public List<SearchPopupColumnSettings> SearchPopupColumns { get; set; } = new();
    public Dictionary<string, WindowPlacementSettings> WindowPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Clone()
    {
        return new AppSettings
        {
            SettingsVersion = SettingsVersion,
            Language = Language,
            Theme = Theme,
            GlassTransparency = GlassTransparency,
            TemplatesPath = TemplatesPath,
            Shortcut = Shortcut.Clone(),
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            IsNavigationPaneCollapsed = IsNavigationPaneCollapsed,
            SelectedNavigationIndex = SelectedNavigationIndex,
            ActiveTabIndex = ActiveTabIndex,
            SearchPopupFontSize = SearchPopupFontSize,
            SearchPopupBlurRadius = SearchPopupBlurRadius,
            SearchPopupPreviewPosition = SearchPopupPreviewPosition,
            SearchPopupPreviewRightRatio = SearchPopupPreviewRightRatio,
            SearchPopupPreviewBottomRatio = SearchPopupPreviewBottomRatio,
            MainWindowFontSize = MainWindowFontSize,
            SearchPopupVisibleLines = SearchPopupVisibleLines,
            ClipboardTimeoutMs = ClipboardTimeoutMs,
            ClipboardRetryDelayMs = ClipboardRetryDelayMs,
            ClipboardRestoreDelayMs = ClipboardRestoreDelayMs,
            MaxImageBytes = MaxImageBytes,
            UserName = UserName,
            CompanyName = CompanyName,
            AutosaveEditor = AutosaveEditor,
            PopupCategoryFilter = PopupCategoryFilter.ToList(),
            SearchPopupColumns = SearchPopupColumns.Select(x => x.Clone()).ToList(),
            WindowPlacements = WindowPlacements.ToDictionary(
                x => x.Key,
                x => x.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };
    }
}


public sealed class SearchPopupColumnSettings
{
    public string Key { get; set; } = string.Empty;
    public int DisplayIndex { get; set; }
    public double Width { get; set; }

    public SearchPopupColumnSettings Clone()
    {
        return new SearchPopupColumnSettings
        {
            Key = Key,
            DisplayIndex = DisplayIndex,
            Width = Width
        };
    }
}


public sealed class WindowPlacementSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }

    [JsonIgnore]
    public bool HasBounds => !double.IsNaN(Left)
        && !double.IsNaN(Top)
        && !double.IsNaN(Width)
        && !double.IsNaN(Height)
        && Width > 0
        && Height > 0;

    public WindowPlacementSettings Clone()
    {
        return new WindowPlacementSettings
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            IsMaximized = IsMaximized
        };
    }
}


public sealed class CategoryCatalog
{
    public List<string> Categories { get; set; } = new();
}

public enum ClipboardDataKind
{
    Text,
    Rtf,
    Html,
    Bitmap,
    Png,
    Dib,
    Binary
}

public sealed class ClipboardFormatData
{
    public string Format { get; init; } = string.Empty;
    public ClipboardDataKind Kind { get; init; }
    public string? Text { get; init; }
    public byte[]? Bytes { get; init; }

    public bool HasData => Text is not null || Bytes is { Length: > 0 };
}

public sealed class ClipboardFormatHash
{
    public string Format { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class ClipboardFingerprint : IEquatable<ClipboardFingerprint>
{
    public static ClipboardFingerprint Empty { get; } = new(string.Empty, Array.Empty<ClipboardFormatHash>());

    public ClipboardFingerprint(string value, IReadOnlyList<ClipboardFormatHash> formats)
    {
        Value = value;
        Formats = formats;
    }

    public string Value { get; }
    public IReadOnlyList<ClipboardFormatHash> Formats { get; }

    public bool Equals(ClipboardFingerprint? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ClipboardFingerprint);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    public override string ToString()
    {
        return Value;
    }
}

public sealed class ClipboardSnapshot
{
    public static ClipboardSnapshot Empty { get; } = new(Array.Empty<ClipboardFormatData>(), ClipboardFingerprint.Empty);

    public ClipboardSnapshot(IReadOnlyList<ClipboardFormatData> formats, ClipboardFingerprint fingerprint)
    {
        Formats = formats;
        Fingerprint = fingerprint;
    }

    public IReadOnlyList<ClipboardFormatData> Formats { get; }
    public ClipboardFingerprint Fingerprint { get; }

    public string? GetPreferredText()
    {
        return Formats.FirstOrDefault(x => x.Format == ClipboardFormatNames.UnicodeText)?.Text
            ?? Formats.FirstOrDefault(x => x.Format == ClipboardFormatNames.Text)?.Text
            ?? Formats.FirstOrDefault(x => x.Kind == ClipboardDataKind.Text)?.Text;
    }

    public ClipboardFormatData? GetFormat(string format)
    {
        return Formats.FirstOrDefault(x => string.Equals(x.Format, format, StringComparison.Ordinal));
    }
}

public static class ClipboardFormatNames
{
    public const string UnicodeText = "UnicodeText";
    public const string Text = "Text";
    public const string Rtf = "Rich Text Format";
    public const string Html = "HTML Format";
    public const string Bitmap = "Bitmap";
    public const string Png = "PNG";
    public const string DeviceIndependentBitmap = "DeviceIndependentBitmap";
}

public sealed class HotkeySettings
{
    public bool Control { get; set; } = true;
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = "M";

    public static HotkeySettings Default() => new();

    public string ToDisplayString()
    {
        var parts = new List<string>();
        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Win)
        {
            parts.Add("Win");
        }

        parts.Add(Key.Trim().ToUpperInvariant());
        return string.Join("+", parts);
    }

    public HotkeySettings Clone()
    {
        return new HotkeySettings
        {
            Control = Control,
            Shift = Shift,
            Alt = Alt,
            Win = Win,
            Key = Key
        };
    }
}

public sealed class PasteResult
{
    private PasteResult(bool success, ClipboardFingerprint? pastedFingerprint, string? reason)
    {
        Success = success;
        PastedFingerprint = pastedFingerprint;
        Reason = reason;
    }

    public bool Success { get; }
    public ClipboardFingerprint? PastedFingerprint { get; }
    public string? Reason { get; }

    public static PasteResult Ok(ClipboardFingerprint fingerprint) => new(true, fingerprint, null);
    public static PasteResult Fail(string reason) => new(false, null, reason);
}

public sealed class TemplateManifest
{
    public int ContainerVersion { get; set; } = 1;
    public int FormatVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<TemplateManifestEntry> Entries { get; set; } = new();
}

public sealed class TemplateManifestEntry
{
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class TemplateMetadata
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int FormatVersion { get; set; } = 1;
    public string AppVersion { get; set; } = AppConstants.AppVersion;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Kategorie")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("Sprache")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("Clipboard-Formate")]
    public List<string> ClipboardFormats { get; set; } = new();

    [JsonPropertyName("Erstellungsdatum")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("Änderungsdatum")]
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public TemplateMetadata CloneForDuplicate()
    {
        return new TemplateMetadata
        {
            Id = Guid.NewGuid().ToString("N"),
            FormatVersion = FormatVersion,
            AppVersion = AppVersion,
            Key = Key,
            Name = Name,
            Category = Category,
            Language = Language,
            ClipboardFormats = ClipboardFormats.ToList(),
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow
        };
    }
}

public sealed class TemplatePackage
{
    public TemplateMetadata Metadata { get; set; } = new();
    public TemplateContent Content { get; set; } = new();
    public string? FilePath { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.Name) ? Metadata.Key : Metadata.Name;
}

public sealed class TemplateContent
{
    public string PlainText { get; set; } = string.Empty;
    public string Rtf { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public List<TemplateImageItem> Images { get; set; } = new();
    public List<TemplateBinaryItem> BinaryItems { get; set; } = new();

    public TemplateContent Clone()
    {
        return new TemplateContent
        {
            PlainText = PlainText,
            Rtf = Rtf,
            Html = Html,
            Images = Images.Select(x => x.Clone()).ToList(),
            BinaryItems = BinaryItems.Select(x => x.Clone()).ToList()
        };
    }
}

public sealed class TemplateImageItem
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public byte[] Bytes { get; set; } = Array.Empty<byte>();

    public TemplateImageItem Clone()
    {
        return new TemplateImageItem
        {
            FileName = FileName,
            MediaType = MediaType,
            Bytes = Bytes.ToArray()
        };
    }
}

public sealed class TemplateBinaryItem
{
    public string Format { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public byte[] Bytes { get; set; } = Array.Empty<byte>();

    public TemplateBinaryItem Clone()
    {
        return new TemplateBinaryItem
        {
            Format = Format,
            FileName = FileName,
            Bytes = Bytes.ToArray()
        };
    }
}

public sealed class WindowTargetContext
{
    public WindowTargetContext(IntPtr foregroundWindow, IntPtr focusWindow, uint threadId, DateTime capturedUtc)
    {
        ForegroundWindow = foregroundWindow;
        FocusWindow = focusWindow;
        ThreadId = threadId;
        CapturedUtc = capturedUtc;
    }

    public IntPtr ForegroundWindow { get; }
    public IntPtr FocusWindow { get; }
    public uint ThreadId { get; }
    public DateTime CapturedUtc { get; }
    public bool IsValid => ForegroundWindow != IntPtr.Zero;
}
