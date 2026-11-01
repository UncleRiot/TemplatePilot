using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using TemplatePilot.Models;
using TemplatePilot.Services;

namespace TemplatePilot.ViewModels;

public sealed class SearchPopupViewModel : ViewModelBase
{
    private readonly LocalizationService _localization;
    private readonly SettingsService _settings;
    private bool _updatingCategoryFilters;
    private string _filterText;
    private TemplatePackage? _selectedTemplate;
    private int? _previewFontSize;
    private int? _previewVisibleLines;
    private string? _previewPosition;

    public SearchPopupViewModel(LocalizationService localization, SettingsService settings, string initialQuery)
    {
        _localization = localization;
        _settings = settings;
        _filterText = initialQuery;

        TemplatesView = CollectionViewSource.GetDefaultView(Templates);
        TemplatesView.Filter = FilterTemplate;
        SelectedTemplate = TemplatesView.Cast<TemplatePackage>().FirstOrDefault();

        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, null));
        ClearCategoryFilterCommand = new RelayCommand(ClearCategoryFilter);
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public event EventHandler<TemplatePackage?>? RequestClose;

    public ObservableCollection<TemplatePackage> Templates { get; } = new();
    public ObservableCollection<PopupCategoryFilterItem> CategoryFilters { get; } = new();
    public ICollectionView TemplatesView { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCategoryFilterCommand { get; }

    public string HintText => _localization["SearchPopupHint"];
    public string EscapeKeyText => _localization["EscapeKey"];
    public string InsertText => _localization["Insert"];
    public string KeyLabel => _localization["Key"];
    public string NameLabel => _localization["Name"];
    public string CategoryLabel => _localization["Category"];
    public string PreviewLabel => _localization["Preview"];
    // Semantic Anchor: SearchPopupPreviewLayoutAccess
    // SSOT: the persisted AppSettings position and ratios are shared by normal popups
    // and the real, non-persisting settings preview. Only user-initiated splitter drags persist.
    public bool IsSearchPopupPreviewBelow => string.Equals(
        _previewPosition ?? _settings.Current.SearchPopupPreviewPosition, "Below", StringComparison.Ordinal);

    public double GetSearchPopupPreviewRatio(bool below)
    {
        return below ? _settings.Current.SearchPopupPreviewBottomRatio : _settings.Current.SearchPopupPreviewRightRatio;
    }

    public Task SaveSearchPopupPreviewRatioAsync(bool below, double ratio)
    {
        if (!double.IsFinite(ratio))
        {
            return Task.CompletedTask;
        }

        return _settings.UpdateAsync(settings =>
        {
            if (below)
            {
                settings.SearchPopupPreviewBottomRatio = ratio;
            }
            else
            {
                settings.SearchPopupPreviewRightRatio = ratio;
            }
        }, CancellationToken.None);
    }
    // Semantic Anchor: SearchPopupSettingsTransientPreview
    // SSOT: normal popups read AppSettings unless this instance receives in-memory
    // pending visual values; the settings preview always receives those pending values.
    // These overrides never update SettingsService or template data.
    public void ApplySettingsPreview(int visibleLines, int fontSize, string position)
    {
        _previewVisibleLines = Math.Clamp(visibleLines, 1, 10);
        _previewFontSize = Math.Clamp(fontSize, 10, 20);
        _previewPosition = string.Equals(position, "Below", StringComparison.OrdinalIgnoreCase)
            ? "Below" : "Right";
        OnPropertyChanged(nameof(SearchPopupVisibleLines));
        OnPropertyChanged(nameof(SearchPopupFontSize));
        OnPropertyChanged(nameof(SearchPopupTextMaxHeight));
    }

    // Semantic Anchor: PopupTypographyRuntime | SSOT: AppSettings is persisted;
    // in-memory visual overrides apply immediately to both popup presentation modes.
    public int SearchPopupFontSize => _previewFontSize ?? Math.Clamp(_settings.Current.SearchPopupFontSize, 10, 20);
    public int SearchPopupVisibleLines => _previewVisibleLines ?? Math.Clamp(_settings.Current.SearchPopupVisibleLines, 1, 10);
    public double SearchPopupTextMaxHeight => SearchPopupVisibleLines * (SearchPopupFontSize + 5d);
    public string CategoryFilterLabel => _localization["CategoryFilter"];
    public string AllCategoriesText => _localization["AllCategories"];
    public string CategoryFilterSummary
    {
        get
        {
            var selectedCount = CategoryFilters.Count(x => x.IsSelected);
            return selectedCount == 0
                ? AllCategoriesText
                : string.Format(_localization["SelectedCategories"], selectedCount);
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RefreshTemplatesView();
            }
        }
    }

    public TemplatePackage? SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetProperty(ref _selectedTemplate, value);
    }

    public void SetCategories(IEnumerable<string> categories)
    {
        _updatingCategoryFilters = true;

        foreach (var item in CategoryFilters)
        {
            item.SelectionChanged -= OnCategoryFilterSelectionChanged;
        }

        CategoryFilters.Clear();

        var selectedCategories = new HashSet<string>(
            _settings.Current.PopupCategoryFilter
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()),
            StringComparer.InvariantCultureIgnoreCase);

        foreach (var category in categories
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x.Trim())
                     .Distinct(StringComparer.InvariantCultureIgnoreCase)
                     .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase))
        {
            var item = new PopupCategoryFilterItem(category, selectedCategories.Contains(category));
            item.SelectionChanged += OnCategoryFilterSelectionChanged;
            CategoryFilters.Add(item);
        }

        _updatingCategoryFilters = false;
        OnPropertyChanged(nameof(CategoryFilterSummary));
        RefreshTemplatesView();
    }

    public void SetTemplates(IEnumerable<TemplatePackage> templates)
    {
        Templates.Clear();

        foreach (var template in templates)
        {
            Templates.Add(template);
        }

        RefreshTemplatesView();
    }

    private bool FilterTemplate(object item)
    {
        if (item is not TemplatePackage template)
        {
            return false;
        }

        if (!IsCategoryVisible(template.Metadata.Category))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Key, FilterText)
            || TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Name, FilterText)
            || TemplatePilot.Helpers.UnicodeTextNormalizer.ContainsKey(template.Metadata.Category, FilterText);
    }

    private bool IsCategoryVisible(string category)
    {
        var selectedCategories = CategoryFilters
            .Where(x => x.IsSelected)
            .Select(x => x.Name)
            .ToList();

        return selectedCategories.Count == 0
            || selectedCategories.Contains(category, StringComparer.InvariantCultureIgnoreCase);
    }

    private void RefreshTemplatesView()
    {
        TemplatesView.Refresh();
        SelectedTemplate = TemplatesView.Cast<TemplatePackage>().FirstOrDefault();
    }

    private void OnCategoryFilterSelectionChanged(object? sender, EventArgs e)
    {
        if (_updatingCategoryFilters)
        {
            return;
        }

        RefreshTemplatesView();
        OnPropertyChanged(nameof(CategoryFilterSummary));
        SaveCategoryFilter();
    }

    private void ClearCategoryFilter()
    {
        _updatingCategoryFilters = true;

        foreach (var item in CategoryFilters)
        {
            item.IsSelected = false;
        }

        _updatingCategoryFilters = false;
        RefreshTemplatesView();
        OnPropertyChanged(nameof(CategoryFilterSummary));
        SaveCategoryFilter();
    }

    private void SaveCategoryFilter()
    {
        var selectedCategories = CategoryFilters
            .Where(x => x.IsSelected)
            .Select(x => x.Name)
            .ToList();

        _ = _settings.UpdateAsync(settings =>
        {
            settings.PopupCategoryFilter = selectedCategories;
        }, CancellationToken.None);
    }

    public IReadOnlyList<SearchPopupColumnSettings> GetSearchPopupColumns()
    {
        return _settings.Current.SearchPopupColumns
            .Select(x => x.Clone())
            .ToList();
    }

    public async Task SaveSearchPopupColumnsAsync(IEnumerable<SearchPopupColumnSettings> columns)
    {
        var columnSettings = columns
            .Select(x => x.Clone())
            .ToList();

        await _settings.UpdateAsync(settings =>
        {
            settings.SearchPopupColumns = columnSettings;
        }, CancellationToken.None);
    }

    private void Confirm()
    {
        if (SelectedTemplate is not null)
        {
            RequestClose?.Invoke(this, SelectedTemplate);
        }
    }
}

public sealed class PopupCategoryFilterItem : ViewModelBase
{
    private bool _isSelected;

    public PopupCategoryFilterItem(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public event EventHandler? SelectionChanged;

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
