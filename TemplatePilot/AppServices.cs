using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using TemplatePilot.Infrastructure;
using TemplatePilot.Models;
using Microsoft.Win32;
using System.Reflection;
using System.Text.RegularExpressions;
using TemplatePilot.Helpers;
using TemplatePilot.ViewModels;
using TemplatePilot.Views;
using Forms = System.Windows.Forms;

namespace TemplatePilot.Services;

public sealed class AppThemeService
{
    public void ApplyDefaultTheme()
    {
        Application.Current.Resources["ThemeAppliedUtc"] = DateTime.UtcNow;
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

        var templates = await _storage.LoadMetadataAllAsync(cancellationToken).ConfigureAwait(false);
        if (templates.Any(x => string.Equals(x.Metadata.Category, normalized, StringComparison.InvariantCultureIgnoreCase)))
        {
            throw new InvalidOperationException("Category is used by at least one template.");
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
    public string? ShowOpenTemplateOrZipDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Template packages (*.tplpack;*.zip)|*.tplpack;*.zip|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowExportZipDialog()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP export (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = AppConstants.AppName + "_export.zip"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
    private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? LanguageChanged;

    public LocalizationService()
    {
        LoadEmbeddedLanguages();
        SetLanguage("en");
    }

    public IReadOnlyList<string> AvailableLanguages => _languages.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    public string CurrentLanguage { get; private set; } = "en";

    public string this[string key]
    {
        get
        {
            if (_current.TryGetValue(key, out var value))
            {
                return value;
            }

            return _languages.TryGetValue("en", out var english) && english.TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public void SetLanguage(string language)
    {
        if (!_languages.TryGetValue(language, out var dictionary))
        {
            dictionary = _languages["en"];
            language = "en";
        }

        CurrentLanguage = language;
        _current = dictionary;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadEmbeddedLanguages()
    {
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames().Where(x => x.EndsWith("_lang.json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, AtomicFileWriter.Utf8WithoutBom);
            var json = reader.ReadToEnd();
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions.Default) ?? new Dictionary<string, string>();
            var file = name.Split('.').Reverse().Skip(1).FirstOrDefault() ?? "en_lang";
            var language = file.Replace("_lang", string.Empty, StringComparison.OrdinalIgnoreCase);
            _languages[language] = new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        if (!_languages.ContainsKey("en"))
        {
            _languages["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppTitle"] = AppConstants.AppName
            };
        }
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

public sealed class MessageBoxService
{
    private readonly LocalizationService _localization;

    public MessageBoxService(LocalizationService localization)
    {
        _localization = localization;
    }

    public bool Confirm(string message)
    {
        return MessageBox.Show(message, _localization["AppTitle"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void Error(string message)
    {
        MessageBox.Show(message, _localization["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void Info(string message)
    {
        MessageBox.Show(message, _localization["AppTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
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

    public SearchPopupService(
        TemplateResolver resolver,
        LocalizationService localization,
        ForegroundWindowService foreground,
        LoggingService logging,
        WindowPlacementService windowPlacement,
        SettingsService settings,
        CategoryCatalogService categories)
    {
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
        var viewModel = new SearchPopupViewModel(_localization, _settings, initialQuery);
        var window = new SearchPopupWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
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
        if (settings.SettingsVersion < AppConstants.CurrentSettingsVersion)
        {
            settings.SettingsVersion = AppConstants.CurrentSettingsVersion;
        }

        settings.Shortcut ??= HotkeySettings.Default();
        settings.PopupCategoryFilter ??= new List<string>();
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
        menu.Items.Add(_localization["AppTitle"], null, (_, _) => ShowMainWindow());
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
            menu.Items[0].Text = _localization["AppTitle"];
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
