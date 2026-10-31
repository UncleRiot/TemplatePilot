using System.IO;
using TemplatePilot.Infrastructure;
using TemplatePilot.Models;
using TemplatePilot.ViewModels;
using TemplatePilot.Views;
using TemplatePilot.Helpers;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Text;

namespace TemplatePilot.Services;

public sealed class TemplateCacheService : IDisposable
{
    private readonly TemplateStorageService _storage;
    private readonly AppPaths _paths;
    private readonly LoggingService _logging;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private FileSystemWatcher _watcher;
    private IReadOnlyList<TemplatePackage>? _cache;
    private IReadOnlyList<TemplatePackage>? _metadataCache;
    private CancellationTokenSource? _debounce;

    public event EventHandler? TemplatesChanged;

    public TemplateCacheService(TemplateStorageService storage, AppPaths paths, LoggingService logging)
    {
        _storage = storage;
        _paths = paths;
        _logging = logging;
        Directory.CreateDirectory(_paths.TemplatesPath);
        _watcher = CreateWatcher();
    }

    public async Task<IReadOnlyList<TemplatePackage>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _cache ??= await _storage.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TemplatePackage>> GetTemplateMetadataAsync(CancellationToken cancellationToken)
    {
        if (_metadataCache is not null)
        {
            return _metadataCache;
        }

        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _metadataCache ??= await _storage.LoadMetadataAllAsync(cancellationToken).ConfigureAwait(false);
            return _metadataCache;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task<IReadOnlyList<TemplatePackage>> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _cache = await _storage.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            _metadataCache = null;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _cache = null;
        _metadataCache = null;
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangeTemplatesPath()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        Directory.CreateDirectory(_paths.TemplatesPath);
        _watcher = CreateWatcher();
        _cache = null;
        _metadataCache = null;
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_paths.TemplatesPath, "*" + AppConstants.TemplateExtension)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };

        watcher.Created += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileChanged;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    _logging.Info("Template cache invalidated by file watcher.");
                    Invalidate();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _watcher.Dispose();
        _metadataGate.Dispose();
        _gate.Dispose();
    }
}

public sealed class TemplateEditorDialogService
{
    private readonly TemplateEditorService _editorService;
    private readonly LocalizationService _localization;
    private readonly CrashRecoveryService _crashRecovery;
    private readonly SettingsService _settings;
    private readonly MessageBoxService _messages;
    private readonly LoggingService _logging;
    private readonly CategoryCatalogService _categories;
    private readonly WindowPlacementService _windowPlacement;
    private readonly CategoryDialogService _categoryDialog;
    private readonly AppThemeService _themeService;

    public TemplateEditorDialogService(
        TemplateEditorService editorService,
        LocalizationService localization,
        CrashRecoveryService crashRecovery,
        SettingsService settings,
        MessageBoxService messages,
        LoggingService logging,
        CategoryCatalogService categories,
        WindowPlacementService windowPlacement,
        CategoryDialogService categoryDialog,
        AppThemeService themeService)
    {
        _themeService = themeService;
        _editorService = editorService;
        _localization = localization;
        _crashRecovery = crashRecovery;
        _settings = settings;
        _messages = messages;
        _logging = logging;
        _categories = categories;
        _windowPlacement = windowPlacement;
        _categoryDialog = categoryDialog;
    }

    public async Task<TemplateEditorViewModel> CreateViewModelAsync(
        TemplatePackage? package,
        CancellationToken cancellationToken)
    {
        var workingCopy = package is null
            ? _editorService.CreateNew()
            : new TemplatePackage
            {
                Metadata = new TemplateMetadata
                {
                    Id = package.Metadata.Id,
                    FormatVersion = package.Metadata.FormatVersion,
                    AppVersion = package.Metadata.AppVersion,
                    Key = package.Metadata.Key,
                    Name = package.Metadata.Name,
                    Category = package.Metadata.Category,
                    Language = package.Metadata.Language,
                    ClipboardFormats = package.Metadata.ClipboardFormats.ToList(),
                    CreatedUtc = package.Metadata.CreatedUtc,
                    ModifiedUtc = package.Metadata.ModifiedUtc
                },
                Content = package.Content.Clone(),
                FilePath = package.FilePath
            };

        if (package is not null)
        {
            try
            {
                var recovered = await _crashRecovery.TryLoadRecoveryAsync(
                    package.Metadata.Id,
                    cancellationToken).ConfigureAwait(true);

                if (recovered is not null && _messages.Confirm(_localization["RestoreRecovery"]))
                {
                    workingCopy = recovered;
                    workingCopy.FilePath = package.FilePath;
                }
            }
            catch (Exception exception)
            {
                _logging.Warning("Crash recovery load failed.", exception);
            }
        }

        return new TemplateEditorViewModel(
            workingCopy,
            _editorService,
            _localization,
            _crashRecovery,
            _settings,
            _messages,
            _logging,
            _categories,
            _categoryDialog);
    }

