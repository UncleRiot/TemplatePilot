using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using TemplatePilot.Infrastructure;
using TemplatePilot.Models;
using TemplatePilot.Services;

namespace TemplatePilot.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    public event Action<string, bool>? UpdateDialogRequested;

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
    private readonly CategoryDialogService _categoryDialog;
    private readonly FileDialogService _fileDialogs;
    private readonly LoggingService _logging;
    private readonly AppThemeService _themeService;
    private readonly AppPaths _paths;
    private readonly UpdateService _updateService;

    private int _activeTabIndex;
    private int _selectedNavigationIndex;
    private string _searchText = string.Empty;
    private string? _selectedCategory;
    private string? _selectedCategoryFilter;
    private string _categoryEditText = string.Empty;
    private TemplatePackage? _selectedTemplate;
    private string _selectedLanguage;
    private string _selectedTheme;
    private string _shortcutKey;
    private bool _shortcutControl;
    private bool _shortcutShift;
    private bool _shortcutAlt;
    private bool _shortcutWin;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _closeToTray;
    private int _searchPopupVisibleLines;
    private string _templatesPath;
    private TemplateEditorViewModel? _activeEditor;
    private string _updateStatus = string.Empty;
    private string _latestReleaseUrl = UpdateService.ReleasesUrl;
    private string _latestVersion = string.Empty;
    private UpdateState _updateState = UpdateState.Checking;

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
        CategoryDialogService categoryDialog,
        FileDialogService fileDialogs,
        LoggingService logging,
        AppThemeService themeService,
        AppPaths paths,
        UpdateService updateService)
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
        _categoryDialog = categoryDialog;
        _fileDialogs = fileDialogs;
        _logging = logging;
        _themeService = themeService;
        _paths = paths;
        _updateService = updateService;
        _updateStatus = _localization["CheckingForUpdates"];

        _selectedLanguage = _localization.GetLanguageDisplayName(_settings.Current.Language);
        _selectedTheme = ToThemeDisplayName(_settings.Current.Theme);
        _shortcutKey = _settings.Current.Shortcut.Key;
        _shortcutControl = _settings.Current.Shortcut.Control;
        _shortcutShift = _settings.Current.Shortcut.Shift;
        _shortcutAlt = _settings.Current.Shortcut.Alt;
        _shortcutWin = _settings.Current.Shortcut.Win;
        _startWithWindows = _settings.Current.StartWithWindows;
        _minimizeToTray = _settings.Current.MinimizeToTray;
        _closeToTray = _settings.Current.CloseToTray;
        _searchPopupVisibleLines = Math.Clamp(_settings.Current.SearchPopupVisibleLines, 1, 10);
        _templatesPath = _paths.TemplatesPath;

        TemplatesView = CollectionViewSource.GetDefaultView(Templates);
        TemplatesView.Filter = FilterTemplate;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        NewTemplateCommand = new AsyncRelayCommand(NewTemplateAsync, () => !IsEditorOpen);
        EditTemplateCommand = new AsyncRelayCommand(EditTemplateAsync, () => SelectedTemplate is not null && !IsEditorOpen);
        DeleteTemplateCommand = new AsyncRelayCommand(DeleteTemplateAsync, () => SelectedTemplate is not null);
        DuplicateTemplateCommand = new AsyncRelayCommand(DuplicateTemplateAsync, () => SelectedTemplate is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        BrowseTemplatesFolderCommand = new RelayCommand(BrowseTemplatesFolder);
        ActiveNewCommand = new AsyncRelayCommand(ActiveNewAsync);
        ActiveEditCommand = new AsyncRelayCommand(ActiveEditAsync, CanActiveEdit);
        ActiveSaveCommand = new AsyncRelayCommand(ActiveSaveAsync, CanActiveSave);
        ActiveDeleteCommand = new AsyncRelayCommand(ActiveDeleteAsync, CanActiveDelete);
        NewCategoryCommand = new AsyncRelayCommand(NewCategoryAsync);
        SaveCategoryCommand = new AsyncRelayCommand(SaveCategoryAsync);
        DeleteCategoryCommand = new AsyncRelayCommand(DeleteCategoryAsync, () => !string.IsNullOrWhiteSpace(SelectedCategory));
        ClearCategoryFilterCommand = new RelayCommand(ClearCategoryFilter);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesManuallyAsync);
        OpenRepositoryCommand = new RelayCommand(
            () => OpenUrl(UpdateService.RepositoryUrl));
        OpenReleasesCommand = new RelayCommand(
            () => OpenUrl(_latestReleaseUrl));
        OpenKoFiCommand = new RelayCommand(
            () => OpenUrl("https://ko-fi.com/uncleriot"));

        _cache.TemplatesChanged += OnTemplatesChanged;
        _categories.CategoriesChanged += OnCategoriesChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TemplatePackage> Templates { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ICollectionView TemplatesView { get; }
    public IReadOnlyList<string> AvailableLanguages => _localization.AvailableLanguages
        .Select(_localization.GetLanguageDisplayName)
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
    public RelayCommand BrowseTemplatesFolderCommand { get; }
    public AsyncRelayCommand ActiveNewCommand { get; }
    public AsyncRelayCommand ActiveEditCommand { get; }
    public AsyncRelayCommand ActiveSaveCommand { get; }
    public AsyncRelayCommand ActiveDeleteCommand { get; }
    public AsyncRelayCommand NewCategoryCommand { get; }
    public AsyncRelayCommand SaveCategoryCommand { get; }
    public AsyncRelayCommand DeleteCategoryCommand { get; }
    public RelayCommand ClearCategoryFilterCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }
    public RelayCommand OpenKoFiCommand { get; }

    public TemplateEditorViewModel? ActiveEditor
    {
        get => _activeEditor;
        private set
        {
            if (SetProperty(ref _activeEditor, value))
            {
                OnPropertyChanged(nameof(IsEditorOpen));
                NewTemplateCommand.RaiseCanExecuteChanged();
                EditTemplateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEditorOpen => ActiveEditor is not null;

    public string AppTitle => _localization["AppTitle"];
    public string EditorText => _localization["Editor"];
    public string AboutText => _localization["About"];
    public string ThemeText => _localization["Theme"];
    public string ThemeSystemText => _localization["ThemeSystem"];
    public string ThemeLightText => _localization["ThemeLight"];
    public string ThemeDarkText => _localization["ThemeDark"];
    public string SettingsDescriptionText => _localization["SettingsDescription"];
    public string AboutDescriptionText => _localization["AboutDescription"];
    public string VersionText => _localization["Version"];
    public string AppearanceText => _localization["Appearance"];
    public string GeneralText => _localization["General"];
    public string CtrlText => _localization["Ctrl"];
    public string ShiftText => _localization["Shift"];
    public string AltText => _localization["Alt"];
    public string WinText => _localization["Win"];
    public string ApplicationVersion => TemplatePilot.Infrastructure.AppConstants.AppVersion;
    public IReadOnlyList<string> AvailableThemes =>
        new[] { ThemeSystemText, ThemeLightText, ThemeDarkText };
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
    public string SearchPopupVisibleLinesLabel => _localization["SearchPopupVisibleLines"];
    public IReadOnlyList<int> SearchPopupVisibleLineOptions { get; } = Enumerable.Range(1, 10).ToList();
    public string TemplatesFolderLabel => _localization["TemplatesFolder"];
    public string ChooseFolderText => _localization["ChooseFolder"];
    public string CopyrightText => _localization["Copyright"];
    public string LicenseText => _localization["License"];
    public string RepositoryText => _localization["Repository"];
    public string ReleasesText => _localization["Releases"];
    public string CheckForUpdatesText => _localization["CheckForUpdates"];
    public string OpenReleaseText => _localization["OpenRelease"];
    public string OkText => _localization["OK"];
    public string CancelText => _localization["Cancel"];
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public int SelectedNavigationIndex
    {
        get => _selectedNavigationIndex;
        set => SetProperty(ref _selectedNavigationIndex, value);
    }

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

    public string SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
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

    public int SearchPopupVisibleLines
    {
        get => _searchPopupVisibleLines;
        set => SetProperty(ref _searchPopupVisibleLines, Math.Clamp(value, 1, 10));
    }

    public string TemplatesPath
    {
        get => _templatesPath;
        set => SetProperty(ref _templatesPath, value);
    }

    private async Task CheckForUpdatesManuallyAsync(
        CancellationToken cancellationToken)
    {
        await CheckForUpdatesAsync(
            showUpToDateMessage: true,
            cancellationToken).ConfigureAwait(true);
    }

    private async Task CheckForUpdatesAsync(
        bool showUpToDateMessage,
        CancellationToken cancellationToken)
    {
        _updateState = UpdateState.Checking;
        RefreshUpdateStatus();

        UpdateCheckResult result =
            await _updateService.CheckAsync(
                cancellationToken).ConfigureAwait(true);

        _latestReleaseUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? UpdateService.ReleasesUrl
            : result.ReleaseUrl;

        if (!result.Succeeded)
        {
            _updateState = UpdateState.Failed;
            RefreshUpdateStatus();
            UpdateDialogRequested?.Invoke(
                UpdateStatus,
                false);
            return;
        }

        if (result.IsUpdateAvailable)
        {
            _latestVersion = result.LatestVersion;
            _updateState = UpdateState.Available;
            RefreshUpdateStatus();

            UpdateDialogRequested?.Invoke(
                UpdateStatus,
                true);
            return;
        }

        _latestVersion = result.LatestVersion;
        _updateState = UpdateState.Current;
        RefreshUpdateStatus();

        if (showUpToDateMessage)
        {
            UpdateDialogRequested?.Invoke(
                UpdateStatus,
                false);
        }
    }

    private void RefreshUpdateStatus()
    {
        UpdateStatus = _updateState switch
        {
            UpdateState.Available => string.Format(
                CultureInfo.CurrentCulture,
                _localization["UpdateAvailable"],
                _latestVersion),
            UpdateState.Current => _localization["UpToDate"],
            UpdateState.Failed => _localization["UpdateCheckFailed"],
            _ => _localization["CheckingForUpdates"]
        };
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logging.Warning($"Could not open URL '{url}'.", exception);
            _messages.Error(_localization["OpenLinkFailed"]);
        }
    }

    private void BrowseTemplatesFolder()
    {
        var selectedPath = _fileDialogs.ShowTemplatesFolderDialog(TemplatesPath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            TemplatesPath = selectedPath;
        }
    }

    private async Task InitializeAsync()
    {
        await RefreshTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshCategoriesAsync(CancellationToken.None).ConfigureAwait(true);
        await CheckForUpdatesAsync(
            showUpToDateMessage: false,
            CancellationToken.None).ConfigureAwait(true);
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

        await NewCategoryAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ActiveEditAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await EditTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await EditCategoryAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ActiveSaveAsync(CancellationToken cancellationToken)
    {
        if (ActiveTabIndex == 0)
        {
            await SaveSelectedTemplateAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await EditCategoryAsync(cancellationToken).ConfigureAwait(true);
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
            : !string.IsNullOrWhiteSpace(SelectedCategory);
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

    private async Task EditCategoryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedCategory))
        {
            return;
        }

        var oldCategory = SelectedCategory;
        var category = _categoryDialog.ShowCategoryDialog(_localization["Edit"], oldCategory);
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        CategoryEditText = category;

        try
        {
            await _categories.RenameAsync(oldCategory, category, cancellationToken).ConfigureAwait(true);
            _cache.Invalidate();
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
            SelectedCategory = Categories.FirstOrDefault(x => string.Equals(x, category, StringComparison.InvariantCultureIgnoreCase));
        }
        catch (Exception exception)
        {
            _logging.Error("Category edit failed.", exception);
            _messages.Error(exception.Message);
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

    private async Task NewCategoryAsync(CancellationToken cancellationToken)
    {
        var category = _categoryDialog.ShowCategoryDialog(_localization["AddCategory"], string.Empty);
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        CategoryEditText = category;

        try
        {
            await _categories.AddAsync(category, cancellationToken).ConfigureAwait(true);
            await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
            SelectedCategory = Categories.FirstOrDefault(x => string.Equals(x, category, StringComparison.InvariantCultureIgnoreCase));
        }
        catch (Exception exception)
        {
            _logging.Error("Category add failed.", exception);
            _messages.Error(exception.Message);
        }
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

        if (!_messages.Confirm(_localization["ConfirmDeleteCategory"]))
        {
            return;
        }

        try
        {
            await _categories.DeleteAsync(SelectedCategory, cancellationToken).ConfigureAwait(true);
            _cache.Invalidate();
            SelectedCategory = null;
            CategoryEditText = string.Empty;
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshCategoriesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logging.Error("Category delete failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private async Task NewTemplateAsync(CancellationToken cancellationToken)
    {
        await OpenEditorAsync(null, cancellationToken).ConfigureAwait(true);
    }

    private async Task EditTemplateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        await OpenEditorAsync(SelectedTemplate, cancellationToken).ConfigureAwait(true);
    }

    private async Task OpenEditorAsync(
        TemplatePackage? package,
        CancellationToken cancellationToken)
    {
        if (ActiveEditor is not null)
        {
            return;
        }

        var editor = await _editorDialog.CreateViewModelAsync(
            package,
            cancellationToken).ConfigureAwait(true);

        editor.RequestClose += OnEditorRequestClose;
        ActiveEditor = editor;
        SelectedNavigationIndex = 1;
        ActiveTabIndex = 0;
    }

    public void CancelActiveEditor()
    {
        if (ActiveEditor?.CancelCommand.CanExecute(null) == true)
        {
            ActiveEditor.CancelCommand.Execute(null);
        }
    }

    private async void OnEditorRequestClose(object? sender, bool saved)
    {
        if (sender is not TemplateEditorViewModel editor || !ReferenceEquals(editor, ActiveEditor))
        {
            return;
        }

        editor.RequestClose -= OnEditorRequestClose;
        ActiveEditor = null;
        editor.Dispose();

        if (saved)
        {
            await RefreshTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
            SelectedTemplate = Templates.FirstOrDefault(
                x => x.Metadata.Id == editor.Package.Metadata.Id);
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
        next.Language = _localization.GetLanguageCode(SelectedLanguage);
        next.Theme = ToThemeCode(SelectedTheme);
        next.Shortcut.Key = ShortcutKey.Trim();
        next.Shortcut.Control = ShortcutControl;
        next.Shortcut.Shift = ShortcutShift;
        next.Shortcut.Alt = ShortcutAlt;
        next.Shortcut.Win = ShortcutWin;
        next.StartWithWindows = StartWithWindows;
        next.MinimizeToTray = MinimizeToTray;
        next.CloseToTray = CloseToTray;
        next.SearchPopupVisibleLines = Math.Clamp(SearchPopupVisibleLines, 1, 10);
        next.TemplatesPath = Path.GetFullPath(TemplatesPath.Trim());

        var templatesPathChanged = !string.Equals(
            _paths.TemplatesPath,
            next.TemplatesPath,
            StringComparison.OrdinalIgnoreCase);

        if (templatesPathChanged)
        {
            try
            {
                _paths.SetTemplatesPath(next.TemplatesPath);
                next.TemplatesPath = _paths.TemplatesPath;
            }
            catch (Exception exception)
            {
                _logging.Error("Templates folder change failed.", exception);
                _messages.Error(exception.Message);
                return;
            }
        }

        await _settings.SaveAsync(next, cancellationToken).ConfigureAwait(true);
        _localization.SetLanguage(next.Language);
        _selectedLanguage = _localization.GetLanguageDisplayName(next.Language);
        _selectedTheme = ToThemeDisplayName(next.Theme);
        _themeService.ApplyTheme(next.Theme);
        _startup.SetEnabled(next.StartWithWindows);

        if (templatesPathChanged)
        {
            _cache.ChangeTemplatesPath();
            TemplatesPath = _paths.TemplatesPath;
            SelectedTemplate = null;
            await RefreshTemplatesAsync(cancellationToken).ConfigureAwait(true);
        }

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

    private string ToThemeDisplayName(string theme)
    {
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeDarkText;
        }

        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeLightText;
        }

        return ThemeSystemText;
    }

    private string ToThemeCode(string theme)
    {
        if (string.Equals(theme, ThemeDarkText, StringComparison.CurrentCultureIgnoreCase))
        {
            return "Dark";
        }

        if (string.Equals(theme, ThemeLightText, StringComparison.CurrentCultureIgnoreCase))
        {
            return "Light";
        }

        return "System";
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
        _selectedLanguage =
            _localization.GetLanguageDisplayName(_localization.CurrentLanguage);
        _selectedTheme =
            ToThemeDisplayName(_settings.Current.Theme);
        RefreshUpdateStatus();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        if (ActiveEditor is not null)
        {
            ActiveEditor.RequestClose -= OnEditorRequestClose;
            ActiveEditor.Dispose();
            ActiveEditor = null;
        }

        _cache.TemplatesChanged -= OnTemplatesChanged;
        _categories.CategoriesChanged -= OnCategoriesChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }


    private enum UpdateState
    {
        Checking,
        Current,
        Available,
        Failed
    }

}