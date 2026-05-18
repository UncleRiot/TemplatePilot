using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using TemplatePilot.Models;
using TemplatePilot.Services;

namespace TemplatePilot.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly TemplateCacheService _cache;
    private readonly TemplateEditorService _editorService;
    private readonly TemplateEditorDialogService _editorDialog;
    private readonly TemplateImportExportService _importExport;
    private readonly CategoryCatalogService _categories;
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly StartupService _startup;
    private readonly GlobalHotkeyService _hotkey;
    private readonly MessageBoxService _messages;
    private readonly FileDialogService _fileDialogs;
    private readonly LoggingService _logging;

    private int _activeTabIndex;
    private string _searchText = string.Empty;
    private string? _selectedCategory;
    private string? _selectedCategoryFilter;
    private string _categoryEditText = string.Empty;
    private TemplatePackage? _selectedTemplate;
    private string _selectedLanguage;
    private string _shortcutKey;
    private bool _shortcutControl;
    private bool _shortcutShift;
    private bool _shortcutAlt;
    private bool _shortcutWin;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _closeToTray;

    public MainWindowViewModel(
        TemplateCacheService cache,
        TemplateEditorService editorService,
        TemplateEditorDialogService editorDialog,
        TemplateImportExportService importExport,
        CategoryCatalogService categories,
        SettingsService settings,
        LocalizationService localization,
        StartupService startup,
        GlobalHotkeyService hotkey,
        MessageBoxService messages,
        FileDialogService fileDialogs,
        LoggingService logging)
    {
        _cache = cache;
        _editorService = editorService;
        _editorDialog = editorDialog;
        _importExport = importExport;
        _categories = categories;
        _settings = settings;
        _localization = localization;
        _startup = startup;
        _hotkey = hotkey;
        _messages = messages;
        _fileDialogs = fileDialogs;
        _logging = logging;

        _selectedLanguage = ToLanguageDisplayName(_settings.Current.Language);
        _shortcutKey = _settings.Current.Shortcut.Key;
        _shortcutControl = _settings.Current.Shortcut.Control;
        _shortcutShift = _settings.Current.Shortcut.Shift;
        _shortcutAlt = _settings.Current.Shortcut.Alt;
        _shortcutWin = _settings.Current.Shortcut.Win;
        _startWithWindows = _settings.Current.StartWithWindows;
        _minimizeToTray = _settings.Current.MinimizeToTray;
        _closeToTray = _settings.Current.CloseToTray;

        TemplatesView = CollectionViewSource.GetDefaultView(Templates);
        TemplatesView.Filter = FilterTemplate;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        NewTemplateCommand = new AsyncRelayCommand(NewTemplateAsync);
        EditTemplateCommand = new AsyncRelayCommand(EditTemplateAsync, () => SelectedTemplate is not null);
        DeleteTemplateCommand = new AsyncRelayCommand(DeleteTemplateAsync, () => SelectedTemplate is not null);
        DuplicateTemplateCommand = new AsyncRelayCommand(DuplicateTemplateAsync, () => SelectedTemplate is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        ActiveNewCommand = new AsyncRelayCommand(ActiveNewAsync);
        ActiveEditCommand = new AsyncRelayCommand(ActiveEditAsync, CanActiveEdit);
        ActiveSaveCommand = new AsyncRelayCommand(ActiveSaveAsync, CanActiveSave);
        ActiveDeleteCommand = new AsyncRelayCommand(ActiveDeleteAsync, CanActiveDelete);
        NewCategoryCommand = new RelayCommand(NewCategory);
        SaveCategoryCommand = new AsyncRelayCommand(SaveCategoryAsync);
        DeleteCategoryCommand = new AsyncRelayCommand(DeleteCategoryAsync, () => !string.IsNullOrWhiteSpace(SelectedCategory));
        ClearCategoryFilterCommand = new RelayCommand(ClearCategoryFilter);

        _cache.TemplatesChanged += OnTemplatesChanged;
        _categories.CategoriesChanged += OnCategoriesChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TemplatePackage> Templates { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ICollectionView TemplatesView { get; }
    public IReadOnlyList<string> AvailableLanguages => _localization.AvailableLanguages
        .Select(ToLanguageDisplayName)
        .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
        .ToList();
    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand NewTemplateCommand { get; }
    public AsyncRelayCommand EditTemplateCommand { get; }
    public AsyncRelayCommand DeleteTemplateCommand { get; }
    public AsyncRelayCommand DuplicateTemplateCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ActiveNewCommand { get; }
    public AsyncRelayCommand ActiveEditCommand { get; }
    public AsyncRelayCommand ActiveSaveCommand { get; }
    public AsyncRelayCommand ActiveDeleteCommand { get; }
    public RelayCommand NewCategoryCommand { get; }
    public AsyncRelayCommand SaveCategoryCommand { get; }
    public AsyncRelayCommand DeleteCategoryCommand { get; }
    public RelayCommand ClearCategoryFilterCommand { get; }

    public string AppTitle => _localization["AppTitle"];
    public string TemplatesText => _localization["Templates"];
    public string SearchTextLabel => _localization["Search"];
    public string NewText => _localization["New"];
    public string EditText => _localization["Edit"];
    public string DeleteText => _localization["Delete"];
    public string DuplicateText => _localization["Duplicate"];
    public string ImportText => _localization["Import"];
    public string ExportText => _localization["Export"];
    public string SettingsText => _localization["Settings"];
    public string ShortcutText => _localization["Shortcut"];
    public string SaveText => _localization["Save"];
    public string PlainTextLabel => _localization["PlainText"];
    public string RichTextLabel => _localization["RichText"];
    public string HtmlLabel => _localization["Html"];
    public string ImagesLabel => _localization["Images"];
    public string PreviewLabel => _localization["Preview"];
    public string KeyLabel => _localization["Key"];
    public string NameLabel => _localization["Name"];
    public string CategoryLabel => _localization["Category"];
    public string CategoriesText => _localization["Categories"];
    public string NewCategoryText => _localization["NewCategory"];
    public string SaveCategoryText => _localization["SaveCategory"];
    public string DeleteCategoryText => _localization["DeleteCategory"];
    public string ClearCategoryFilterText => _localization["ClearCategoryFilter"];
    public string CategoryFilterText => _localization["CategoryFilter"];
    public string LanguageLabel => _localization["Language"];
    public string StartWithWindowsLabel => _localization["StartWithWindows"];
    public string MinimizeToTrayLabel => _localization["MinimizeToTray"];
    public string CloseToTrayLabel => _localization["CloseToTray"];

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (SetProperty(ref _activeTabIndex, value))
            {
                RaiseActiveCommandStates();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                TemplatesView.Refresh();
            }
        }
    }

    public string? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                CategoryEditText = value ?? string.Empty;
                DeleteCategoryCommand.RaiseCanExecuteChanged();
                RaiseActiveCommandStates();
            }
        }
    }

    public string? SelectedCategoryFilter
    {
        get => _selectedCategoryFilter;
        set
        {
            if (SetProperty(ref _selectedCategoryFilter, value))
            {
                TemplatesView.Refresh();
            }
        }
    }

    public string CategoryEditText
    {
        get => _categoryEditText;
        set
        {
            if (SetProperty(ref _categoryEditText, value))
            {
                RaiseActiveCommandStates();
            }
        }
    }

    public TemplatePackage? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                EditTemplateCommand.RaiseCanExecuteChanged();
                DeleteTemplateCommand.RaiseCanExecuteChanged();
                DuplicateTemplateCommand.RaiseCanExecuteChanged();
                RaiseActiveCommandStates();
            }
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
    }

    public string ShortcutKey
    {
        get => _shortcutKey;
        set => SetProperty(ref _shortcutKey, value);
    }

    public bool ShortcutControl
    {
        get => _shortcutControl;
        set => SetProperty(ref _shortcutControl, value);
    }

    public bool ShortcutShift
    {
        get => _shortcutShift;
        set => SetProperty(ref _shortcutShift, value);
    }

    public bool ShortcutAlt
    {
        get => _shortcutAlt;
        set => SetProperty(ref _shortcutAlt, value);
    }

    public bool ShortcutWin
    {
        get => _shortcutWin;
        set => SetProperty(ref _shortcutWin, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    private async Task InitializeAsync()
    {
        await RefreshTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshCategoriesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = await _cache.RefreshAsync(cancellationToken).ConfigureAwait(true);
        Templates.Clear();

        foreach (var template in templates)
        {
            Templates.Add(template);
        }

        await _categories.AddRangeAsync(templates.Select(x => x.Metadata.Category), cancellationToken).ConfigureAwait(true);
        TemplatesView.Refresh();
        SelectedTemplate ??= Templates.FirstOrDefault();
    }

    private bool FilterTemplate(object item)
    {
        if (item is not TemplatePackage template)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedCategoryFilter) && !string.Equals(template.Metadata.Category, SelectedCategoryFilter, StringComparison.InvariantCultureIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Key, SearchText)
            || TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Name, SearchText)
            || TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Category, SearchText);
    }

    private async Task RefreshCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await _categories.GetCategoriesAsync(cancellationToken).ConfigureAwait(true);
        Categories.Clear();

        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        if (!string.IsNullOrWhiteSpace(SelectedCategory) && !Categories.Contains(SelectedCategory, StringComparer.InvariantCultureIgnoreCase))
        {
            SelectedCategory = null;
        }

        if (!string.IsNullOrWhiteSpace(SelectedCategoryFilter) && !Categories.Contains(SelectedCategoryFilter, StringComparer.InvariantCultureIgnoreCase))
        {
            SelectedCategoryFilter = null;
        }
    }

    private async Task ActiveNewAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await NewTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        NewCategory();
    }

    private async Task ActiveEditAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await EditTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        EditCategory();
    }

    private async Task ActiveSaveAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await SaveSelectedTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await SaveCategoryAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ActiveDeleteAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await DeleteTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await DeleteCategoryAsync(cancellationToken).ConfigureAwait(true);
    }

    private bool CanActiveEdit()
    {
        return ActiveTabIndex == 0
            ? SelectedTemplate is not null
            : !string.IsNullOrWhiteSpace(SelectedCategory);
    }

    private bool CanActiveSave()
    {
        return ActiveTabIndex == 0
            ? SelectedTemplate is not null
            : !string.IsNullOrWhiteSpace(CategoryEditText);
    }

    private bool CanActiveDelete()
    {
        return ActiveTabIndex == 0
            ? SelectedTemplate is not null
            : !string.IsNullOrWhiteSpace(SelectedCategory);
    }

    private void RaiseActiveCommandStates()
    {
        ActiveEditCommand.RaiseCanExecuteChanged();
        ActiveSaveCommand.RaiseCanExecuteChanged();
        ActiveDeleteCommand.RaiseCanExecuteChanged();
    }

    private void EditCategory()
    {
        if (!string.IsNullOrWhiteSpace(SelectedCategory))
        {
            CategoryEditText = SelectedCategory;
        }
    }

    private async Task SaveSelectedTemplateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        try
        {
            await _editorService.SaveAsync(SelectedTemplate, cancellationToken).ConfigureAwait(true);
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logging.Error("Template save failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private void NewCategory()
    {
        SelectedCategory = null;
        CategoryEditText = string.Empty;
    }

    private void ClearCategoryFilter()
    {
        SelectedCategoryFilter = null;
    }

    private async Task SaveCategoryAsync(CancellationToken cancellationToken)
    {
        var value = CategoryEditText.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(SelectedCategory))
            {
                await _categories.AddAsync(value, cancellationToken).ConfigureAwait(true);
                await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
                SelectedCategory = Categories.FirstOrDefault(x => string.Equals(x, value, StringComparison.InvariantCultureIgnoreCase));
                return;
            }

            var oldCategory = SelectedCategory;
            await _categories.RenameAsync(oldCategory, value, cancellationToken).ConfigureAwait(true);
            _cache.Invalidate();
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
            SelectedCategory = Categories.FirstOrDefault(x => string.Equals(x, value, StringComparison.InvariantCultureIgnoreCase));
        }
        catch (Exception exception)
        {
            _logging.Error("Category save failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private async Task DeleteCategoryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedCategory))
        {
            return;
        }

        try
        {
            await _categories.DeleteAsync(SelectedCategory, cancellationToken).ConfigureAwait(true);
            SelectedCategory = null;
            CategoryEditText = string.Empty;
            await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logging.Error("Category delete failed.", exception);
            _messages.Error(_localization["CategoryInUse"]);
        }
    }

    private async Task NewTemplateAsync(CancellationToken cancellationToken)
    {
        if (await _editorDialog.EditAsync(null, cancellationToken).ConfigureAwait(true))
        {
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task EditTemplateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        if (await _editorDialog.EditAsync(SelectedTemplate, cancellationToken).ConfigureAwait(true))
        {
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task DeleteTemplateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        if (!_messages.Confirm(_localization["ConfirmDelete"]))
        {
            return;
        }

        await _editorService.DeleteAsync(SelectedTemplate, cancellationToken).ConfigureAwait(true);
        SelectedTemplate = null;
        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task DuplicateTemplateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        var duplicate = await _editorService.DuplicateAsync(SelectedTemplate, cancellationToken).ConfigureAwait(true);
        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        SelectedTemplate = Templates.FirstOrDefault(x => x.Metadata.Id == duplicate.Metadata.Id);
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        var path = _fileDialogs.ShowOpenTemplateOrZipDialog();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var count = await _importExport.ImportAsync(path, cancellationToken).ConfigureAwait(true);
        await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        _messages.Info($"{_localization["ImportCompleted"]} ({count})");
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var path = _fileDialogs.ShowExportZipDialog();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await _importExport.ExportAllAsync(path, cancellationToken).ConfigureAwait(true);
        _messages.Info(_localization["ExportCompleted"]);
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        var next = _settings.Current.Clone();
        next.Language = ToLanguageCode(SelectedLanguage);
        next.Shortcut.Key = ShortcutKey.Trim();
        next.Shortcut.Control = ShortcutControl;
        next.Shortcut.Shift = ShortcutShift;
        next.Shortcut.Alt = ShortcutAlt;
        next.Shortcut.Win = ShortcutWin;
        next.StartWithWindows = StartWithWindows;
        next.MinimizeToTray = MinimizeToTray;
        next.CloseToTray = CloseToTray;

        await _settings.SaveAsync(next, cancellationToken).ConfigureAwait(true);
        _localization.SetLanguage(next.Language);
        _startup.SetEnabled(next.StartWithWindows);

        try
        {
            _hotkey.Reload(next.Shortcut);
        }
        catch (Exception exception)
        {
            _logging.Error("Hotkey reload failed.", exception);
            _messages.Error(_localization["HotkeyConflict"]);
        }
    }

    private static string ToLanguageDisplayName(string language)
    {
        return string.Equals(language, "de", StringComparison.OrdinalIgnoreCase)
            ? "Deutsch"
            : "Englisch";
    }

    private static string ToLanguageCode(string language)
    {
        return string.Equals(language, "Deutsch", StringComparison.OrdinalIgnoreCase)
            ? "de"
            : "en";
    }

    private void OnTemplatesChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await RefreshTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
        });
    }

    private void OnCategoriesChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await RefreshCategoriesAsync(CancellationToken.None).ConfigureAwait(true);
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        _cache.TemplatesChanged -= OnTemplatesChanged;
        _categories.CategoriesChanged -= OnCategoriesChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}