using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.Json;
using TemplatePilot.Infrastructure;
using TemplatePilot.Models;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using TemplatePilot.Helpers;
using TemplatePilot.ViewModels;
using TemplatePilot.Views;
using Forms = System.Windows.Forms;

namespace TemplatePilot.Services;

[SupportedOSPlatform("windows7.0")]
public sealed class AppThemeService
{
    private readonly LoggingService _logging;
    private Window? _watchedWindow;
    private bool _isWatchingSystemTheme;
    private bool _glassBackdropEnabled;
    private readonly HashSet<Wpf.Ui.Controls.FluentWindow> _secondaryWindows = new();

    // Semantic Anchor: SearchPopupGlassModeSSOT
    // Invariant: the popup chooses its native or layered HWND rendering mode once, before Show().
    // The window's focus state must never change that choice.
    public bool IsLayeredPopupGlassActive => _glassBackdropEnabled;

    public AppThemeService(LoggingService logging)
    {
        _logging = logging;
    }

    public void RegisterMainWindow(Window window, string themeMode, string transparencyMode = "Standard")
    {
        _watchedWindow = window;
        ApplyTheme(themeMode, transparencyMode);
    }

    public void ApplyDefaultTheme()
    {
        ApplyTheme("System");
    }

    // Semantic Anchor: SecondaryWindowThemeLifecycle
    // SSOT: AppThemeService determines the material for every application-owned FluentWindow.
    // Invariants: preserve existing window chrome, input and focus behavior; use the same shared
    // palette as the main window; unregister closed windows; never force a native backdrop
    // when the current theme, OS or High Contrast mode disallows it.
    public void RegisterSecondaryWindow(Wpf.Ui.Controls.FluentWindow window)
    {
        if (!_secondaryWindows.Add(window))
        {
            return;
        }

        // Semantic Anchor: SecondaryWindowBackgroundOwnership
        // Invariant: layered WPF windows require a truly transparent HWND root; their
        // visible material is drawn by the popup Border, not by the FluentWindow template.
        if (window.AllowsTransparency)
        {
            window.Background = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            window.SetResourceReference(Control.BackgroundProperty, "TemplatePilotWindowSurfaceBrush");
        }

        window.Loaded += SecondaryWindow_Loaded;
        window.Closed += SecondaryWindow_Closed;
        ApplySecondaryWindowBackdrop(window);
    }

