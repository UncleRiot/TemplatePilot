using System.Windows;
using TemplatePilot.ViewModels;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;

namespace TemplatePilot.Views;

public partial class TemplateEditorWindow : Window
{
    public TemplateEditorWindow(TemplateEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    private TemplateEditorViewModel ViewModel => (TemplateEditorViewModel)DataContext;

    private void OnRequestClose(object? sender, bool saved)
    {
        DialogResult = saved;
        Close();
    }

    private void Window_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WpfDataFormats.FileDrop) ? WpfDragDropEffects.Copy : WpfDragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(WpfDataFormats.FileDrop) is string[] files)
        {
            ViewModel.AddDroppedFiles(files);
        }
    }

    private void RtfUndo_Click(object sender, RoutedEventArgs e)
    {
        if (RtfEditor.CanUndo)
        {
            RtfEditor.Undo();
        }
    }

    private void RtfRedo_Click(object sender, RoutedEventArgs e)
    {
        if (RtfEditor.CanRedo)
        {
            RtfEditor.Redo();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
    }
}