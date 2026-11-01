using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TemplatePilot.Infrastructure;

namespace TemplatePilot.Services;

/// <summary>
/// Semantic Anchor: ApplicationDialogVisualSSOT
/// SSOT: All app-owned message, confirmation and category-input dialogs share this
/// window chrome and the existing AppThemeService resource palette.
/// Invariants: palette brushes are cloned per window (no shared Freezable context),
/// Glass dialog surfaces remain readable, modal ownership and keyboard dismissal
/// are preserved, and only an explicit affirmative action returns true.
/// Native Windows file/folder pickers are intentionally not replaced.
/// </summary>
internal sealed class ThemedDialogWindow : Window
{
    public Grid Body { get; }
    public Brush DialogTextBrush { get; }

    public ThemedDialogWindow(string title, Window? owner, double width = 460, double height = 210)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = width;
        MinHeight = height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontSize = owner?.FontSize ?? 14;
        if (owner is not null)
        {
            Owner = owner;
        }

        // Never share a Freezable brush between the main FluentWindow and a modal HWND.
        Brush surface = CloneThemeBrush("TemplatePilotFloatingMenuSurfaceBrush", Color.FromRgb(36, 36, 38));
        if (surface is SolidColorBrush tinted && tinted.Color.A < 248)
        {
            Color c = tinted.Color;
            tinted.Color = Color.FromArgb(248, c.R, c.G, c.B);
        }

        DialogTextBrush = CloneThemeBrush("TextFillColorPrimaryBrush", Colors.White);
        Brush outline = CloneThemeBrush("ControlStrokeColorDefaultBrush", Color.FromRgb(86, 86, 90));

        var frame = new Border
        {
            Background = surface,
            BorderBrush = outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Height = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(18, 0, 8, 0),
            Child = new TextBlock
            {
                Text = title,
                Foreground = DialogTextBrush,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        caption.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        };
        header.Children.Add(caption);

        var close = new Wpf.Ui.Controls.Button
        {
            Content = "×",
            Width = 32,
            Height = 30,
            Margin = new Thickness(0, 7, 8, 7),
            ToolTip = title
        };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);

        Body = new Grid { Margin = new Thickness(20, 8, 20, 18) };
        Grid.SetRow(Body, 1);
        layout.Children.Add(Body);
        frame.Child = layout;
        Content = frame;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    // Semantic Anchor: DialogOwnerResolution
    // Invariant: a modal dialog owns the active visible app window, including the editor;
    // if no suitable owner exists (e.g. early startup), it opens centered on screen.
    public static Window? FindOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => FindOwner());
        }

        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.IsActive)
            ?? (app.MainWindow is { IsVisible: true } main ? main : null);
    }

    // Semantic Anchor: UnifiedMessageDialogSemantics
    // Invariants: informational/error dialogs require only a dismissal;
    // confirmations return true only for the affirmative button; the UI thread owns
    // modal window creation, and text stays legible for long errors via scrolling.
    public static bool ShowMessage(Window? owner, string title, string message,
        string? confirmText, string closeText, bool isDestructive = false, string? symbol = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => ShowMessage(owner, title, message,
                confirmText, closeText, isDestructive, symbol));
        }

        var dialog = new ThemedDialogWindow(title, owner);
        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var messageGrid = new Grid();
        if (!string.IsNullOrEmpty(symbol))
        {
            messageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            messageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = CloneThemeBrush("SystemAccentColorPrimaryBrush", Color.FromRgb(0, 103, 192)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = symbol,
                    Foreground = CloneThemeBrush("TemplatePilotPopupSelectionTextBrush", Colors.White),
                    FontSize = 19,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            messageGrid.Children.Add(icon);
        }
        else
        {
            messageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = message,
                Foreground = dialog.DialogTextBrush,
                TextWrapping = TextWrapping.Wrap
            }
        };
        Grid.SetColumn(scroll, messageGrid.ColumnDefinitions.Count - 1);
        messageGrid.Children.Add(scroll);
        dialog.Body.Children.Add(messageGrid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (!string.IsNullOrWhiteSpace(confirmText))
        {
            var confirm = new Wpf.Ui.Controls.Button
            {
                Content = confirmText,
                MinWidth = 84,
                IsDefault = true,
                Margin = new Thickness(0, 0, 12, 0)
            };
            if (isDestructive)
            {
                confirm.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
            }
            confirm.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(confirm);
        }

        var dismiss = new Wpf.Ui.Controls.Button
        {
            Content = closeText,
            MinWidth = 84,
            IsCancel = true,
            IsDefault = string.IsNullOrWhiteSpace(confirmText)
        };
        dismiss.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(dismiss);
        Grid.SetRow(buttons, 1);
        dialog.Body.Children.Add(buttons);
        return dialog.ShowDialog() == true;
    }

    private static Brush CloneThemeBrush(string key, Color fallback)
    {
        return Application.Current?.TryFindResource(key) is Brush shared
            ? (Brush)shared.CloneCurrentValue()
            : new SolidColorBrush(fallback);
    }
}
