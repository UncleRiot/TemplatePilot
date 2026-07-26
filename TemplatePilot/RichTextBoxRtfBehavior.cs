using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using WpfDataFormats = System.Windows.DataFormats;
using WpfRichTextBox = System.Windows.Controls.RichTextBox;
using WpfTextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;

namespace TemplatePilot.Behaviors;

public static class RichTextBoxRtfBehavior
{
    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(RichTextBoxRtfBehavior), new PropertyMetadata(false));

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached("IsHooked", typeof(bool), typeof(RichTextBoxRtfBehavior), new PropertyMetadata(false));

    public static readonly DependencyProperty RtfTextProperty =
        DependencyProperty.RegisterAttached(
            "RtfText",
            typeof(string),
            typeof(RichTextBoxRtfBehavior),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRtfTextChanged));

    public static string GetRtfText(DependencyObject obj)
    {
        return (string)obj.GetValue(RtfTextProperty);
    }

    public static void SetRtfText(DependencyObject obj, string value)
    {
        obj.SetValue(RtfTextProperty, value);
    }

    private static void OnRtfTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not WpfRichTextBox richTextBox)
        {
            return;
        }

        EnsureHooked(richTextBox);

        if ((bool)richTextBox.GetValue(IsUpdatingProperty))
        {
            return;
        }

        richTextBox.SetValue(IsUpdatingProperty, true);

        try
        {
            var text = e.NewValue as string ?? string.Empty;
            var range = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);

            if (string.IsNullOrWhiteSpace(text))
            {
                richTextBox.Document.Blocks.Clear();
                richTextBox.Document.Blocks.Add(new Paragraph());
            }
            else
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                range.Load(stream, WpfDataFormats.Rtf);
            }
        }
        catch
        {
            richTextBox.Document.Blocks.Clear();
            richTextBox.Document.Blocks.Add(new Paragraph(new Run(e.NewValue as string ?? string.Empty)));
        }
        finally
        {
            richTextBox.SetValue(IsUpdatingProperty, false);
        }
    }

    private static void EnsureHooked(WpfRichTextBox richTextBox)
    {
        if ((bool)richTextBox.GetValue(IsHookedProperty))
        {
            return;
        }

        richTextBox.TextChanged += OnRichTextBoxTextChanged;
        richTextBox.Unloaded += OnRichTextBoxUnloaded;
        richTextBox.SetValue(IsHookedProperty, true);
    }

    private static void OnRichTextBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRichTextBox richTextBox)
        {
            return;
        }

        richTextBox.TextChanged -= OnRichTextBoxTextChanged;
        richTextBox.Unloaded -= OnRichTextBoxUnloaded;
        richTextBox.SetValue(IsHookedProperty, false);
    }

    private static void OnRichTextBoxTextChanged(object sender, WpfTextChangedEventArgs e)
    {
        if (sender is not WpfRichTextBox richTextBox)
        {
            return;
        }

        if ((bool)richTextBox.GetValue(IsUpdatingProperty))
        {
            return;
        }

        richTextBox.SetValue(IsUpdatingProperty, true);

        try
        {
            var range = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, WpfDataFormats.Rtf);
            var rtf = Encoding.UTF8.GetString(stream.ToArray());
            richTextBox.SetCurrentValue(RtfTextProperty, rtf);
        }
        finally
        {
            richTextBox.SetValue(IsUpdatingProperty, false);
        }
    }
}