using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TemplatePilot.Helpers;
using TemplatePilot.Services;
using TemplatePilot.ViewModels;

namespace TemplatePilot.Views;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly TrayService _tray;
    private readonly ApplicationLifetimeService _lifetime;
    private readonly WindowPlacementService _windowPlacement;
    private const double SettingsPanelExpandedWidth = 230;
    private bool _htmlInitialized;
    private bool _settingsPanelOpen;

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
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.InitializeCommand.Execute(null);
        await HtmlPreviewHelper.InitializeAsync(HtmlPreview).ConfigureAwait(true);
        _htmlInitialized = true;
        RenderHtmlPreview();
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



    private void SettingsToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_settingsPanelOpen)
        {
            return;
        }

        _settingsPanelOpen = true;
        SettingsPanel.Visibility = Visibility.Visible;
        Width += SettingsPanelExpandedWidth;
    }

    private void SettingsToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsPanelOpen)
        {
            return;
        }

        _settingsPanelOpen = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        Width = Math.Max(MinWidth, Width - SettingsPanelExpandedWidth);
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
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.SelectedTemplate))
        {
            RenderHtmlPreview();
        }
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
