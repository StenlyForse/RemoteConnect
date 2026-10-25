using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteConnect.Core.Chat;
using Serilog;

namespace RemoteConnect.App.Views;

/// <summary>
/// Панель чата (spec-chat) — одна и та же в окне просмотра и под плашкой управляемой стороны.
/// Показывает историю <see cref="ChatSession"/> и отправляет сообщения; когда раскрывать панель, решает владелец.
/// </summary>
public partial class ChatPanel : UserControl
{
    /// <summary>Максимальная ширина текста в «пузыре» сообщения при ширине панели 320.</summary>
    private const double MaxTextWidth = 220;

    private static readonly Brush MineBrush = Frozen(Color.FromRgb(0xDB, 0xEA, 0xFE));
    private static readonly Brush TheirsBrush = Frozen(Color.FromRgb(0xF1, 0xF5, 0xF9));

    private ChatSession? _session;

    public ChatPanel()
    {
        InitializeComponent();
    }

    public void Attach(ChatSession session, string peerName)
    {
        _session = session;
        PeerNameRun.Text = peerName;
        session.MessageAdded += OnMessageAdded;
        session.OldestRemoved += _ =>
        {
            if (MessageList.Children.Count > 0)
            {
                MessageList.Children.RemoveAt(0);
            }
        };
    }

    public void FocusInput()
    {
        Input.Focus();
        Keyboard.Focus(Input);
    }

    /// <summary>Открывает ссылку в браузере своего ПК, только http/https (FR-CHT-14, FR-CHT-15).</summary>
    public static void OpenLink(Uri uri)
    {
        if (!ChatLinkParser.IsSafe(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Не удалось открыть ссылку из чата");
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            Send();
            e.Handled = true;
        }
    }

    private void Send()
    {
        if (_session?.TrySend(Input.Text) == true)
        {
            Log.Information("Чат: отправлено сообщение, {Length} символов", _session.Messages[^1].Text.Length);
            Input.Clear();
        }

        Input.Focus();
    }

    private void OnMessageAdded(ChatEntry entry)
    {
        var atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
        MessageList.Children.Add(CreateBubble(entry));
        EmptyHint.Visibility = Visibility.Collapsed;

        // Если пользователь прокрутил вверх, чтобы перечитать, — не сбиваем позицию (FR-CHT-7).
        if (atBottom || entry.Author == ChatAuthor.Me)
        {
            Scroller.ScrollToEnd();
        }
    }

    private FrameworkElement CreateBubble(ChatEntry entry)
    {
        var mine = entry.Author == ChatAuthor.Me;
        var align = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var header = new TextBlock
        {
            Text = $"{entry.Name} · {entry.Time:HH:mm}",
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = align,
            Margin = new Thickness(2, 0, 2, 2),
        };

        var body = CreateBody(entry);
        var bubble = new Border
        {
            Background = mine ? MineBrush : TheirsBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3, 5, 3, 5),
            HorizontalAlignment = align,
            Child = body,
        };

        var panel = new StackPanel
        {
            Margin = mine ? new Thickness(36, 0, 0, 10) : new Thickness(0, 0, 36, 10),
            HorizontalAlignment = align,
        };
        panel.Children.Add(header);
        panel.Children.Add(bubble);
        return panel;
    }

    /// <summary>
    /// Текст сообщения — <see cref="RichTextBox"/> только для чтения: выделение мышью и кликабельные ссылки (FR-CHT-12, 14).
    /// Ширина подбирается по тексту, иначе каждый «пузырь» растягивался бы на всю панель.
    /// </summary>
    private RichTextBox CreateBody(ChatEntry entry)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var fragment in entry.Fragments)
        {
            if (fragment.Link is { } uri)
            {
                paragraph.Inlines.Add(CreateLink(fragment.Text, uri));
            }
            else
            {
                AddText(paragraph.Inlines, fragment.Text);
            }
        }

        var width = MeasureTextWidth(entry.Text);
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = FontSize,
            TextAlignment = TextAlignment.Left,
            Foreground = (Brush)FindResource("TextBrush"),
        };

        var box = new RichTextBox
        {
            Document = document,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            // RichTextBox сам ставит документу отступ 5 px слева и справа (PagePadding 5,0) — ширина с его учётом и запасом.
            Width = width + 16,
        };

        void Copy() => CopyText(box.Selection.IsEmpty ? entry.Text : box.Selection.Text);
        box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            Copy();
            e.Handled = true;
        }));
        var copy = new MenuItem { Header = "Копировать", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => Copy();
        box.ContextMenu = new ContextMenu { Items = { copy } };
        return box;
    }

    private Hyperlink CreateLink(string text, Uri uri)
    {
        var link = new Hyperlink(new Run(text))
        {
            ToolTip = uri.AbsoluteUri,
            Foreground = (Brush)FindResource("AccentBrush"),
            Cursor = Cursors.Hand,
        };
        link.Click += (_, _) => OpenLink(uri);

        var open = new MenuItem { Header = "Открыть ссылку" };
        open.Click += (_, _) => OpenLink(uri);
        var copy = new MenuItem { Header = "Копировать ссылку" };
        copy.Click += (_, _) => CopyText(uri.AbsoluteUri);
        link.ContextMenu = new ContextMenu { Items = { open, copy } };
        return link;
    }

    private static void AddText(InlineCollection inlines, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                inlines.Add(new Run(lines[i]));
            }
        }
    }

    private double MeasureTextWidth(string text)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var widest = 0.0;
        foreach (var line in text.Split('\n'))
        {
            var formatted = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.Black, dpi);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
            if (widest >= MaxTextWidth)
            {
                return MaxTextWidth;
            }
        }

        return Math.Ceiling(widest) + 2;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        }
        catch (COMException ex)
        {
            Log.Debug(ex, "Буфер обмена занят — копирование из чата не выполнено");
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