    private void SecondaryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.FluentWindow window)
        {
            ApplySecondaryWindowBackdrop(window);
        }
    }

    private void SecondaryWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is Wpf.Ui.Controls.FluentWindow window)
        {
            window.Loaded -= SecondaryWindow_Loaded;
            window.Closed -= SecondaryWindow_Closed;
            _secondaryWindows.Remove(window);
        }
    }

    // Semantic Anchor: SecondaryWindowBackdropComposition
    // SSOT: AppThemeService selects native composition for ordinary FluentWindows;
    // the layered search popup remains on a separate WPF transparency path.
    // Invariants: existing dialogs retain their native WindowChrome handling;
    // all windows preserve sizing, input, theme brushes and High Contrast restrictions.
    // A successful native API call alone does not guarantee visible blur (OS-controlled).
    private void ApplySecondaryWindowBackdrop(Wpf.Ui.Controls.FluentWindow window)
    {
        // Semantic Anchor: SearchPopupLayeredGlassBackdropExclusion
        // Invariant: WPF per-pixel transparency and native DWM Acrylic are mutually
        // exclusive HWND strategies. Never reset the transparent composition target
        // via WindowBackdrop.RemoveBackdrop on an already layered popup.
        if (window.AllowsTransparency)
        {
            window.Background = System.Windows.Media.Brushes.Transparent;
            return;
        }

        if (!window.IsLoaded)
        {
            return;
        }

        // Semantic Anchor: SecondaryFluentNativeBackdropLifecycle
        // WPF-UI applies Acrylic to non-layered windows with extended title bar content.
        // Non-Glass and unsupported environments retain the normal opaque surface.
        if (window.ExtendsContentIntoTitleBar)
        {
            var backdropType = _glassBackdropEnabled
                ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
                : Wpf.Ui.Controls.WindowBackdropType.None;

            if (window.WindowBackdropType != backdropType)
            {
                window.WindowBackdropType = backdropType;
            }

            if (!_glassBackdropEnabled)
            {
                window.SetResourceReference(Control.BackgroundProperty, "TemplatePilotWindowSurfaceBrush");
            }

            return;
        }

        // Secondary windows that keep ExtendsContentIntoTitleBar=False use their
        // existing manual WindowChrome path to preserve their current behavior.
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        if (chrome is null)
        {
            chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                ResizeBorderThickness = window.ResizeMode == ResizeMode.NoResize
                    ? new Thickness(0)
                    : new Thickness(4),
                UseAeroCaptionButtons = false
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);
        }

        chrome.GlassFrameThickness = _glassBackdropEnabled
            ? new Thickness(-1)
            : new Thickness(0.00001);

        if (!_glassBackdropEnabled)
        {
            Wpf.Ui.Controls.WindowBackdrop.RemoveBackdrop(window);
            window.SetResourceReference(Control.BackgroundProperty, "TemplatePilotWindowSurfaceBrush");
            return;
        }

        bool applied = Wpf.Ui.Controls.WindowBackdrop.RemoveBackground(window)
            && Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(
                window, Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        if (!applied)
        {
            _logging.Warning($"Glass theme: native Desktop Acrylic could not be enabled for {window.GetType().Name}.");
        }
    }

    // Semantic Anchor: GlassThemeIdentity
    // SSOT: Glass theme identities are shared by native Acrylic and layered-popup materials.
    // Invariant: each code is stable across language changes and is recognized by settings validation.
    public static bool IsGlassTheme(string? themeMode)
    {
        return themeMode is not null && (string.Equals(themeMode, "LiquidGlass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeMode, "GlassBlue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeMode, "GlassCrystal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeMode, "GlassGraphite", StringComparison.OrdinalIgnoreCase)
            || string.Equals(themeMode, "GlassViolet", StringComparison.OrdinalIgnoreCase));
    }

    // Semantic Anchor: AppThemeMaterialSSOT
    // SSOT: The selected theme and transparency preference define all Window/Card/Popup material brushes.
    // Invariants: System/Light/Dark themes retain their appearance; WPF-UI native Acrylic remains
    // OS-managed for normal HWNDs. The Glass popup uses per-pixel WPF transparency instead,
    // keeping a legible tinted surface regardless of activation. No custom blur is claimed.
    // High Contrast and unsupported Windows builds retain opaque theme surfaces.
    public void ApplyTheme(string themeMode, string transparencyMode = "Standard")
    {
        if (_isWatchingSystemTheme && _watchedWindow is not null)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(_watchedWindow);
            _isWatchingSystemTheme = false;
        }

        bool isGlass = IsGlassTheme(themeMode);
        bool isCrystal = string.Equals(themeMode, "GlassCrystal", StringComparison.OrdinalIgnoreCase);
        bool acrylicAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        bool enableGlass = isGlass && acrylicAvailable && !System.Windows.SystemParameters.HighContrast;
        _glassBackdropEnabled = enableGlass;

        if (isGlass || string.Equals(themeMode, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                isCrystal ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        }
        else if (string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase))
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Acrylic);
        }
        else
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

            if (_watchedWindow is not null)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(
                    _watchedWindow,
                    Wpf.Ui.Controls.WindowBackdropType.Acrylic);
                _isWatchingSystemTheme = true;
            }
        }

        var resources = System.Windows.Application.Current.Resources;
        if (enableGlass)
        {
            // Semantic Anchor: GlassMaterialPalette
            // SSOT: Shared levels control Alpha; stable theme codes select RGB tint only.
            // Invariant: Clear has less tint than Standard or Subtle;
            // palette changes must not modify the native backdrop or non-Glass themes.
            int cardAlpha = transparencyMode switch
            {
                "Subtle" => 172,
                "Clear" => 72,
                _ => 112
            };
            byte windowAlpha = transparencyMode switch
            {
                "Subtle" => 52,
                "Clear" => 16,
                _ => 32
            };

            var color = themeMode.ToUpperInvariant() switch
            {
                "GLASSBLUE" => System.Windows.Media.Color.FromRgb(32, 75, 114),
                "GLASSCRYSTAL" => System.Windows.Media.Color.FromRgb(195, 211, 226),
                "GLASSGRAPHITE" => System.Windows.Media.Color.FromRgb(49, 55, 64),
                "GLASSVIOLET" => System.Windows.Media.Color.FromRgb(83, 65, 110),
                _ => System.Windows.Media.Color.FromRgb(32, 32, 32)
            };

            resources["TemplatePilotWindowSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(windowAlpha, color.R, color.G, color.B));
            resources["TemplatePilotCardSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb((byte)cardAlpha, color.R, color.G, color.B));
            // Semantic Anchor: SearchPopupLayeredGlassPalette
            // SSOT: reuse the active theme tint; the popup's HWND-transparent surface
            // needs a higher alpha than native Acrylic to keep text readable over desktop content.
            // Invariant: Subtle is most opaque, Clear is least opaque; all stay below full opacity.
            byte popupAlpha = transparencyMode switch
            {
                "Subtle" => 226,
                "Clear" => 152,
                _ => 192
            };
            resources["TemplatePilotPopupSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(popupAlpha, color.R, color.G, color.B));
            // Semantic Anchor: FloatingMenuGlassLegibility
            // SSOT: the selected Glass palette supplies the tint; floating menus use a fixed
            // near-opaque alpha even when ordinary cards or the popup body are highly translucent.
            // Invariant: no transparency setting may reduce floating-menu text readability.
            resources["TemplatePilotFloatingMenuSurfaceBrush"] = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(248, color.R, color.G, color.B));
        }
        else
        {
            resources["TemplatePilotWindowSurfaceBrush"] = System.Windows.Application.Current.TryFindResource("ApplicationBackgroundBrush")
                ?? System.Windows.Media.Brushes.Transparent;
            resources["TemplatePilotCardSurfaceBrush"] = System.Windows.Application.Current.TryFindResource("CardBackgroundFillColorDefaultBrush")
                ?? System.Windows.Media.Brushes.Transparent;
            resources["TemplatePilotPopupSurfaceBrush"] = resources["TemplatePilotWindowSurfaceBrush"];
            resources["TemplatePilotFloatingMenuSurfaceBrush"] = resources["TemplatePilotCardSurfaceBrush"];
        }

        // Semantic Anchor: SearchPopupSelectionContrast
        // SSOT: the selected popup row uses SystemAccentColorPrimaryBrush; choose its
        // foreground once per theme using WCAG contrast (black/white) and share it with
        // the row, cell and generated text. Never infer contrast from the unselected text.
        var accent = (System.Windows.Application.Current.TryFindResource("SystemAccentColorPrimaryBrush")
            as System.Windows.Media.SolidColorBrush)?.Color
            ?? System.Windows.Media.Color.FromRgb(0, 103, 192);
        static double LinearChannel(byte value)
        {
            double srgb = value / 255d;
            return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }
        double accentLuminance = 0.2126 * LinearChannel(accent.R)
            + 0.7152 * LinearChannel(accent.G) + 0.0722 * LinearChannel(accent.B);
        resources["TemplatePilotPopupSelectionTextBrush"] = accentLuminance > 0.179
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.White;

        // Semantic Anchor: WindowBackdropLifecycle
        // Invariants: FluentWindow.WindowBackdropType is the native DWM source; the window's brush
        // remains a DynamicResource after a theme switch. Color overlays remain even while inactive;
        // actual inactive native Acrylic blur is controlled by Windows, not the app.
        if (_watchedWindow is Wpf.Ui.Controls.FluentWindow fluentWindow)
        {
            fluentWindow.WindowBackdropType = enableGlass
                ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
                : Wpf.Ui.Controls.WindowBackdropType.None;

            if (enableGlass && fluentWindow.IsLoaded)
            {
                bool applied = Wpf.Ui.Controls.WindowBackdrop.RemoveBackground(fluentWindow)
                    && Wpf.Ui.Controls.WindowBackdrop.ApplyBackdrop(
                        fluentWindow,
                        Wpf.Ui.Controls.WindowBackdropType.Acrylic);
                if (!applied)
                {
                    _logging.Warning("Glass theme: WPF-UI could not activate native Desktop Acrylic on the main window.");
                }
            }

            fluentWindow.SetResourceReference(
                System.Windows.Controls.Control.BackgroundProperty,
                "TemplatePilotWindowSurfaceBrush");
        }

        // Semantic Anchor: SecondaryWindowsFollowTheme
        // Invariant: existing secondary windows receive theme changes using the same native
        // material decision as newly opened windows, not a separate per-window theme state.
        foreach (var secondaryWindow in _secondaryWindows)
        {
            if (!secondaryWindow.AllowsTransparency)
            {
                secondaryWindow.SetResourceReference(
                    Control.BackgroundProperty, "TemplatePilotWindowSurfaceBrush");
            }
            ApplySecondaryWindowBackdrop(secondaryWindow);
        }

        if (isGlass && !acrylicAvailable)
        {
            _logging.Warning("Glass themes require Windows 11 build 22621 or newer; using opaque theme surfaces.");
        }
        else if (isGlass && System.Windows.SystemParameters.HighContrast)
        {
            _logging.Warning("Glass theme: high contrast is enabled; keeping opaque theme surfaces for accessibility.");
        }
    }
}

