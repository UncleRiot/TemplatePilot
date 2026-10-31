using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TemplatePilot.Models;
using TemplatePilot.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TemplatePilot.Views;

public partial class SearchPopupWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool _restoringTemplateListColumns;
    private bool _isPreviewPanelExpanded = true;

    public SearchPopupWindow(SearchPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SearchPopupViewModel ViewModel => (SearchPopupViewModel)DataContext;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreTemplateListColumns();
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

        FilterTextBox.Focus();
        FilterTextBox.SelectAll();
    }


    /// <summary>
    /// Semantic Anchor: SearchPreviewPanelState
    /// Invariants: opening/closing only changes the right preview column and leaves the selected template and commands untouched.
    /// </summary>
    private void ApplyPreviewPanelState()
    {
        PreviewPanelColumn.Width = _isPreviewPanelExpanded ? new GridLength(280) : new GridLength(0);
        PreviewPanel.Visibility = _isPreviewPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanelToggleButton.Content = _isPreviewPanelExpanded ? "›" : "‹";
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
        if (_restoringTemplateListColumns)
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
                || source is System.Windows.Controls.DataGridCell)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void TemplateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel.ConfirmCommand.Execute(null);
    }
}
