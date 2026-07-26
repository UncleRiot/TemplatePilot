using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TemplatePilot.Models;
using TemplatePilot.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TemplatePilot.Views;

public partial class SearchPopupWindow : Wpf.Ui.Controls.FluentWindow
{
    public SearchPopupWindow(SearchPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SearchPopupViewModel ViewModel => (SearchPopupViewModel)DataContext;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FilterTextBox.Focus();
        FilterTextBox.SelectAll();
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