    public async Task<bool> EditAsync(TemplatePackage? package, CancellationToken cancellationToken)
    {
        var viewModel = await CreateViewModelAsync(package, cancellationToken).ConfigureAwait(true);
        var window = new TemplateEditorWindow(viewModel);
        // Semantic Anchor: TemplateEditorWindowThemeRegistration
        // Invariant: modal editor uses the same Glass palette and native backdrop policy.
        _themeService.RegisterSecondaryWindow(window);
        _windowPlacement.Attach(window, "TemplateEditorWindow");
        var result = window.ShowDialog() == true;
        viewModel.Dispose();
        return result;
    }
}

public sealed class TemplateEditorService
{
    private readonly TemplateStorageService _storage;
    private readonly TemplateCacheService _cache;
    private readonly ClipboardCaptureService _clipboardCapture;
    private readonly SettingsService _settings;
    private readonly CategoryCatalogService _categories;

    public TemplateEditorService(
        TemplateStorageService storage,
        TemplateCacheService cache,
        ClipboardCaptureService clipboardCapture,
        SettingsService settings,
        CategoryCatalogService categories)
    {
        _storage = storage;
        _cache = cache;
        _clipboardCapture = clipboardCapture;
        _settings = settings;
        _categories = categories;
    }

    public TemplatePackage CreateNew()
    {
        var metadata = new TemplateMetadata
        {
            FormatVersion = AppConstants.CurrentTemplateFormatVersion,
            AppVersion = AppConstants.AppVersion,
            Language = _settings.Current.Language,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow
        };

        return new TemplatePackage
        {
            Metadata = metadata,
            Content = new TemplateContent
            {
                PlainText = string.Empty,
                Rtf = RtfHelper.CreateFromPlainText(string.Empty),
                Html = string.Empty
            }
        };
    }

