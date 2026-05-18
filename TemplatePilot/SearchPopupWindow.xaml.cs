using System.Windows;
using System.Windows.Input;
using TemplatePilot.ViewModels;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TemplatePilot.Views;

public partial class SearchPopupWindow : Window
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

    private void Window_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ViewModel.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && !TemplateList.IsKeyboardFocusWithin)
        {
            TemplateList.Focus();
            e.Handled = true;
        }
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
                || source is System.Windows.Controls.ListBoxItem)
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