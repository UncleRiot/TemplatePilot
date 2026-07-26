using System.Windows;
using System.Windows.Controls;
using TemplatePilot.ViewModels;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;

namespace TemplatePilot.Views;

public partial class TemplateEditorView : UserControl
{
    public TemplateEditorView()
    {
        InitializeComponent();
    }

    private TemplateEditorViewModel? ViewModel => DataContext as TemplateEditorViewModel;

    private void Editor_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WpfDataFormats.FileDrop)
            ? WpfDragDropEffects.Copy
            : WpfDragDropEffects.None;
        e.Handled = true;
    }

    private void Editor_Drop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(WpfDataFormats.FileDrop) is string[] files)
        {
            ViewModel?.AddDroppedFiles(files);
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
}