    public async Task SaveAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(UnicodeTextNormalizer.NormalizeKey(package.Metadata.Key)))
        {
            throw new InvalidOperationException("Template key is required.");
        }

        if (string.IsNullOrWhiteSpace(package.Metadata.Name))
        {
            throw new InvalidOperationException("Template name is required.");
        }

        package.Metadata.Key = UnicodeTextNormalizer.NormalizeKey(package.Metadata.Key);
        package.Metadata.ModifiedUtc = DateTime.UtcNow;
        package.Content.Rtf = RtfHelper.EnsureRtf(package.Content.Rtf, package.Content.PlainText);

        await _storage.SaveAsync(package, cancellationToken).ConfigureAwait(false);
        await _categories.AddAsync(package.Metadata.Category, cancellationToken).ConfigureAwait(false);
        _cache.Invalidate();
    }

    public async Task DeleteAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        await _storage.DeleteAsync(package, cancellationToken).ConfigureAwait(false);
        _cache.Invalidate();
    }

    public async Task<TemplatePackage> DuplicateAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        var duplicate = await _storage.DuplicateAsync(package, cancellationToken).ConfigureAwait(false);
        _cache.Invalidate();
        return duplicate;
    }

    public async Task CaptureClipboardIntoAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        var snapshot = await _clipboardCapture.CaptureAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in snapshot.Formats)
        {
            if (item.Format == ClipboardFormatNames.UnicodeText && item.Text is not null)
            {
                package.Content.PlainText = item.Text;
            }
            else if (item.Format == ClipboardFormatNames.Text && string.IsNullOrEmpty(package.Content.PlainText) && item.Text is not null)
            {
                package.Content.PlainText = item.Text;
            }
            else if (item.Format == ClipboardFormatNames.Rtf && item.Text is not null)
            {
                package.Content.Rtf = RtfHelper.EnsureRtf(item.Text, package.Content.PlainText);
            }
            else if (item.Format == ClipboardFormatNames.Html && item.Text is not null)
            {
                package.Content.Html = ClipboardHtmlBuilder.ExtractFragment(item.Text);
            }
            else if (item.Kind is ClipboardDataKind.Bitmap or ClipboardDataKind.Png && item.Bytes is { Length: > 0 })
            {
                AddImage(package, item.Bytes, "clipboard.png");
            }
            else if (item.Kind is ClipboardDataKind.Dib && item.Bytes is { Length: > 0 })
            {
                package.Content.BinaryItems.Add(new TemplateBinaryItem
                {
                    Format = ClipboardFormatNames.DeviceIndependentBitmap,
                    FileName = "DeviceIndependentBitmap.bin",
                    Bytes = item.Bytes
                });
            }
            else if (item.Kind is ClipboardDataKind.Binary && item.Bytes is { Length: > 0 } && ClipboardFormatSafetyPolicy.IsAllowedForTemplateBinaryStorage(item.Format))
            {
                package.Content.BinaryItems.Add(new TemplateBinaryItem
                {
                    Format = item.Format,
                    FileName = item.Format + ".bin",
                    Bytes = item.Bytes
                });
            }
        }

        if (string.IsNullOrWhiteSpace(package.Content.Rtf))
        {
            package.Content.Rtf = RtfHelper.CreateFromPlainText(package.Content.PlainText);
        }
    }

    public void AddImage(TemplatePackage package, byte[] bytes, string fileName)
    {
        if (bytes.Length > _settings.Current.MaxImageBytes)
        {
            throw new InvalidOperationException("Image exceeds configured size limit.");
        }

        var bitmap = ImageHelper.DecodeBitmap(bytes);
        var png = ImageHelper.EncodePng(bitmap);

        package.Content.Images.Add(new TemplateImageItem
        {
            FileName = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(fileName)) + ".png",
            MediaType = "image/png",
            Bytes = png
        });
    }

    public void AddImageFile(TemplatePackage package, string path)
    {
        package.Content.Images.Add(ImageHelper.LoadImageFile(path, _settings.Current.MaxImageBytes));
    }
}

public sealed class TemplateImportExportService
{
    private readonly TemplateStorageService _storage;
    private readonly TemplateCacheService _cache;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;

    public TemplateImportExportService(
        TemplateStorageService storage,
        TemplateCacheService cache,
        SettingsService settings,
        AppPaths paths)
    {
        _storage = storage;
        _cache = cache;
        _settings = settings;
        _paths = paths;
    }

    public async Task ExportAllAsync(string zipPath, CancellationToken cancellationToken)
    {
        var templates = await _cache.GetTemplatesAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(zipPath)!, Path.GetFileName(zipPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var template in templates)
                {
                    if (string.IsNullOrWhiteSpace(template.FilePath) || !File.Exists(template.FilePath))
                    {
                        continue;
                    }

                    archive.CreateEntryFromFile(template.FilePath, "Templates/" + Path.GetFileName(template.FilePath), CompressionLevel.Optimal);
                }

                var settingsEntry = archive.CreateEntry("Settings/settings.json", CompressionLevel.Optimal);
                await using (var stream = settingsEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(stream, _settings.Current, JsonOptions.Default, cancellationToken).ConfigureAwait(false);
                }

                var categories = templates
                    .Select(x => x.Metadata.Category)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.InvariantCultureIgnoreCase)
                    .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
                    .ToList();

                var categoriesEntry = archive.CreateEntry("categories.json", CompressionLevel.Optimal);
                await using (var stream = categoriesEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(stream, categories, JsonOptions.Default, cancellationToken).ConfigureAwait(false);
                }
            }

            AtomicFileWriter.ReplaceFile(tempPath, zipPath);
        }
        finally
        {
            AtomicFileWriter.TryDelete(tempPath);
        }
    }

    public async Task<int> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var imported = 0;

        if (string.Equals(Path.GetExtension(path), AppConstants.TemplateExtension, StringComparison.OrdinalIgnoreCase))
        {
            await _storage.ImportTplPackAsync(path, cancellationToken).ConfigureAwait(false);
            _cache.Invalidate();
            return 1;
        }

        using var archive = ZipFile.OpenRead(path);

        foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith("Templates/", StringComparison.OrdinalIgnoreCase) && x.Name.EndsWith(AppConstants.TemplateExtension, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = Path.Combine(_paths.TempPath, Guid.NewGuid().ToString("N") + AppConstants.TemplateExtension);

            try
            {
                entry.ExtractToFile(tempPath, overwrite: true);
                await _storage.ImportTplPackAsync(tempPath, cancellationToken).ConfigureAwait(false);
                imported++;
            }
            finally
            {
                AtomicFileWriter.TryDelete(tempPath);
            }
        }

        _cache.Invalidate();
        return imported;
    }
}

