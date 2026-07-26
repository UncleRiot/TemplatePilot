using System.Collections.ObjectModel;
using System.Windows.Threading;
using TemplatePilot.Models;
using TemplatePilot.Services;

namespace TemplatePilot.ViewModels;

public sealed class TemplateEditorViewModel : ViewModelBase, IDisposable
{
    private readonly TemplateEditorService _editorService;
    private readonly LocalizationService _localization;
    private readonly CrashRecoveryService _crashRecovery;
    private readonly SettingsService _settings;
    private readonly MessageBoxService _messages;
    private readonly LoggingService _logging;
    private readonly CategoryCatalogService _categories;
    private readonly CategoryDialogService _categoryDialog;
    private readonly DispatcherTimer _autosaveTimer;
    private string _key;
    private string _templateName;
    private string _category;
    private string _language;
    private string _plainText;
    private string _rtf;
    private string _html;
    private TemplateImageItem? _selectedImage;
    private bool _isDirty;

    public TemplateEditorViewModel(
        TemplatePackage package,
        TemplateEditorService editorService,
        LocalizationService localization,
        CrashRecoveryService crashRecovery,
        SettingsService settings,
        MessageBoxService messages,
        LoggingService logging,
        CategoryCatalogService categories,
        CategoryDialogService categoryDialog)
    {
        Package = package;
        _editorService = editorService;
        _localization = localization;
        _crashRecovery = crashRecovery;
        _settings = settings;
        _messages = messages;
        _logging = logging;
        _categories = categories;
        _categoryDialog = categoryDialog;

        _key = package.Metadata.Key;
        _templateName = package.Metadata.Name;
        _category = package.Metadata.Category;
        _language = package.Metadata.Language;
        _plainText = package.Content.PlainText;
        _rtf = package.Content.Rtf;
        _html = package.Content.Html;

        foreach (var image in package.Content.Images)
        {
            Images.Add(image);
        }

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
        CaptureClipboardCommand = new AsyncRelayCommand(CaptureClipboardAsync);
        AddCategoryCommand = new AsyncRelayCommand(AddCategoryAsync);
        RemoveImageCommand = new RelayCommand(RemoveSelectedImage, () => SelectedImage is not null);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += OnAutosaveTick;
        _autosaveTimer.Start();

        _localization.LanguageChanged += OnLanguageChanged;
        _categories.CategoriesChanged += OnCategoriesChanged;
        _ = LoadCategoriesAsync(CancellationToken.None);
    }

    public event EventHandler<bool>? RequestClose;

    public TemplatePackage Package { get; }
    public ObservableCollection<TemplateImageItem> Images { get; } = new();
    public ObservableCollection<string> AvailableCategories { get; } = new();
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand CaptureClipboardCommand { get; }
    public AsyncRelayCommand AddCategoryCommand { get; }
    public RelayCommand RemoveImageCommand { get; }

    public string WindowTitle => _localization["Edit"];
    public string SaveText => _localization["Save"];
    public string CancelText => _localization["Cancel"];
    public string PlainTextLabel => _localization["PlainText"];
    public string RichTextLabel => _localization["RichText"];
    public string HtmlLabel => _localization["Html"];
    public string ImagesLabel => _localization["Images"];
    public string KeyLabel => _localization["Key"];
    public string CategoryLabel => _localization["Category"];
    public string AddCategoryText => _localization["AddCategory"];
    public string RemoveImageText => _localization["RemoveImage"];
    public string DragDropImageText => _localization["DragDropImage"];
    public string UndoText => _localization["Undo"];
    public string RedoText => _localization["Redo"];
    public string LanguageLabel => _localization["Language"];
    public IReadOnlyList<string> AvailableLanguages => _localization.AvailableLanguages;

    public string Key
    {
        get => _key;
        set => SetDirtyProperty(ref _key, value);
    }

    public string TemplateName
    {
        get => _templateName;
        set => SetDirtyProperty(ref _templateName, value);
    }

    public string Category
    {
        get => _category;
        set => SetDirtyProperty(ref _category, value ?? string.Empty);
    }

    public string Language
    {
        get => _language;
        set => SetDirtyProperty(ref _language, value);
    }

    public string PlainText
    {
        get => _plainText;
        set => SetDirtyProperty(ref _plainText, value);
    }