public sealed class ApplicationLifetimeService
{
    public bool IsShutdownRequested { get; private set; }

    public void RequestShutdown()
    {
        IsShutdownRequested = true;
        Application.Current.Shutdown();
    }
}


public sealed class WindowPlacementService
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly HashSet<string> _attached = new(StringComparer.OrdinalIgnoreCase);

    public WindowPlacementService(SettingsService settings, LoggingService logging)
    {
        _settings = settings;
        _logging = logging;
    }

    public void Attach(Window window, string key)
    {
        var attachKey = $"{key}:{window.GetHashCode()}";
        if (!_attached.Add(attachKey))
        {
            return;
        }

        Apply(window, key);

        window.LocationChanged += (_, _) => Save(key, window);
        window.SizeChanged += (_, _) => Save(key, window);
        window.StateChanged += (_, _) => Save(key, window);
        window.Closing += (_, _) => Save(key, window);
        window.Closed += (_, _) => Save(key, window);
    }

    private void Apply(Window window, string key)
    {
        if (!_settings.Current.WindowPlacements.TryGetValue(key, out var placement) || !placement.HasBounds)
        {
            return;
        }

        if (!IsPlacementVisible(placement))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = Math.Max(window.MinWidth, placement.Width);
        window.Height = Math.Max(window.MinHeight, placement.Height);

        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void Save(string key, Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            return;
        }

        var bounds = window.WindowState == WindowState.Maximized
            ? window.RestoreBounds
            : new Rect(window.Left, window.Top, window.Width, window.Height);

        if (double.IsNaN(bounds.Left)
            || double.IsNaN(bounds.Top)
            || double.IsNaN(bounds.Width)
            || double.IsNaN(bounds.Height)
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return;
        }

        var placement = new WindowPlacementSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized
        };

        _ = _settings.UpdateAsync(settings =>
        {
            settings.WindowPlacements[key] = placement;
        }, CancellationToken.None).ContinueWith(task =>
        {
            if (task.Exception is not null)
            {
                _logging.Warning("Window placement save failed.", task.Exception);
            }
        }, TaskScheduler.Default);
    }

    private static bool IsPlacementVisible(WindowPlacementSettings placement)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var placementRight = placement.Left + Math.Min(placement.Width, 120);
        var placementBottom = placement.Top + Math.Min(placement.Height, 120);

        return placementRight > virtualLeft
            && placementBottom > virtualTop
            && placement.Left < virtualRight
            && placement.Top < virtualBottom;
    }
}


public sealed class CategoryCatalogService
{
    private readonly AppPaths _paths;
    private readonly LoggingService _logging;
    private readonly TemplateStorageService _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _cache;

    public CategoryCatalogService(AppPaths paths, LoggingService logging, TemplateStorageService storage)
    {
        _paths = paths;
        _logging = logging;
        _storage = storage;
    }

    public event EventHandler? CategoriesChanged;