public sealed class TemplateInsertionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ForegroundWindowService _foreground;
    private readonly FocusValidationService _focusValidation;
    private readonly ClipboardPasteService _paste;
    private readonly KeyboardInputService _keyboard;
    private readonly SearchPopupService _searchPopup;
    private readonly TemplateStorageService _storage;
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;

    public TemplateInsertionCoordinator(
        ForegroundWindowService foreground,
        FocusValidationService focusValidation,
        ClipboardPasteService paste,
        KeyboardInputService keyboard,
        SearchPopupService searchPopup,
        TemplateStorageService storage,
        SettingsService settings,
        LoggingService logging)
    {
        _foreground = foreground;
        _focusValidation = focusValidation;
        _paste = paste;
        _keyboard = keyboard;
        _searchPopup = searchPopup;
        _storage = storage;
        _settings = settings;
        _logging = logging;
    }

    public async Task HandleHotkeyAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            _logging.Warning("Hotkey ignored because an insertion is already running.");
            return;
        }

        try
        {
            await HandleHotkeyCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logging.Error("Template insertion failed.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleHotkeyCoreAsync(CancellationToken cancellationToken)
    {
        var target = _foreground.CaptureForeground();

        if (!target.IsValid || _foreground.IsOwnProcessWindow(target))
        {
            return;
        }

        var selectedTemplate = await _searchPopup.ShowAsync(target, string.Empty, cancellationToken).ConfigureAwait(true);

        if (selectedTemplate is null)
        {
            return;
        }

        if (!await _keyboard.WaitForHotkeyReleasedAsync(_settings.Current.Shortcut, TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(true))
        {
            _logging.Warning("Template insertion continues although the hotkey keys were not released before timeout.");
        }

        await PasteTemplateAsync(selectedTemplate, target, cancellationToken).ConfigureAwait(true);
    }

    private async Task PasteTemplateAsync(TemplatePackage template, WindowTargetContext target, CancellationToken cancellationToken)
    {
        if (!_foreground.TryRestore(target))
        {
            _logging.Warning("Paste skipped because target window could not be restored.");
            return;
        }

        await Task.Delay(60, cancellationToken).ConfigureAwait(true);

        if (!_focusValidation.IsTargetStillValid(target))
        {
            _logging.Warning("Paste skipped because target changed before clipboard set.");
            return;
        }

        var fullTemplate = await LoadFullTemplateAsync(template, cancellationToken).ConfigureAwait(true);
        await _paste.PutTemplateOnClipboardAsync(fullTemplate, cancellationToken).ConfigureAwait(true);

        if (!_focusValidation.IsTargetStillValid(target))
        {
            _logging.Warning("Paste skipped because target changed after clipboard set.");
            return;
        }

        if (!_keyboard.SendCtrlV())
        {
            _logging.Warning("CTRL+V could not be sent.");
        }
    }

    private async Task<TemplatePackage> LoadFullTemplateAsync(TemplatePackage template, CancellationToken cancellationToken)
    {
        if (template.Content.PlainText.Length > 0
            || template.Content.Rtf.Length > 0
            || template.Content.Html.Length > 0
            || template.Content.Images.Count > 0
            || template.Content.BinaryItems.Count > 0
            || string.IsNullOrWhiteSpace(template.FilePath))
        {
            return template;
        }

        return await _storage.LoadAsync(template.FilePath, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TemplateResolver
{
    private readonly TemplateCacheService _cache;

    public TemplateResolver(TemplateCacheService cache)
    {
        _cache = cache;
    }

    public async Task<IReadOnlyList<TemplatePackage>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var templates = await _cache.GetTemplatesAsync(cancellationToken).ConfigureAwait(false);
        return Filter(templates, query);
    }

    public async Task<IReadOnlyList<TemplatePackage>> SearchMetadataAsync(string query, CancellationToken cancellationToken)
    {
        var templates = await _cache.GetTemplateMetadataAsync(cancellationToken).ConfigureAwait(false);
        return Filter(templates, query);
    }

    private static IReadOnlyList<TemplatePackage> Filter(IReadOnlyList<TemplatePackage> templates, string query)
    {
        var normalized = UnicodeTextNormalizer.NormalizeKey(query);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return templates;
        }

        return templates.Where(x =>
                UnicodeTextNormalizer.ContainsKey(x.Metadata.Key, normalized)
                || UnicodeTextNormalizer.ContainsKey(x.Metadata.Name, normalized)
                || UnicodeTextNormalizer.ContainsKey(x.Metadata.Category, normalized))
            .ToList();
    }
}

public sealed class TemplateStorageService
{
    private readonly AppPaths _paths;
    private readonly FileLockService _fileLock;
    private readonly LoggingService _logging;

    public TemplateStorageService(AppPaths paths, FileLockService fileLock, LoggingService logging)
    {
        _paths = paths;
        _fileLock = fileLock;
        _logging = logging;
    }

    public async Task<IReadOnlyList<TemplatePackage>> LoadAllAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.TemplatesPath);
        var result = new List<TemplatePackage>();

        foreach (var path in Directory.EnumerateFiles(_paths.TemplatesPath, "*" + AppConstants.TemplateExtension, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                result.Add(await LoadAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                _logging.Warning($"Template could not be loaded: {path}", exception);
            }
        }

        return SortTemplates(result);
    }

    public async Task<IReadOnlyList<TemplatePackage>> LoadMetadataAllAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.TemplatesPath);
        var result = new List<TemplatePackage>();

        foreach (var path in Directory.EnumerateFiles(_paths.TemplatesPath, "*" + AppConstants.TemplateExtension, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                result.Add(await LoadMetadataAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                _logging.Warning($"Template metadata could not be loaded: {path}", exception);
            }
        }

        return SortTemplates(result);
    }

    public async Task<TemplatePackage> LoadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var metadata = await ReadJsonEntryAsync<TemplateMetadata>(archive, "metadata.json", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Template metadata.json missing.");

        return new TemplatePackage
        {
            Metadata = metadata,
            Content = new TemplateContent
            {
                PlainText = await ReadTextEntryAsync(archive, "text.txt", cancellationToken).ConfigureAwait(false) ?? string.Empty
            },
            FilePath = path
        };
    }

    public async Task<TemplatePackage> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await _fileLock.WaitUntilAvailableAsync(path, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        using var archive = ZipFile.OpenRead(path);
        var manifest = await ReadJsonEntryAsync<TemplateManifest>(archive, "manifest.json", cancellationToken).ConfigureAwait(false) ?? new TemplateManifest();
        var metadata = await ReadJsonEntryAsync<TemplateMetadata>(archive, "metadata.json", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Template metadata.json missing.");

        ValidateManifest(archive, manifest);

        var content = new TemplateContent
        {
            PlainText = await ReadTextEntryAsync(archive, "text.txt", cancellationToken).ConfigureAwait(false) ?? string.Empty,
            Rtf = await ReadTextEntryAsync(archive, "content.rtf", cancellationToken).ConfigureAwait(false) ?? string.Empty,
            Html = await ReadTextEntryAsync(archive, "content.html", cancellationToken).ConfigureAwait(false) ?? string.Empty
        };

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.Name)))
        {
            content.Images.Add(new TemplateImageItem
            {
                FileName = entry.Name,
                MediaType = "image/png",
                Bytes = await ReadBytesEntryAsync(entry, cancellationToken).ConfigureAwait(false)
            });
        }

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("binary/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.Name)))
        {
            var formatName = DecodeBinaryFileName(entry.Name);
            content.BinaryItems.Add(new TemplateBinaryItem
            {
                FileName = entry.Name,
                Format = formatName,
                Bytes = await ReadBytesEntryAsync(entry, cancellationToken).ConfigureAwait(false)
            });
        }

        metadata.ClipboardFormats = CollectFormats(content);
        return new TemplatePackage { Metadata = metadata, Content = content, FilePath = path };
    }

    public async Task SaveAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.Metadata.Id))
        {
            package.Metadata.Id = Guid.NewGuid().ToString("N");
        }

        package.Metadata.FormatVersion = AppConstants.CurrentTemplateFormatVersion;
        package.Metadata.AppVersion = AppConstants.AppVersion;
        package.Metadata.ModifiedUtc = DateTime.UtcNow;

        if (package.Metadata.CreatedUtc == default)
        {
            package.Metadata.CreatedUtc = package.Metadata.ModifiedUtc;
        }

        package.Metadata.ClipboardFormats = CollectFormats(package.Content);

        var targetPath = package.FilePath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = BuildTemplatePath(package.Metadata);
            package.FilePath = targetPath;
        }

        Directory.CreateDirectory(_paths.TemplatesPath);
        var tempPath = Path.Combine(_paths.TemplatesPath, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WritePackageZipAsync(tempPath, package, cancellationToken).ConfigureAwait(false);
            AtomicFileWriter.ReplaceFile(tempPath, targetPath);
            package.FilePath = targetPath;
        }
        finally
        {
            AtomicFileWriter.TryDelete(tempPath);
        }
    }

    public Task DeleteAsync(TemplatePackage package, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(package.FilePath) && File.Exists(package.FilePath))
        {
            File.Delete(package.FilePath);
        }

        return Task.CompletedTask;
    }

    public async Task<TemplatePackage> DuplicateAsync(TemplatePackage source, CancellationToken cancellationToken)
    {
        var duplicate = new TemplatePackage
        {
            Metadata = source.Metadata.CloneForDuplicate(),
            Content = source.Content.Clone()
        };

        duplicate.Metadata.Key = await CreateUniqueKeyAsync(source.Metadata.Key + "_copy", cancellationToken).ConfigureAwait(false);
        duplicate.Metadata.Name = source.Metadata.Name + " Copy";
        await SaveAsync(duplicate, cancellationToken).ConfigureAwait(false);
        return duplicate;
    }

    public async Task<TemplatePackage> ImportTplPackAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        loaded.Metadata.Id = Guid.NewGuid().ToString("N");
        loaded.FilePath = null;
        loaded.Metadata.Key = await CreateUniqueKeyAsync(loaded.Metadata.Key, cancellationToken).ConfigureAwait(false);
        await SaveAsync(loaded, cancellationToken).ConfigureAwait(false);
        return loaded;
    }

    private async Task<string> CreateUniqueKeyAsync(string preferredKey, CancellationToken cancellationToken)
    {
        var existing = (await LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => UnicodeTextNormalizer.NormalizeKey(x.Metadata.Key))
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

        var normalized = UnicodeTextNormalizer.NormalizeKey(preferredKey);
        if (!existing.Contains(normalized))
        {
            return normalized;
        }

        for (var index = 2; index < 10000; index++)
        {
            var candidate = $"{normalized}_{index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return normalized + "_" + Guid.NewGuid().ToString("N")[..8];
    }

    private string BuildTemplatePath(TemplateMetadata metadata)
    {
        var safe = FileNameSanitizer.Sanitize(metadata.Key);
        return Path.Combine(_paths.TemplatesPath, $"{safe}_{metadata.Id}{AppConstants.TemplateExtension}");
    }

    private static async Task WritePackageZipAsync(
    string path,
    TemplatePackage package,
    CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(
            path,
            ZipArchiveMode.Create);

        var manifest = new TemplateManifest();

        await AddJsonEntryAsync(
            archive,
            manifest,
            "metadata.json",
            package.Metadata,
            cancellationToken).ConfigureAwait(false);

        await AddTextEntryAsync(
            archive,
            manifest,
            "text.txt",
            package.Content.PlainText ?? string.Empty,
            "UnicodeText",
            cancellationToken).ConfigureAwait(false);

        await AddTextEntryAsync(
            archive,
            manifest,
            "content.rtf",
            RtfHelper.EnsureRtf(
                package.Content.Rtf,
                package.Content.PlainText ?? string.Empty),
            "RTF",
            cancellationToken).ConfigureAwait(false);

        await AddTextEntryAsync(
            archive,
            manifest,
            "content.html",
            package.Content.Html ?? string.Empty,
            "HTML",
            cancellationToken).ConfigureAwait(false);

        foreach (var image in package.Content.Images.Where(
            x => x.Bytes.Length > 0))
        {
            var name =
                "images/" +
                FileNameSanitizer.Sanitize(
                    string.IsNullOrWhiteSpace(image.FileName)
                        ? Guid.NewGuid().ToString("N") + ".png"
                        : image.FileName);

            await AddBytesEntryAsync(
                archive,
                manifest,
                name,
                image.Bytes,
                image.MediaType,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var binary in package.Content.BinaryItems.Where(
            x =>
                x.Bytes.Length > 0 &&
                ClipboardFormatSafetyPolicy.IsAllowedForTemplateBinaryStorage(
                    x.Format)))
        {
            var name =
                "binary/" +
                EncodeBinaryFileName(binary.Format);

            await AddBytesEntryAsync(
                archive,
                manifest,
                name,
                binary.Bytes,
                binary.Format,
                cancellationToken).ConfigureAwait(false);
        }

        var manifestEntry = archive.CreateEntry(
            "manifest.json",
            CompressionLevel.Optimal);

        await using var manifestStream =
            manifestEntry.Open();

        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            JsonOptions.Default,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddJsonEntryAsync<T>(ZipArchive archive, TemplateManifest manifest, string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        await AddTextEntryAsync(archive, manifest, path, json, "JSON", cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTextEntryAsync(ZipArchive archive, TemplateManifest manifest, string path, string text, string format, CancellationToken cancellationToken)
    {
        var bytes = AtomicFileWriter.Utf8WithoutBom.GetBytes(text ?? string.Empty);
        await AddBytesEntryAsync(archive, manifest, path, bytes, format, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddBytesEntryAsync(ZipArchive archive, TemplateManifest manifest, string path, byte[] bytes, string format, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using (var stream = entry.Open())
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        manifest.Entries.Add(new TemplateManifestEntry
        {
            Path = path,
            Format = format,
            Length = bytes.Length,
            Sha256 = HashHelper.Sha256Hex(bytes)
        });
    }

    private static async Task<T?> ReadJsonEntryAsync<T>(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        var text = await ReadTextEntryAsync(archive, path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, JsonOptions.Default);
    }

    private static async Task<string?> ReadTextEntryAsync(ZipArchive archive, string path, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, AtomicFileWriter.Utf8WithoutBom);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBytesEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static void ValidateManifest(ZipArchive archive, TemplateManifest manifest)
    {
        foreach (var manifestEntry in manifest.Entries)
        {
            var entry = archive.GetEntry(manifestEntry.Path);
            if (entry is null)
            {
                throw new InvalidDataException($"Container entry missing: {manifestEntry.Path}");
            }

            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            var hash = HashHelper.Sha256Hex(bytes);

            if (!string.Equals(hash, manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Container entry hash mismatch: {manifestEntry.Path}");
            }
        }
    }

    private static IReadOnlyList<TemplatePackage> SortTemplates(IEnumerable<TemplatePackage> templates)
    {
        return templates
            .OrderBy(x => x.Metadata.Category, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(x => x.Metadata.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
    }

    private static List<string> CollectFormats(TemplateContent content)
    {
        var formats = new List<string>();

        if (!string.IsNullOrWhiteSpace(content.Html))
        {
            formats.Add(ClipboardFormatNames.Html);
        }

        if (!string.IsNullOrWhiteSpace(content.Rtf))
        {
            formats.Add(ClipboardFormatNames.Rtf);
        }

        if (!string.IsNullOrWhiteSpace(content.PlainText))
        {
            formats.Add(ClipboardFormatNames.UnicodeText);
            formats.Add(ClipboardFormatNames.Text);
        }

        if (content.Images.Count > 0)
        {
            formats.Add(ClipboardFormatNames.Bitmap);
            formats.Add(ClipboardFormatNames.Png);
        }

        formats.AddRange(content.BinaryItems.Select(x => x.Format).Where(x => !string.IsNullOrWhiteSpace(x)));

        return formats.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string EncodeBinaryFileName(string format)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(format)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return encoded + ".bin";
    }

    private static string DecodeBinaryFileName(string name)
    {
        var base64 = Path.GetFileNameWithoutExtension(name).Replace('-', '+').Replace('_', '/');
        while (base64.Length % 4 != 0)
        {
            base64 += "=";
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return name;
        }
    }
}