    public string Rtf
    {
        get => _rtf;
        set => SetDirtyProperty(ref _rtf, value);
    }

    public string Html
    {
        get => _html;
        set => SetDirtyProperty(ref _html, value);
    }

    public TemplateImageItem? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (SetProperty(ref _selectedImage, value))
            {
                RemoveImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void AddDroppedFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(TemplatePilot.Helpers.ImageHelper.IsSupportedImagePath))
        {
            try
            {
                ApplyToPackage();
                _editorService.AddImageFile(Package, path);
                Images.Add(Package.Content.Images.Last());
                _isDirty = true;
            }
            catch (Exception exception)
            {
                _logging.Warning("Image drop failed.", exception);
                _messages.Error(exception.Message);
            }
        }
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await _categories.GetCategoriesAsync(cancellationToken).ConfigureAwait(true);
        AvailableCategories.Clear();

        foreach (var category in categories)
        {
            AvailableCategories.Add(category);
        }

        if (!string.IsNullOrWhiteSpace(Category) && !AvailableCategories.Contains(Category, StringComparer.InvariantCultureIgnoreCase))
        {
            AvailableCategories.Add(Category);
        }
    }

    private async Task AddCategoryAsync(CancellationToken cancellationToken)
    {
        var category = _categoryDialog.ShowCategoryDialog(_localization["AddCategory"], string.Empty);
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        try
        {
            await _categories.AddAsync(category, cancellationToken).ConfigureAwait(true);
            await LoadCategoriesAsync(cancellationToken).ConfigureAwait(true);
            Category = category;
        }
        catch (Exception exception)
        {
            _logging.Error("Category add failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        ApplyToPackage();

        try
        {
            await _editorService.SaveAsync(Package, cancellationToken).ConfigureAwait(true);
            await _crashRecovery.ClearRecoveryAsync(Package.Metadata.Id).ConfigureAwait(true);
            _isDirty = false;
            RequestClose?.Invoke(this, true);
        }
        catch (Exception exception)
        {
            _logging.Error("Template save failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private async Task CaptureClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApplyToPackage();
            await _editorService.CaptureClipboardIntoAsync(Package, cancellationToken).ConfigureAwait(true);
            Key = Package.Metadata.Key;
            TemplateName = Package.Metadata.Name;
            Category = Package.Metadata.Category;
            Language = Package.Metadata.Language;
            PlainText = Package.Content.PlainText;
            Rtf = Package.Content.Rtf;
            Html = Package.Content.Html;
            Images.Clear();

            foreach (var image in Package.Content.Images)
            {
                Images.Add(image);
            }

            _isDirty = true;
        }
        catch (Exception exception)
        {
            _logging.Error("Clipboard capture into template failed.", exception);
            _messages.Error(exception.Message);
        }
    }

    private void RemoveSelectedImage()
    {
        if (SelectedImage is null)
        {
            return;
        }

        ApplyToPackage();
        Package.Content.Images.Remove(SelectedImage);
        Images.Remove(SelectedImage);
        SelectedImage = null;
        _isDirty = true;
    }

    private async void OnAutosaveTick(object? sender, EventArgs e)
    {
        if (!_settings.Current.AutosaveEditor || !_isDirty)
        {
            return;
        }

        try
        {
            ApplyToPackage();
            await _crashRecovery.SaveRecoveryAsync(Package, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logging.Warning("Editor crash recovery save failed.", exception);
        }
    }

    private void ApplyToPackage()
    {
        Package.Metadata.Key = Key;
        Package.Metadata.Name = string.IsNullOrWhiteSpace(Key) ? TemplateName : Key;
        Package.Metadata.Category = Category;
        Package.Metadata.Language = Language;
        Package.Content.PlainText = PlainText;
        Package.Content.Rtf = Rtf;
        Package.Content.Html = Html;
        Package.Content.Images = Images.ToList();
    }

    private void SetDirtyProperty(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            _isDirty = true;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
    }

    private void OnCategoriesChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await LoadCategoriesAsync(CancellationToken.None).ConfigureAwait(true);
        });
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _categories.CategoriesChanged -= OnCategoriesChanged;
        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= OnAutosaveTick;
    }
}