    private string CategoriesFile => Path.Combine(_paths.SettingsPath, "categories.json");

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            _cache = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(string category, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCategory(category);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var categories = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (categories.Contains(normalized, StringComparer.InvariantCultureIgnoreCase))
            {
                return;
            }

            categories.Add(normalized);
            await SaveUnsafeAsync(categories, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddRangeAsync(IEnumerable<string> categories, CancellationToken cancellationToken)
    {
        var normalized = categories
            .Select(NormalizeCategory)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return;
        }

        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();

            foreach (var category in normalized)
            {
                if (!current.Contains(category, StringComparer.InvariantCultureIgnoreCase))
                {
                    current.Add(category);
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveUnsafeAsync(current, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            CategoriesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RenameAsync(string oldCategory, string newCategory, CancellationToken cancellationToken)
    {
        var oldName = NormalizeCategory(oldCategory);
        var newName = NormalizeCategory(newCategory);

        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.InvariantCultureIgnoreCase))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var categories = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            categories.RemoveAll(x => string.Equals(x, oldName, StringComparison.InvariantCultureIgnoreCase));

            if (!categories.Contains(newName, StringComparer.InvariantCultureIgnoreCase))
            {
                categories.Add(newName);
            }

            await SaveUnsafeAsync(categories, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var templates = await _storage.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (var template in templates.Where(x => string.Equals(x.Metadata.Category, oldName, StringComparison.InvariantCultureIgnoreCase)))
        {
            template.Metadata.Category = newName;
            await _storage.SaveAsync(template, cancellationToken).ConfigureAwait(false);
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(string category, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCategory(category);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var templates = await _storage.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (var template in templates.Where(
                     x => string.Equals(
                         x.Metadata.Category,
                         normalized,
                         StringComparison.InvariantCultureIgnoreCase)))
        {
            template.Metadata.Category = string.Empty;
            await _storage.SaveAsync(template, cancellationToken).ConfigureAwait(false);
        }

        var changed = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var categories = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            changed = categories.RemoveAll(x => string.Equals(x, normalized, StringComparison.InvariantCultureIgnoreCase)) > 0;

            if (changed)
            {
                await SaveUnsafeAsync(categories, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            CategoriesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<IReadOnlyList<string>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CategoriesFile))
            {
                return Array.Empty<string>();
            }

            var json = await File.ReadAllTextAsync(CategoriesFile, AtomicFileWriter.Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            var catalog = JsonSerializer.Deserialize<CategoryCatalog>(json, JsonOptions.Default) ?? new CategoryCatalog();
            return SortCategories(catalog.Categories);
        }
        catch (Exception exception)
        {
            _logging.Warning("Category catalog could not be loaded.", exception);
            return Array.Empty<string>();
        }
    }

    private async Task SaveUnsafeAsync(IEnumerable<string> categories, CancellationToken cancellationToken)
    {
        var catalog = new CategoryCatalog
        {
            Categories = SortCategories(categories).ToList()
        };

        var json = JsonSerializer.Serialize(catalog, JsonOptions.Default);
        await AtomicFileWriter.WriteTextAtomicAsync(CategoriesFile, json, cancellationToken).ConfigureAwait(false);
        _cache = catalog.Categories;
    }

    private static IReadOnlyList<string> SortCategories(IEnumerable<string> categories)
    {
        return categories
            .Select(NormalizeCategory)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeCategory(string? category)
    {
        return (category ?? string.Empty).Trim();
    }
}

public sealed class CrashRecoveryService
{
    private readonly AppPaths _paths;

    public CrashRecoveryService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task SaveRecoveryAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.CrashRecoveryPath);
        var file = Path.Combine(_paths.CrashRecoveryPath, package.Metadata.Id + ".recovery.json");
        var json = JsonSerializer.Serialize(package, JsonOptions.Default);
        await AtomicFileWriter.WriteTextAtomicAsync(file, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplatePackage?> TryLoadRecoveryAsync(string templateId, CancellationToken cancellationToken)
    {
        var file = Path.Combine(_paths.CrashRecoveryPath, templateId + ".recovery.json");

        if (!File.Exists(file))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(file, AtomicFileWriter.Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TemplatePackage>(json, JsonOptions.Default);
    }

    public Task ClearRecoveryAsync(string templateId)
    {
        var file = Path.Combine(_paths.CrashRecoveryPath, templateId + ".recovery.json");
        AtomicFileWriter.TryDelete(file);
        return Task.CompletedTask;
    }

    public void DeleteStaleRecoveryFiles()
    {
        if (!Directory.Exists(_paths.CrashRecoveryPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.CrashRecoveryPath, "*.recovery.json"))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
            {
                info.Delete();
            }
        }
    }
}

public sealed class FileDialogService
{
    private readonly LocalizationService _localization;

    public FileDialogService(LocalizationService localization)
    {
        _localization = localization;
    }

    public string? ShowOpenTemplateOrZipDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = _localization["OpenTemplateFilter"],
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowExportZipDialog()
    {
        var dialog = new SaveFileDialog
        {
            Filter = _localization["ExportZipFilter"],
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = AppConstants.AppName + "_export.zip"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowTemplatesFolderDialog(string initialPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization["TemplatesFolder"],
            SelectedPath = Directory.Exists(initialPath) ? initialPath : string.Empty,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}

public sealed class FileLockService
{
    public bool IsLocked(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public async Task WaitUntilAvailableAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;

        while (IsLocked(path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - start > timeout)
            {
                throw new IOException($"File is locked: {path}");
            }

            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class LocalizationService
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? LanguageChanged;

    public LocalizationService()
    {
        _languages["en"] = CreateEnglishFallback();
        LoadExternalLanguages();
        SetLanguage("en");
    }

    public IReadOnlyList<string> AvailableLanguages =>
        _languages.Keys.OrderBy(
            value => value,
            StringComparer.OrdinalIgnoreCase).ToList();

    public string CurrentLanguage { get; private set; } = "en";

    public string this[string key]
    {
        get
        {
            if (_current.TryGetValue(key, out string? value))
            {
                return value;
            }

            return _languages["en"].TryGetValue(key, out string? fallback)
                ? fallback
                : key;
        }
    }

    public string GetLanguageDisplayName(string languageCode)
    {
        if (_languages.TryGetValue(languageCode, out Dictionary<string, string>? language) &&
            language.TryGetValue("LanguageName", out string? displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return languageCode.ToUpperInvariant();
    }

    public string GetLanguageCode(string displayName)
    {
        foreach ((string code, Dictionary<string, string> language) in _languages)
        {
            if (language.TryGetValue("LanguageName", out string? name) &&
                string.Equals(name, displayName, StringComparison.CurrentCultureIgnoreCase))
            {
                return code;
            }
        }

        return "en";
    }

    public void SetLanguage(string language)
    {
        if (!_languages.TryGetValue(language, out Dictionary<string, string>? dictionary))
        {
            dictionary = _languages["en"];
            language = "en";
        }

        CurrentLanguage = language;
        _current = dictionary;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadExternalLanguages()
    {
        string languageDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Languages");

        Directory.CreateDirectory(languageDirectory);

        foreach (string filePath in Directory.EnumerateFiles(
            languageDirectory,
            "lang_*.json",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = File.ReadAllText(
                    filePath,
                    AtomicFileWriter.Utf8WithoutBom);

                Dictionary<string, string>? values =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        json,
                        JsonOptions.Default);

                if (values is null)
                {
                    continue;
                }

                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string languageCode = fileName["lang_".Length..].Trim();

                if (string.IsNullOrWhiteSpace(languageCode) ||
                    string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _languages[languageCode] =
                    new Dictionary<string, string>(
                        values,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    private static Dictionary<string, string> CreateEnglishFallback()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Templates"] = "Templates",
            ["Search"] = "Search",
            ["New"] = "New",
            ["Edit"] = "Edit",
            ["Delete"] = "Delete",
            ["Duplicate"] = "Duplicate",
            ["Import"] = "Import",
            ["Export"] = "Export",
            ["Settings"] = "Settings",
            ["Shortcut"] = "Shortcut",
            ["Save"] = "Save",
            ["Cancel"] = "Cancel",
            ["Yes"] = "Yes",
            ["No"] = "No",
            ["CaptureClipboard"] = "Capture clipboard",
            ["PlainText"] = "Plain text",
            ["RichText"] = "Rich text",
            ["Html"] = "HTML",
            ["Images"] = "Images",
            ["Preview"] = "Preview",
            ["Key"] = "Shortcut",
            ["Name"] = "Name",
            ["Category"] = "Category",
            ["Language"] = "Language",
            ["StartWithWindows"] = "Start with Windows",
            ["MinimizeToTray"] = "Minimize to tray",
            ["CloseToTray"] = "Close to tray",
            ["SearchPopupVisibleLines"] = "Visible text lines per search result",
            ["SearchPopupFontSize"] = "Search popup font size",
            ["SearchPopupBlur"] = "Shortcut popup glass blur strength",
            ["SearchPopupBlurPreview"] = "Move the slider to open the actual shortcut window as a live preview.",
            ["SearchPopupPreviewPosition"] = "Shortcut preview position",
            ["SearchPopupPreviewRight"] = "Right",
            ["SearchPopupPreviewBelow"] = "Below",
            ["MainWindowFontSize"] = "Main window font size",
            ["UserName"] = "Name",
            ["Company"] = "Company",
            ["NoTemplateSelected"] = "No template selected",
            ["SearchPopupHint"] = "Type to filter templates",
            ["Insert"] = "Insert",
            ["ConfirmDelete"] = "Delete selected template?",
            ["ConfirmDeleteTemplateNamed"] = "Delete template \"{0}\"?",
            ["ConfirmDeleteTemplatesCount"] = "Delete {0} selected templates?",
            ["Error"] = "Error",
            ["HotkeyConflict"] = "The configured shortcut is already used by another application.",
            ["TemplateKeyRequired"] = "Shortcut is required.",
            ["TemplateNameRequired"] = "Template name is required.",
            ["ExportCompleted"] = "Export completed.",
            ["ImportCompleted"] = "Import completed.",
            ["Categories"] = "Categories",
            ["NewCategory"] = "New",
            ["SaveCategory"] = "Save",
            ["DeleteCategory"] = "Delete",
            ["ClearCategoryFilter"] = "Clear category filter",
            ["AddCategory"] = "Add category",
            ["CategoryInUse"] = "The category is still used by at least one template.",
            ["ConfirmDeleteCategory"] = "Delete the selected category? Templates using it will remain and become uncategorized.",
            ["ConfirmDeleteCategoryNamed"] = "Delete category \"{0}\"? Templates using it will remain and become uncategorized.",
            ["Exit"] = "Exit",
            ["RemoveImage"] = "Remove image",
            ["DragDropImage"] = "Drag and drop image files here.",
            ["Undo"] = "Undo",
            ["Redo"] = "Redo",
            ["CategoryFilter"] = "Category filter",
            ["AllCategories"] = "All categories",
            ["SelectedCategories"] = "{0} categories",
            ["Editor"] = "Editor",
            ["About"] = "About",
            ["Theme"] = "Theme",
            ["ThemeSystem"] = "System",
            ["ThemeLight"] = "Light",
            ["ThemeDark"] = "Dark",
            ["ThemeLiquidGlass"] = "Liquid Glass",
            ["ThemeGlassBlue"] = "Blue Glass",
            ["ThemeGlassCrystal"] = "Crystal Glass",
            ["ThemeGlassGraphite"] = "Graphite Glass",
            ["ThemeGlassViolet"] = "Violet Glass",
            ["GlassTransparency"] = "Glass transparency",
            ["GlassTransparencySubtle"] = "Subtle",
            ["GlassTransparencyStandard"] = "Standard",
            ["GlassTransparencyClear"] = "Clear",
            ["SettingsDescription"] = "Configure language, theme, shortcuts, and startup behavior.",
            ["TemplatesFolder"] = "Templates folder",
            ["ChooseFolder"] = "Choose folder",
            ["AboutDescription"] = "helps you manage and insert reusable content efficiently.",
            ["Version"] = "Version",
            ["License"] = "License",
            ["Repository"] = "Open GitHub repository",
            ["Releases"] = "Open releases",
            ["CheckForUpdates"] = "Check for updates",
            ["OpenRelease"] = "Open release",
            ["OK"] = "OK",
            ["CheckingForUpdates"] = "Checking for updates...",
            ["UpToDate"] = "You are using the latest version.",
            ["UpdateAvailable"] = "Version {0} is available.",
            ["UpdateAvailableMessage"] = "Version {0} is available. Open About to view the release.",
            ["UpdateCheckFailed"] = "Update information is currently unavailable.",
            ["OpenLinkFailed"] = "The link could not be opened.",
            ["Ctrl"] = "Ctrl",
            ["Shift"] = "Shift",
            ["Alt"] = "Alt",
            ["Win"] = "Win",
            ["EscapeKey"] = "ESC",
            ["LanguageName"] = "English",
            ["Navigation"] = "Navigation",
            ["Appearance"] = "Appearance",
            ["General"] = "General",
            ["ShortcutPopupSettings"] = "Shortcut popup",
            ["RestoreRecovery"] = "Restore unsaved editor recovery?",
            ["OpenTemplateFilter"] = "Template packages (*.tplpack;*.zip)|*.tplpack;*.zip|All files (*.*)|*.*",
            ["ExportZipFilter"] = "ZIP export (*.zip)|*.zip",
        };
    }
}

public sealed class LoggingService
{
    private readonly AppPaths _paths;
    private readonly object _sync = new();

    public LoggingService(AppPaths paths)
    {
        _paths = paths;
        RotateLogs();
    }

    public void Info(string message)
    {
        Write("INFO", message, null);
    }

    public void Warning(string message, Exception? exception = null)
    {
        Write("WARN", message, exception);
    }

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            Directory.CreateDirectory(_paths.LogsPath);
            File.AppendAllText(GetCurrentLogPath(), line + Environment.NewLine, AtomicFileWriter.Utf8WithoutBom);
        }
    }

    private string GetCurrentLogPath()
    {
        return Path.Combine(_paths.LogsPath, $"{DateTime.Now:yyyyMMdd}.log");
    }

    private void RotateLogs()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsPath);

            foreach (var file in Directory.EnumerateFiles(_paths.LogsPath, "*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14) || info.Length > 10 * 1024 * 1024)
                {
                    info.Delete();
                }
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Semantic Anchor: ApplicationMessageRoutingSSOT
/// SSOT: Every application-owned information, error and confirmation message goes
/// through ThemedDialogWindow; native Windows file/folder pickers remain OS-owned.
/// Invariants: messages retain their text and localization, and a confirmation
/// only succeeds on an explicit affirmative action.
/// </summary>
public sealed class MessageBoxService
{
    private readonly LocalizationService _localization;

    public MessageBoxService(LocalizationService localization)
    {
        _localization = localization;
    }

    public bool Confirm(string message)
    {
        return ThemedDialogWindow.ShowMessage(ThemedDialogWindow.FindOwner(),
            AppConstants.AppName, message, _localization["Yes"], _localization["No"], symbol: "?");
    }

    public void Error(string message)
    {
        ThemedDialogWindow.ShowMessage(ThemedDialogWindow.FindOwner(),
            _localization["Error"], message, null, _localization["OK"], symbol: "!");
    }

    public void Info(string message)
    {
        ThemedDialogWindow.ShowMessage(ThemedDialogWindow.FindOwner(),
            AppConstants.AppName, message, null, _localization["OK"], symbol: "i");
    }
}

/// <summary>
/// Semantic Anchor: CategoryDialogThemeAndInputContract
/// SSOT: ThemedDialogWindow owns the modal chrome and palette for category input;
/// LocalizationService supplies its existing labels and validation remains local.
/// Invariants: no empty category is accepted, Escape cancels, existing text is
/// selected on opening, and the active window remains the modal owner.
/// </summary>
public sealed class CategoryDialogService
{
    private readonly LocalizationService _localization;
    private readonly AppThemeService _themeService;

    public CategoryDialogService(LocalizationService localization, AppThemeService themeService)
    {
        _localization = localization;
        _themeService = themeService;
    }

    public string? ShowCategoryDialog(string title, string initialValue)
    {
        // AppThemeService owns the shared resources used by ThemedDialogWindow.
        // Keep its injected dependency to preserve the existing service contract.
        var owner = ThemedDialogWindow.FindOwner();
        var window = new ThemedDialogWindow(title, owner, width: 390, height: 215);
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 220,
            Height = 28,
            MinHeight = 22,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var okButton = new Wpf.Ui.Controls.Button
        {
            Content = _localization["Save"],
            MinWidth = 90,
            Height = 30,
            IsDefault = true
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = _localization["Cancel"],
            MinWidth = 90,
            Height = 30,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = _localization["Category"],
            Foreground = window.DialogTextBrush,
            Margin = new Thickness(0, 0, 0, 6)
        });
        content.Children.Add(input);
        content.Children.Add(buttons);
        window.Body.Children.Add(content);

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                input.Focus();
                input.SelectAll();
                return;
            }

            window.DialogResult = true;
        };
        cancelButton.Click += (_, _) => window.DialogResult = false;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}


public sealed partial class PlaceholderService
{
    private readonly SettingsService _settings;

    public PlaceholderService(SettingsService settings)
    {
        _settings = settings;
    }

    public string ApplyToText(string text)
    {
        return Replace(text, rtf: false);
    }

    public string ApplyToHtml(string html)
    {
        return Replace(html, rtf: false);
    }

    public string ApplyToRtf(string rtf)
    {
        return Replace(rtf, rtf: true);
    }

    private string Replace(string text, bool rtf)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Datum"] = DateTime.Now.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
            ["Uhrzeit"] = DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
            ["Name"] = _settings.Current.UserName,
            ["Firma"] = _settings.Current.CompanyName
        };

        return PlaceholderRegex().Replace(text, match =>
        {
            var key = match.Groups["name"].Value;
            if (!values.TryGetValue(key, out var value))
            {
                return match.Value;
            }

            return rtf ? RtfHelper.EscapeText(value) : value;
        });
    }

    [GeneratedRegex("\\{(?<name>Datum|Uhrzeit|Name|Firma)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

public sealed class SearchPopupService
{
    private readonly TemplateResolver _resolver;
    private readonly LocalizationService _localization;
    private readonly ForegroundWindowService _foreground;
    private readonly LoggingService _logging;
    private readonly WindowPlacementService _windowPlacement;
    private readonly SettingsService _settings;
    private readonly CategoryCatalogService _categories;
    private readonly AppThemeService _themeService;
    private SearchPopupWindow? _settingsPreview;
    private SearchPopupViewModel? _settingsPreviewViewModel;
    // Semantic Anchor: PendingShortcutPopupRuntimeSettings
    // SSOT: one in-memory override for visual popup settings; never writes configuration.
    // SaveSettingsAsync remains the only writer of these settings to AppSettings.
    private (int BlurStrength, int FontSize, int VisibleLines, string Position)? _runtimeVisualSettings;

    public void SetPendingPopupVisualSettings(int blurStrength, int fontSize,
        int visibleLines, string previewPosition)
    {
        _runtimeVisualSettings = (Math.Clamp(blurStrength, 0, 32),
            Math.Clamp(fontSize, 10, 20), Math.Clamp(visibleLines, 1, 10),
            string.Equals(previewPosition, "Below", StringComparison.OrdinalIgnoreCase) ? "Below" : "Right");
    }

    public SearchPopupService(
        TemplateResolver resolver,
        LocalizationService localization,
        ForegroundWindowService foreground,
        LoggingService logging,
        WindowPlacementService windowPlacement,
        SettingsService settings,
        CategoryCatalogService categories,
        AppThemeService themeService)
    {
        _themeService = themeService;
        _resolver = resolver;
        _localization = localization;
        _foreground = foreground;
        _logging = logging;
        _windowPlacement = windowPlacement;
        _settings = settings;
        _categories = categories;
    }

    public async Task<TemplatePackage?> ShowAsync(WindowTargetContext target, string initialQuery, CancellationToken cancellationToken)
    {
        CloseSettingsBlurPreview();
        var viewModel = new SearchPopupViewModel(_localization, _settings, initialQuery);
        if (_runtimeVisualSettings is { } runtime)
        {
            viewModel.ApplySettingsPreview(runtime.VisibleLines, runtime.FontSize, runtime.Position);
        }
        var window = new SearchPopupWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            // Semantic Anchor: SearchPopupLayeredWindowCreation
            // Invariant: set per-pixel transparency before the first HWND is created;
            // the decision follows AppThemeService and does not depend on focus.
            AllowsTransparency = _themeService.IsLayeredPopupGlassActive
        };
        window.SetBackdropBlur(_runtimeVisualSettings?.BlurStrength ?? _settings.Current.SearchPopupBlurRadius);
        // Semantic Anchor: SearchPopupThemeRegistration
        // Invariant: the popup uses the current shared app theme before its first Show.
        _themeService.RegisterSecondaryWindow(window);
        _windowPlacement.Attach(window, "SearchPopupWindow");
        var tcs = new TaskCompletionSource<TemplatePackage?>(TaskCreationOptions.RunContinuationsAsynchronously);

        viewModel.RequestClose += (_, template) =>
        {
            tcs.TrySetResult(template);
            window.Close();
        };

        window.Closed += (_, _) => tcs.TrySetResult(null);

        window.Show();
        window.Activate();

        _ = window.Dispatcher.InvokeAsync(
            new Action(() => _ = LoadTemplatesIntoPopupAsync(viewModel, window, cancellationToken)),
            DispatcherPriority.ContextIdle);

        using var registration = cancellationToken.Register(() =>
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
                window.Close();
            }));
        });

        try
        {
            return await tcs.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _foreground.TryRestore(target);
        }
    }

    // Semantic Anchor: LiveShortcutPopupSettingsPreview
    // SSOT: the same SearchPopupWindow is used for hotkey invocation and for settings.
    // Invariants: exactly one modeless non-activating preview while the popup-settings
    // tab is visible; unsaved visual values never mutate AppSettings, and never insert.
    public void ShowSettingsPopupPreview(Window owner, int blurStrength, int fontSize,
        int visibleLines, string previewPosition)
    {
        if (_settingsPreview is { IsVisible: true })
        {
            _settingsPreview.ApplySettingsPreview(visibleLines, fontSize, previewPosition, blurStrength);
            return;
        }

        // Reuse the preview ViewModel for this service lifetime; its localization event
        // subscription must not grow with every slider-preview opening.
        var viewModel = _settingsPreviewViewModel ??= new SearchPopupViewModel(_localization, _settings, string.Empty);
        var window = new SearchPopupWindow(viewModel)
        {
            IsSettingsPreview = true,
            AllowsTransparency = _themeService.IsLayeredPopupGlassActive,
            Owner = owner,
            ShowActivated = false,
            Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        // Position beside the settings window whenever possible, so the slider stays usable.
        // User placement of the real hotkey window is never read or overwritten here.
        var screen = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle).WorkingArea;
        var source = PresentationSource.FromVisual(owner);
        var pixelsToDip = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = pixelsToDip.Transform(new Point(screen.Left, screen.Top));
        var bottomRight = pixelsToDip.Transform(new Point(screen.Right, screen.Bottom));
        double right = owner.Left + owner.ActualWidth + 12;
        double left = owner.Left - window.Width - 12;
        window.Left = right + window.Width <= bottomRight.X ? right
            : left >= topLeft.X ? left
            : Math.Max(topLeft.X, bottomRight.X - window.Width - 12);
        window.Top = Math.Clamp(owner.Top + 16, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - window.Height));

        window.ApplySettingsPreview(visibleLines, fontSize, previewPosition, blurStrength);
        // The preview's visual tree is noninteractive; unlike the real popup it
        // has no RequestClose handler and cannot trigger template insertion.
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsPreview, window))
            {
                _settingsPreview = null;
            }
        };

        _settingsPreview = window;
        _themeService.RegisterSecondaryWindow(window);
        window.Show();
        _ = window.Dispatcher.InvokeAsync(
            new Action(() => _ = LoadTemplatesIntoPopupAsync(viewModel, window, CancellationToken.None)),
            DispatcherPriority.ContextIdle);
    }

    public void CloseSettingsBlurPreview()
    {
        var window = _settingsPreview;
        _settingsPreview = null;
        if (window is { IsVisible: true })
        {
            window.Close();
        }
    }

    private async Task LoadTemplatesIntoPopupAsync(SearchPopupViewModel viewModel, SearchPopupWindow window, CancellationToken cancellationToken)
    {
        try
        {
            var templatesTask = _resolver.SearchMetadataAsync(string.Empty, cancellationToken);
            var categoriesTask = _categories.GetCategoriesAsync(cancellationToken);

            await Task.WhenAll(templatesTask, categoriesTask).ConfigureAwait(false);

            var templates = await templatesTask.ConfigureAwait(false);
            var categories = (await categoriesTask.ConfigureAwait(false))
                .Concat(templates.Select(x => x.Metadata.Category))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            await window.Dispatcher.InvokeAsync(() =>
            {
                if (!window.IsLoaded || !window.IsVisible)
                {
                    return;
                }

                viewModel.SetCategories(categories);
                viewModel.SetTemplates(templates);
            }, DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logging.Warning("Template loading for popup failed.", exception);
        }
    }
}

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                Current = new AppSettings();
                await SaveCurrentUnsafeAsync(cancellationToken).ConfigureAwait(false);
                return Current.Clone();
            }

            var json = await File.ReadAllTextAsync(_paths.SettingsFile, AtomicFileWriter.Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions.Default) ?? new AppSettings();
            Migrate(Current);
            await SaveCurrentUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return Current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Migrate(settings);
            Current = settings.Clone();
            await SaveCurrentUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var next = Current.Clone();
            update(next);
            Migrate(next);
            Current = next;
            await SaveCurrentUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCurrentUnsafeAsync(CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions.Default);
        await AtomicFileWriter.WriteTextAtomicAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
    }

    private static void Migrate(AppSettings settings)
    {
        // Semantic Anchor: LegacyMainFontDefaultMigration
        // SSOT: MainWindowFontSize is stored only in AppSettings and is controlled by the existing settings slider.
        // Invariant: on first migration from schema v1, the former 12-DIP starting value becomes 13 DIP;
        // all other saved values stay unchanged, and a user-selected 12 DIP is allowed after migration.
        if (settings.SettingsVersion < 2 && settings.MainWindowFontSize == 12)
        {
            settings.MainWindowFontSize = 13;
        }

        if (settings.SettingsVersion < AppConstants.CurrentSettingsVersion)
        {
            settings.SettingsVersion = AppConstants.CurrentSettingsVersion;
        }

        settings.Shortcut ??= HotkeySettings.Default();
        settings.PopupCategoryFilter ??= new List<string>();
        settings.SearchPopupColumns ??= new List<SearchPopupColumnSettings>();
        settings.SearchPopupVisibleLines = Math.Clamp(settings.SearchPopupVisibleLines, 1, 10);
        settings.SearchPopupFontSize = Math.Clamp(settings.SearchPopupFontSize, 10, 20);
        // Semantic Anchor: ShortcutBackdropBlurValidation
        // An additive JSON setting is backward compatible: missing values retain the model default.
        settings.SearchPopupBlurRadius = Math.Clamp(settings.SearchPopupBlurRadius, 0, 32);
        // Semantic Anchor: SearchPopupPreviewLayoutValidation
        // SSOT: AppSettings supplies the same layout to hotkey windows and settings previews.
        // Invariants: old JSON defaults to Right; malformed sizes/positions cannot
        // produce invalid GridLength values or inaccessible preview panes.
        settings.SearchPopupPreviewPosition = string.Equals(settings.SearchPopupPreviewPosition, "Below", StringComparison.OrdinalIgnoreCase)
            ? "Below" : "Right";
        settings.SearchPopupPreviewRightRatio = double.IsFinite(settings.SearchPopupPreviewRightRatio)
            ? Math.Clamp(settings.SearchPopupPreviewRightRatio, 0.20, 0.75) : 0.34;
        settings.SearchPopupPreviewBottomRatio = double.IsFinite(settings.SearchPopupPreviewBottomRatio)
            ? Math.Clamp(settings.SearchPopupPreviewBottomRatio, 0.20, 0.75) : 0.40;
        // Semantic Anchor: MainWindowTypographyValidation | Invariant: both persisted and newly saved sizes stay within the supported range.
        settings.MainWindowFontSize = Math.Clamp(settings.MainWindowFontSize, 10, 20);
        settings.PopupCategoryFilter = settings.PopupCategoryFilter
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
        settings.WindowPlacements ??= new Dictionary<string, WindowPlacementSettings>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(settings.Shortcut.Key))
        {
            settings.Shortcut.Key = "M";
        }

        if (settings.ClipboardTimeoutMs < 250)
        {
            settings.ClipboardTimeoutMs = 250;
        }

        if (settings.ClipboardRetryDelayMs < 10)
        {
            settings.ClipboardRetryDelayMs = 10;
        }

        if (settings.ClipboardRestoreDelayMs < 0)
        {
            settings.ClipboardRestoreDelayMs = 250;
        }

        if (settings.MaxImageBytes < 1024 * 1024)
        {
            settings.MaxImageBytes = 1024 * 1024;
        }

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            settings.Language = "en";
        }

        // Semantic Anchor: ThemeSettingsValidation
        // SSOT: The same stable codes are used in AppThemeService, UI mappings and persistence.
        // Invariants: legacy codes and every Glass variant survive save/load; unknown codes fall back to System.
        if (!string.Equals(settings.Theme, "System", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase) &&
            !AppThemeService.IsGlassTheme(settings.Theme))
        {
            settings.Theme = "System";
        }

        // Invariant: transparency levels are independent of theme selection and only defined codes persist.
        if (settings.GlassTransparency != "Subtle" &&
            settings.GlassTransparency != "Standard" &&
            settings.GlassTransparency != "Clear")
        {
            settings.GlassTransparency = "Standard";
        }

        settings.TemplatesPath = settings.TemplatesPath?.Trim() ?? string.Empty;
    }
}

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly LoggingService _logging;

    public StartupService(LoggingService logging)
    {
        _logging = logging;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppConstants.AppName) is string value && value.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(AppConstants.AppName, Quote(GetExecutablePath()) + " --minimized", RegistryValueKind.String);
            _logging.Info("Startup registry entry enabled.");
        }
        else
        {
            key.DeleteValue(AppConstants.AppName, throwOnMissingValue: false);
            _logging.Info("Startup registry entry disabled.");
        }
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed class TrayService : IDisposable
{
    private readonly LocalizationService _localization;
    private readonly LoggingService _logging;
    private Forms.NotifyIcon? _notifyIcon;
    private Window? _mainWindow;

    public TrayService(LocalizationService localization, LoggingService logging)
    {
        _localization = localization;
        _logging = logging;
    }

    public void Initialize(Window mainWindow, Action exitAction)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AppConstants.AppName,
            Icon = IconResourceHelper.LoadApplicationIcon(),
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(AppConstants.AppName, null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_localization["Exit"], null, (_, _) => exitAction());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        _localization.LanguageChanged += OnLanguageChanged;

        _logging.Info("Tray icon initialized.");
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void HideMainWindow()
    {
        _mainWindow?.Hide();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_notifyIcon?.ContextMenuStrip is { Items.Count: > 0 } menu)
        {
            menu.Items[0].Text = AppConstants.AppName;
            if (menu.Items.Count > 2)
            {
                menu.Items[2].Text = _localization["Exit"];
            }
        }
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;

        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}
