using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly AppThemeService _themeService;
    private readonly SearchPopupService _searchPopupService;
    private bool _htmlInitialized;
    private bool _isNavigationPaneCollapsed;
    private bool _viewStateReady;

    public MainWindow(
        MainWindowViewModel viewModel,
        SettingsService settings,
        TrayService tray,
        ApplicationLifetimeService lifetime,
        WindowPlacementService windowPlacement,
        AppThemeService themeService,
        SearchPopupService searchPopupService)
    {
        InitializeComponent();
        _themeService = themeService;
        _searchPopupService = searchPopupService;
        // Semantic Anchor: FontSizeSliderCommitWiring
        // SSOT: the slider's explicit binding commits to the existing ViewModel property only after interaction completes.
        // Invariant: drag gestures update their local live previews without causing parent-window relayout under the pointer.
        MainWindowFontSizeSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(FontSizeSlider_DragCompleted));
        SearchPopupFontSizeSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(FontSizeSlider_DragCompleted));
        DataContext = viewModel;
        _settings = settings;
        _isNavigationPaneCollapsed = settings.Current.IsNavigationPaneCollapsed;
        _tray = tray;
        _lifetime = lifetime;
        _windowPlacement = windowPlacement;
        _windowPlacement.Attach(this, "MainWindow");
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.UpdateDialogRequested += OnUpdateDialogRequested;
        viewModel.DeleteConfirmationRequested += OnDeleteConfirmationRequested;
        UpdateTemplatesGridHeaders();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    /// <summary>
    /// Semantic Anchor: FontSizeSliderDeferredCommit
    /// SSOT: each slider has exactly one explicit TwoWay binding to its existing ViewModel setting.
    /// Invariants: preview bindings remain live while dragging; the main typography and persisted values
    /// update only after the drag ends or via a completed non-drag input (keyboard/track click).
    /// </summary>
    private static void CommitFontSizeSlider(System.Windows.Controls.Slider slider)
    {
        slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is System.Windows.Controls.Slider slider
            && slider.IsLoaded
            && !slider.IsMouseCaptureWithin)
        {
            CommitFontSizeSlider(slider);
        }
    }

    private void FontSizeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider slider)
        {
            CommitFontSizeSlider(slider);
        }
    }

    private void FontSizeSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider slider)
        {
            CommitFontSizeSlider(slider);
        }
    }

    // Semantic Anchor: SettingsSectionPreviewLifecycle
    // Invariant: the real non-interactive popup is visible iff the dedicated settings
    // tab is selected and the main window can display it. Switching tabs closes it.
    private void SettingsSectionTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsSectionTabs))
        {
            UpdateSettingsPopupPreview();
        }
    }

    private void UpdateSettingsPopupPreview()
    {
        if (!IsLoaded)
        {
            return;
        }

        // Unsaved changes affect both the live settings preview and subsequent hotkey
        // popups in this process. No settings file is modified before Save.
        _searchPopupService.SetPendingPopupVisualSettings(ViewModel.SearchPopupBlurRadius,
            ViewModel.SearchPopupFontSize, ViewModel.SearchPopupVisibleLines,
            ViewModel.SearchPopupPreviewPosition);

        if (!IsVisible || WindowState == WindowState.Minimized
            || ViewModel.SelectedNavigationIndex != 1 || SettingsSectionTabs.SelectedIndex != 1)
        {
            _searchPopupService.CloseSettingsBlurPreview();
            return;
        }

        _searchPopupService.ShowSettingsPopupPreview(this, ViewModel.SearchPopupBlurRadius,
            ViewModel.SearchPopupFontSize, ViewModel.SearchPopupVisibleLines,
            ViewModel.SearchPopupPreviewPosition);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactTemplatesGridStyles();
        ApplyNavigationPaneState();
        ApplyPreviewTabVisibility();
        UpdateSettingsPopupPreview();
        // Semantic Anchor: ViewStatePersistenceActivation | Invariant: persist only after restored view state has been applied.
        _viewStateReady = true;
        ViewModel.InitializeCommand.Execute(null);
        await HtmlPreviewHelper.InitializeAsync(HtmlPreview).ConfigureAwait(true);
        _htmlInitialized = true;
        RenderHtmlPreview();
    }

    /// <summary>
    /// Semantic Anchor: TemplatesGridVisualContract
    /// SSOT: This method is the single runtime source for compact grid density and selection visuals in the templates pane.
    /// Invariants: selected rows must not shift horizontally, the current cell must not add an accent indent, selection visibility must remain clearer than the normal row background, and row/cell minimum heights must not override the font-derived row height.
    /// </summary>
    private void ApplyCompactTemplatesGridStyles()
    {
        object? selectedRowBackground = TryFindResource("ControlFillColorSecondaryBrush")
            ?? TryFindResource("CardBackgroundFillColorDefaultBrush")
            ?? Brushes.LightGray;
        object? selectedRowForeground = TryFindResource("TextFillColorPrimaryBrush")
            ?? Brushes.Black;

        var rowStyle = new Style(typeof(System.Windows.Controls.DataGridRow), TemplatesGrid.RowStyle);
        rowStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        rowStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));

        var selectedRowTrigger = new Trigger
        {
            Property = System.Windows.Controls.DataGridRow.IsSelectedProperty,
            Value = true
        };
        selectedRowTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, selectedRowBackground));
        selectedRowTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, selectedRowForeground));
        rowStyle.Triggers.Add(selectedRowTrigger);

        // Semantic Anchor: GridRowStableGeometry
        // SSOT: the row template owns the cells presenter positioning for every selection state.
        // Invariants: selection changes only the background/foreground; no row header, accent stripe,
        // state-specific margin or padding may shift the cells presenter horizontally.
        var rowTemplate = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.DataGridRow));
        var rowBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        rowBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        rowBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(0));
        rowBorder.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(0));
        var rowContent = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.DataGridCellsPresenter));
        rowContent.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        rowContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        rowBorder.AppendChild(rowContent);
        rowTemplate.VisualTree = rowBorder;
        rowStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, rowTemplate));
        TemplatesGrid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(System.Windows.Controls.DataGridCell), TemplatesGrid.CellStyle);
        cellStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        cellStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(System.Windows.Controls.Control.FocusVisualStyleProperty, null));

        // Semantic Anchor: GridCellStableGeometry
        // SSOT: the cell template controls selected and unselected content geometry.
        // Invariant: selection never inserts an indicator, indentation or border that moves cell content.
        var cellTemplate = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.DataGridCell));
        var cellBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        cellBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        cellBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(0));
        var cellContent = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        cellContent.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.ContentControl.ContentProperty));
        cellContent.SetValue(System.Windows.Controls.ContentPresenter.ContentTemplateProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.ContentControl.ContentTemplateProperty));
        cellContent.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cellContent.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cellBorder.AppendChild(cellContent);
        cellTemplate.VisualTree = cellBorder;
        cellStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, cellTemplate));

        var selectedCellTrigger = new Trigger
        {
            Property = System.Windows.Controls.DataGridCell.IsSelectedProperty,
            Value = true
        };
        selectedCellTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, selectedRowBackground));
        selectedCellTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, selectedRowForeground));
        selectedCellTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));
        selectedCellTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        cellStyle.Triggers.Add(selectedCellTrigger);
        TemplatesGrid.CellStyle = cellStyle;

        // Semantic Anchor: GeneratedGridTextTypography
        // SSOT: generated DataGridTextColumn text uses the same MainWindowFontSize as TemplatesGrid.
        // Invariants: changing typography does not affect cell positioning, row selection templates, or icon sizes.
        var textStyle = new Style(typeof(System.Windows.Controls.TextBlock), TemplatesGrid.TextColumnElementStyle);
        textStyle.Setters.Add(new Setter(System.Windows.Controls.TextBlock.FontSizeProperty,
            new System.Windows.Data.Binding(nameof(TemplatesGrid.FontSize)) { Source = TemplatesGrid }));
        textStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 2, 0)));
        textStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        TemplatesGrid.TextColumnElementStyle = textStyle;
    }

    /// <summary>
    /// Semantic Anchor: NavigationPaneCollapseState
    /// SSOT: This method owns the expanded/collapsed visual state of the left navigation pane.
    /// Invariants: collapse may only change pane width and non-essential text visibility; navigation selection, item order, icons, and command bindings must remain unchanged.
    /// </summary>
    private void ApplyNavigationPaneState()
    {
        NavigationPaneColumn.Width = _isNavigationPaneCollapsed
            ? new GridLength(56)
            : new GridLength(220);

        // Semantic Anchor: CollapsedNavigationGeometry
        // SSOT: the collapsed content width is calculated from the existing NavigationPaneColumn.
        // Invariant: 56 DIP = 40 DIP button + 2 * (4 DIP outside margin + 1 DIP border + 3 DIP padding).
        // Buttons and symbols remain fully visible, with exactly 8 DIP of space on each side;
        // the expanded pane, navigation behavior and icon sizes remain unchanged.
        NavigationPaneBorder.Margin = _isNavigationPaneCollapsed
            ? new Thickness(4, 8, 4, 12)
            : new Thickness(12, 8, 6, 12);
        NavigationPaneBorder.Padding = _isNavigationPaneCollapsed
            ? new Thickness(3)
            : new Thickness(10);
        // Semantic Anchor: CollapsedNavigationSquareButtons
        // SSOT: this state transition defines collapsed navigation item dimensions and alignment.
        // Invariants: items are square and icons are centered; expanded geometry and navigation behavior are preserved.
        foreach (System.Windows.Controls.ListBoxItem item in NavigationList.Items)
        {
            item.Padding = _isNavigationPaneCollapsed
                ? new Thickness(0)
                : new Thickness(12, 8, 12, 8);
            item.Width = _isNavigationPaneCollapsed ? 40 : double.NaN;
            item.Height = _isNavigationPaneCollapsed ? 40 : double.NaN;
            item.MinHeight = _isNavigationPaneCollapsed ? 40 : 42;
            item.HorizontalAlignment = _isNavigationPaneCollapsed
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Stretch;
            item.HorizontalContentAlignment = _isNavigationPaneCollapsed
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch;
        }
        NavigationPaneToggleButton.Width = _isNavigationPaneCollapsed ? 40 : 34;
        NavigationPaneToggleButton.Height = _isNavigationPaneCollapsed ? 40 : 30;
        NavigationPaneToggleButton.HorizontalAlignment = _isNavigationPaneCollapsed
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        Thickness iconMargin = _isNavigationPaneCollapsed
            ? new Thickness(0)
            : new Thickness(0, 0, 10, 0);
        TemplatesNavigationIcon.Margin = iconMargin;
        SettingsNavigationIcon.Margin = iconMargin;
        AboutNavigationIcon.Margin = iconMargin;

        NavigationPaneFooter.Visibility = _isNavigationPaneCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

        TemplatesNavigationText.Visibility = _isNavigationPaneCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

        SettingsNavigationText.Visibility = _isNavigationPaneCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

        AboutNavigationText.Visibility = _isNavigationPaneCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;

        NavigationPaneToggleButton.Content = _isNavigationPaneCollapsed ? "›" : "‹";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _searchPopupService.CloseSettingsBlurPreview();
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

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateSettingsPopupPreview();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateSettingsPopupPreview();
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            _tray.HideMainWindow();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _searchPopupService.CloseSettingsBlurPreview();
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.UpdateDialogRequested -= OnUpdateDialogRequested;
            viewModel.DeleteConfirmationRequested -= OnDeleteConfirmationRequested;
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

    /// <summary>
    /// Semantic Anchor: CategoryContextEdit
    /// SSOT: SelectedCategory synchronizes the existing CategoryEditText; editing only focuses that field.
    /// Invariant: Edit never renames a category until the existing SaveCategoryCommand is explicitly executed.
    /// </summary>
    private void EditCategoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CategoryNameTextBox.Focus();
        CategoryNameTextBox.SelectAll();
    }

    /// <summary>
    /// Semantic Anchor: CategoryContextMenuTargetSelection
    /// SSOT: SelectedCategory targets the existing category commands and the category edit field.
    /// Invariants: right-click always acts on the clicked category without changing command semantics;
    /// context actions never operate on a previously selected category by mistake.
    /// </summary>
    private void CategoriesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(CategoriesList, source)
                is not System.Windows.Controls.ListBoxItem item)
        {
            return;
        }

        item.IsSelected = true;
    }

    private void NavigationPaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isNavigationPaneCollapsed = !_isNavigationPaneCollapsed;
        ApplyNavigationPaneState();
        _ = PersistViewStateAsync();
    }

    /// <summary>
    /// Semantic Anchor: MovableActionDialogs
    /// SSOT: These two action paths use the same native, owner-bound FluentWindow dialog.
    /// Invariants: the title bar remains draggable, confirmation defaults to non-destructive,
    /// only an affirmative button returns true, and the dialog follows the current app theme.
    /// </summary>
    private Task<bool> OnDeleteConfirmationRequested(DeleteConfirmationRequest request)
    {
        bool accepted = ShowMovableActionDialog(
            request.Title,
            request.Message,
            request.PrimaryButtonText,
            request.CloseButtonText);
        return Task.FromResult(accepted);
    }

    private void OnUpdateDialogRequested(string message, bool showOpenReleaseButton)
    {
        bool accepted = ShowMovableActionDialog(
            ViewModel.AppTitle,
            message,
            showOpenReleaseButton ? ViewModel.OpenReleaseText : null,
            showOpenReleaseButton ? ViewModel.CancelText : ViewModel.OkText);

        if (accepted && showOpenReleaseButton)
        {
            ViewModel.OpenReleasesCommand.Execute(null);
        }
    }

    /// <summary>
    /// Semantic Anchor: MovableActionDialogWindow
    /// SSOT: Action dialogs use the same WPF-UI FluentWindow chrome and explicit TitleBar as MainWindow.
    /// Invariants: the title bar remains reachable and draggable; content never overlaps chrome;
    /// dismissing the dialog cannot confirm an action; only the explicit primary button returns true.
    /// </summary>
    private bool ShowMovableActionDialog(
        string title,
        string message,
        string? confirmText,
        string closeText)
    {
        var dialog = new FluentWindow
        {
            Title = title,
            Owner = this,
            Width = 460,
            Height = 210,
            MinWidth = 360,
            MinHeight = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true
        };
        // Semantic Anchor: MovableDialogThemeRegistration
        // Invariant: action dialogs stay movable and follow the same theme as their owner.
        _themeService.RegisterSecondaryWindow(dialog);

        var layout = new System.Windows.Controls.Grid();
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = System.Windows.GridLength.Auto
        });
        layout.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });

        var titleBar = new Wpf.Ui.Controls.TitleBar { Title = title };
        System.Windows.Controls.Grid.SetRow(titleBar, 0);
        layout.Children.Add(titleBar);

        var body = new System.Windows.Controls.Grid { Margin = new Thickness(20, 14, 20, 18) };
        body.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });
        body.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = System.Windows.GridLength.Auto
        });
        body.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        });

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (!string.IsNullOrWhiteSpace(confirmText))
        {
            var confirm = new Wpf.Ui.Controls.Button
            {
                Content = confirmText,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                Margin = new Thickness(0, 0, 12, 0),
                MinWidth = 84
            };
            confirm.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(confirm);
        }

        var cancel = new Wpf.Ui.Controls.Button { Content = closeText, MinWidth = 84 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);
        System.Windows.Controls.Grid.SetRow(body, 1);
        layout.Children.Add(body);
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Semantic Anchor: TemplateContextMenuTargetSelection
    /// SSOT: TemplatesGrid.ContextMenu supplies the existing template actions; SelectedTemplate
    /// remains their sole target through the existing DataGrid selection binding.
    /// Invariants: a right-click on a data row selects exactly that row and opens the template
    /// menu, never an inherited text-editing menu. Commands, inline-edit restrictions and
    /// keyboard/left-click behavior are unchanged.
    /// </summary>
    private void TemplatesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(TemplatesGrid, source)
                is not System.Windows.Controls.DataGridRow row)
        {
            return;
        }

        row.IsSelected = true;

        // The cell's own text context menu can otherwise take precedence over the grid menu.
        // Handle the preview event before child controls receive the click and open the
        // existing command menu explicitly; do not duplicate or reimplement its commands.
        if (TemplatesGrid.ContextMenu is System.Windows.Controls.ContextMenu menu)
        {
            e.Handled = true;
            menu.PlacementTarget = TemplatesGrid;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }

    private void TemplatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.EditTemplateCommand.CanExecute(null))
        {
            ViewModel.EditTemplateCommand.Execute(null);
        }
    }

    /// <summary>
    /// Semantic Anchor: PreviewTabVisibility
    /// SSOT: ActiveTabIndex is the sole source for preview visibility and allocated row height.
    /// Invariants: template preview is available only on Templates; category operations and selection stay independent of preview layout.
    /// </summary>
    private void ApplyPreviewTabVisibility()
    {
        bool showPreview = ViewModel.ActiveTabIndex == 0;
        PreviewSplitter.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        TemplatePreviewBorder.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitterRow.Height = showPreview ? new GridLength(8) : new GridLength(0);
        PreviewContentRow.MinHeight = showPreview ? 180 : 0;
        PreviewContentRow.Height = showPreview ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Semantic Anchor: SettingsVisualValuesBeforeSave
        // SSOT: MainWindowViewModel owns pending settings; the active shortcut preview
        // consumes them immediately, but only SaveSettingsAsync writes AppSettings.
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTheme)
            || e.PropertyName == nameof(MainWindowViewModel.SelectedGlassTransparencyCode))
        {
            _themeService.ApplyTheme(ViewModel.GetPendingThemeCode(), ViewModel.SelectedGlassTransparencyCode);
        }

        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(MainWindowViewModel.SelectedNavigationIndex)
            || e.PropertyName == nameof(MainWindowViewModel.SearchPopupFontSize)
            || e.PropertyName == nameof(MainWindowViewModel.SearchPopupVisibleLines)
            || e.PropertyName == nameof(MainWindowViewModel.SearchPopupBlurRadius)
            || e.PropertyName == nameof(MainWindowViewModel.SearchPopupPreviewPosition))
        {
            UpdateSettingsPopupPreview();
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.ActiveTabIndex))
        {
            ApplyPreviewTabVisibility();
        }

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

    /// <summary>
    /// Semantic Anchor: PersistNavigationPaneState
    /// SSOT: SettingsService.UpdateAsync persists the collapsible pane preference only.
    /// Invariants: app startup always opens Templates, and changing pages does not persist a startup page;
    /// other settings (including legacy view indexes) remain unmodified.
    /// </summary>
    private async Task PersistViewStateAsync()
    {
        if (!_viewStateReady)
        {
            return;
        }

        bool collapsed = _isNavigationPaneCollapsed;
        try
        {
            await _settings.UpdateAsync(settings =>
            {
                settings.IsNavigationPaneCollapsed = collapsed;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save view state: {exception}");
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
