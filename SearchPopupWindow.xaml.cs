using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Interop;
using TemplatePilot.Interop;
using System.Windows.Threading;
using TemplatePilot.Models;
using TemplatePilot.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TemplatePilot.Views;

public partial class SearchPopupWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _restoringTemplateListColumns;
    private bool _isPreviewPanelExpanded = true;
    private int _blurRadius;
    private SearchPopupCompositionBackdrop? _compositionBackdrop;
    private HwndSource? _windowSource;
    private bool _compositionUnavailable;

    // Semantic Anchor: ShortcutSettingsPreviewIsolation
    // The settings preview is the real SearchPopupWindow, not a synthetic mock.
    // Invariants: no insertion, text filtering, column persistence or focus stealing in preview mode.
    public bool IsSettingsPreview { get; set; }

    // Semantic Anchor: ShortcutBackdropBlurRuntime
    // SSOT: AppSettings/SearchPopupBlurRadius controls the strength of the live
    // composition backdrop; the Windows GPU compositor owns all background pixels.
    // Invariants: zero disables composition; changes update the live visual directly;
    // WPF text and controls are not blurred and no desktop screenshots are retained.
    public void SetBackdropBlur(int radius)
    {
        _blurRadius = Math.Clamp(radius, 0, 32);
        if (!IsLoaded || !IsVisible || !AllowsTransparency || _blurRadius == 0)
        {
            StopCompositionBackdrop();
            return;
        }

        if (_compositionUnavailable)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            _compositionBackdrop ??= SearchPopupCompositionBackdrop.Create(handle);
            _compositionBackdrop.SetStrength(_blurRadius);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Shortcut live composition is unavailable: {exception.Message}");
            _compositionUnavailable = true;
            StopCompositionBackdrop();
        }
    }

    private void StopCompositionBackdrop()
    {
        _compositionBackdrop?.Dispose();
        _compositionBackdrop = null;
    }

    // Semantic Anchor: ShortcutCompositionWindowPosition
    // WM_WINDOWPOSCHANGED runs during the native DragMove loop, independent of
    // dispatcher timers. The GPU underlay is placed behind this window at the same
    // physical pixel rectangle; it must not intercept input or change focus.
    private IntPtr PopupWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_compositionBackdrop is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (message == 0x0047) // WM_WINDOWPOSCHANGED
            {
                _compositionBackdrop.Sync(hwnd);
            }
            else if (message == 0x0018) // WM_SHOWWINDOW
            {
                if (wParam == IntPtr.Zero)
                {
                    _compositionBackdrop.Hide();
                }
                else
                {
                    _compositionBackdrop.Sync(hwnd);
                }
            }
            else if (message == 0x0005 && wParam == new IntPtr(1)) // WM_SIZE, minimized
            {
                _compositionBackdrop.Hide();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Shortcut composition position synchronization failed: {exception.Message}");
            _compositionUnavailable = true;
            StopCompositionBackdrop();
        }

        return IntPtr.Zero;
    }

    public SearchPopupWindow(SearchPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SearchPopupViewModel ViewModel => (SearchPopupViewModel)DataContext;

    // Semantic Anchor: SearchPopupSettingsLiveVisualUpdate
    // SSOT: the settings preview's model owns temporary values, while this window
    // updates the existing layout and compositor. No persistent settings are modified.
    public void ApplySettingsPreview(int visibleLines, int fontSize, string position, int blurStrength)
    {
        if (!IsSettingsPreview)
        {
            return;
        }

        bool wasBelow = ViewModel.IsSearchPopupPreviewBelow;
        ViewModel.ApplySettingsPreview(visibleLines, fontSize, position);
        if (IsLoaded && wasBelow != ViewModel.IsSearchPopupPreviewBelow)
        {
            ApplyPreviewPanelState();
        }
        SetBackdropBlur(blurStrength);
    }

    // Semantic Anchor: SearchPopupLayeredWpfChrome
    // SSOT: AllowsTransparency is fixed by SearchPopupService before the HWND exists.
    // Invariants: WPF-UI must not replace WindowStyle=None or reset the layered window's
    // transparent composition target. The native backdrop path remains available for
    // ordinary FluentWindows; popup resizing remains handled by WindowChrome.
    protected override void OnExtendsContentIntoTitleBarChanged(bool oldValue, bool newValue)
    {
        if (!AllowsTransparency)
        {
            base.OnExtendsContentIntoTitleBarChanged(oldValue, newValue);
        }
    }

    protected override void OnBackdropTypeChanged(
        Wpf.Ui.Controls.WindowBackdropType oldValue,
        Wpf.Ui.Controls.WindowBackdropType newValue)
    {
        if (!AllowsTransparency)
        {
            base.OnBackdropTypeChanged(oldValue, newValue);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (AllowsTransparency)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(4),
                UseAeroCaptionButtons = false
            });
        }

        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(PopupWindowHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(PopupWindowHook);
        _windowSource = null;
        StopCompositionBackdrop();
        base.OnClosed(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreTemplateListColumns();
        if (IsSettingsPreview)
        {
            PopupSurface.IsHitTestVisible = false;
        }
        // Semantic Anchor: SearchPreviewPanelToggle | SSOT: preview visibility is controlled only by this state, never by the persisted DataGrid preview column.
        ApplyPreviewPanelState();
        TemplateList.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(TemplateList_ColumnWidthDragCompleted),
            true);

        Dispatcher.BeginInvoke(() =>
        {
            HideLeftColumnHeaderGrippers();
        }, DispatcherPriority.Loaded);

        if (!IsSettingsPreview)
        {
            FilterTextBox.Focus();
            FilterTextBox.SelectAll();
        }

        // Start OS composition after the popup HWND and its WPF content are ready.
        SetBackdropBlur(_blurRadius);
    }


    /// <summary>
    /// Semantic Anchor: SearchPreviewPanelState
    /// SSOT: SearchPopupViewModel reads the persisted placement and split ratios.
    /// Invariants: collapsed preview consumes no space; Right and Below never
    /// coexist; both splitters resize only the intended adjacent panels; existing
    /// selection, insertion, theme and backdrop behavior is unchanged.
    /// </summary>
    private void ApplyPreviewPanelState()
    {
        var below = ViewModel.IsSearchPopupPreviewBelow;
        var showRight = _isPreviewPanelExpanded && !below;
        var showBelow = _isPreviewPanelExpanded && below;
        var rightRatio = ViewModel.GetSearchPopupPreviewRatio(false);
        var bottomRatio = ViewModel.GetSearchPopupPreviewRatio(true);

        SearchResultsColumn.Width = showRight
            ? new GridLength(1d - rightRatio, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        PreviewSplitterColumn.Width = new GridLength(showRight ? 8 : 0);
        PreviewPanelColumn.MinWidth = showRight ? 120 : 0;
        PreviewPanelColumn.Width = showRight
            ? new GridLength(rightRatio, GridUnitType.Star)
            : new GridLength(0);

        SearchResultsRow.Height = showBelow
            ? new GridLength(1d - bottomRatio, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        PreviewSplitterRow.Height = new GridLength(showBelow ? 8 : 0);
        PreviewPanelRow.MinHeight = showBelow ? 80 : 0;
        PreviewPanelRow.Height = showBelow
            ? new GridLength(bottomRatio, GridUnitType.Star)
            : new GridLength(0);

        Grid.SetRow(PreviewPanel, showBelow ? 2 : 0);
        Grid.SetColumn(PreviewPanel, showBelow ? 0 : 2);
        PreviewPanel.Visibility = _isPreviewPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        PreviewVerticalSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        PreviewHorizontalSplitter.Visibility = showBelow ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanelToggleButton.Content = below
            ? (_isPreviewPanelExpanded ? "⌄" : "⌃")
            : (_isPreviewPanelExpanded ? "›" : "‹");
    }

    // Semantic Anchor: SearchPopupPreviewRatioPersistence
    // Invariants: only real user drags save to SettingsService; the preview window
    // never changes saved geometry; saving occurs once on drag completion.
    private async void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled || IsSettingsPreview || !_isPreviewPanelExpanded)
        {
            return;
        }

        var below = ViewModel.IsSearchPopupPreviewBelow;
        var listExtent = below ? SearchResultsRow.ActualHeight : SearchResultsColumn.ActualWidth;
        var previewExtent = below ? PreviewPanelRow.ActualHeight : PreviewPanelColumn.ActualWidth;
        var availableExtent = listExtent + previewExtent;
        if (availableExtent > 0 && double.IsFinite(availableExtent))
        {
            try
            {
                await ViewModel.SaveSearchPopupPreviewRatioAsync(below, previewExtent / availableExtent);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Shortcut preview size could not be saved: {exception.Message}");
            }
        }
    }

    private void PreviewPanelToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isPreviewPanelExpanded = !_isPreviewPanelExpanded;
        ApplyPreviewPanelState();
    }

    private void HideLeftColumnHeaderGrippers()
    {
        foreach (var header in FindVisualChildren<DataGridColumnHeader>(TemplateList))
        {
            if (header.Template.FindName("PART_LeftHeaderGripper", header) is Thumb leftHeaderGripper)
            {
                leftHeaderGripper.Visibility = Visibility.Collapsed;
                leftHeaderGripper.IsHitTestVisible = false;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void RestoreTemplateListColumns()
    {
        var settings = ViewModel.GetSearchPopupColumns()
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var columns = GetTemplateListColumns();
        var validDisplayIndexes = columns.All(x => settings.ContainsKey(x.Key))
            && columns
                .Select(x => settings[x.Key].DisplayIndex)
                .All(x => x >= 0 && x < columns.Count)
            && columns
                .Select(x => settings[x.Key].DisplayIndex)
                .Distinct()
                .Count() == columns.Count;

        _restoringTemplateListColumns = true;

        try
        {
            foreach (var (key, column) in columns)
            {
                if (!settings.TryGetValue(key, out var columnSettings))
                {
                    continue;
                }

                if (columnSettings.Width > 0 && !double.IsNaN(columnSettings.Width) && !double.IsInfinity(columnSettings.Width))
                {
                    column.Width = new DataGridLength(columnSettings.Width);
                }
            }

            if (validDisplayIndexes)
            {
                foreach (var (key, column) in columns.OrderBy(x => settings[x.Key].DisplayIndex))
                {
                    column.DisplayIndex = settings[key].DisplayIndex;
                }
            }

        }
        finally
        {
            _restoringTemplateListColumns = false;
        }
    }

    private async void TemplateList_ColumnWidthDragCompleted(object sender, DragCompletedEventArgs e)
    {
        await SaveTemplateListColumnsAsync();
    }

    private async void TemplateList_ColumnReordered(object sender, DataGridColumnEventArgs e)
    {
        await SaveTemplateListColumnsAsync();
    }

    private async Task SaveTemplateListColumnsAsync()
    {
        if (_restoringTemplateListColumns || IsSettingsPreview)
        {
            return;
        }

        await ViewModel.SaveSearchPopupColumnsAsync(GetTemplateListColumns()
            .Select(x => new SearchPopupColumnSettings
            {
                Key = x.Key,
                DisplayIndex = x.Column.DisplayIndex,
                Width = x.Column.ActualWidth
            }));
    }

    private List<(string Key, DataGridColumn Column)> GetTemplateListColumns()
    {
        return new List<(string Key, DataGridColumn Column)>
        {
            ("Key", KeyColumn),
            ("Category", CategoryColumn),
            ("Preview", PreviewColumn)
        };
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (IsSettingsPreview)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            ViewModel.ConfirmCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            MoveTemplateSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            MoveTemplateSelection(-1);
            e.Handled = true;
        }
    }

    private void MoveTemplateSelection(int direction)
    {
        var templates = ViewModel.TemplatesView
            .Cast<TemplatePackage>()
            .ToList();

        if (templates.Count == 0)
        {
            return;
        }

        var currentIndex = ViewModel.SelectedTemplate is null
            ? -1
            : templates.IndexOf(ViewModel.SelectedTemplate);

        var nextIndex = currentIndex < 0
            ? 0
            : Math.Clamp(currentIndex + direction, 0, templates.Count - 1);

        ViewModel.SelectedTemplate = templates[nextIndex];
        TemplateList.Focus();

        Dispatcher.BeginInvoke(() =>
        {
            TemplateList.ScrollIntoView(ViewModel.SelectedTemplate);
        }, DispatcherPriority.Input);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsSettingsPreview)
        {
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.TextBox
                || source is System.Windows.Controls.Primitives.ButtonBase
                || source is System.Windows.Controls.CheckBox
                || source is System.Windows.Controls.ListBox
                || source is System.Windows.Controls.ListBoxItem
                || source is System.Windows.Controls.DataGrid
                || source is System.Windows.Controls.DataGridRow
                || source is System.Windows.Controls.DataGridCell
                || source is GridSplitter
                || source is Thumb)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void TemplateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsSettingsPreview)
        {
            return;
        }

        ViewModel.ConfirmCommand.Execute(null);
    }
}
