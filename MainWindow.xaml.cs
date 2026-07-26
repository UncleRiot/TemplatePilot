using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TemplatePilot.Helpers;
using TemplatePilot.Services;
using TemplatePilot.ViewModels;
using Wpf.Ui.Controls;

namespace TemplatePilot.Views;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsService _settings;
    private readonly TrayService _tray;
    private readonly ApplicationLifetimeService _lifetime;
    private readonly WindowPlacementService _windowPlacement;
    private bool _htmlInitialized;

    public MainWindow(
        MainWindowViewModel viewModel,
        SettingsService settings,
        TrayService tray,
        ApplicationLifetimeService lifetime,
        WindowPlacementService windowPlacement)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        _tray = tray;
        _lifetime = lifetime;
        _windowPlacement = windowPlacement;
        _windowPlacement.Attach(this, "MainWindow");
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTemplatesGridHeaders();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactTemplatesGridStyles();
        ViewModel.InitializeCommand.Execute(null);
        await HtmlPreviewHelper.InitializeAsync(HtmlPreview).ConfigureAwait(true);
        _htmlInitialized = true;
        RenderHtmlPreview();
    }

    private void ApplyCompactTemplatesGridStyles()
    {
        var rowStyle = new Style(typeof(System.Windows.Controls.DataGridRow), TemplatesGrid.RowStyle);
        rowStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        rowStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        TemplatesGrid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(System.Windows.Controls.DataGridCell), TemplatesGrid.CellStyle);
        cellStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24d));
        TemplatesGrid.CellStyle = cellStyle;

        var textStyle = new Style(typeof(System.Windows.Controls.TextBlock), TemplatesGrid.TextColumnElementStyle);
        textStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0)));
        textStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        TemplatesGrid.TextColumnElementStyle = textStyle;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_lifetime.IsShutdownRequested)
        {
            return;
        }

        if (_settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            _tray.HideMainWindow();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        }

        HtmlPreview.Dispose();
    }





    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ViewModel.IsEditorOpen)
        {
            return;
        }

        ViewModel.CancelActiveEditor();
        e.Handled = true;
    }

    private void NavigationList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsEditorOpen)
        {
            ViewModel.CancelActiveEditor();
        }
    }

    private void OpenTemplates_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedNavigationIndex = 1;
    }

    private void TemplatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.EditTemplateCommand.CanExecute(null))
        {
            ViewModel.EditTemplateCommand.Execute(null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(MainWindowViewModel.KeyLabel)
            || e.PropertyName == nameof(MainWindowViewModel.CategoryLabel))
        {
            UpdateTemplatesGridHeaders();
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.SelectedTemplate))
        {
            RenderHtmlPreview();
        }
    }

    private void UpdateTemplatesGridHeaders()
    {
        TemplateKeyColumn.Header = ViewModel.KeyLabel;
        TemplateCategoryColumn.Header = ViewModel.CategoryLabel;
    }

    private void RenderHtmlPreview()
    {
        if (!_htmlInitialized)
        {
            return;
        }

        var html = ViewModel.SelectedTemplate?.Content.Html ?? string.Empty;
        HtmlPreviewHelper.NavigateSafeHtml(HtmlPreview, html);
    }
}
