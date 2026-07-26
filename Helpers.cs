using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Net;
using System.Drawing;
using WpfApplication = System.Windows.Application;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using WpfDataFormats = System.Windows.DataFormats;

namespace TemplatePilot.Helpers;

public static class ClipboardFormatSafetyPolicy
{
    private static readonly string[] BlockedFormatParts =
    {
        "FileDrop",
        "Shell IDList",
        "Object Descriptor",
        "Link Source",
        "Embed Source",
        "Ole Private Data",
        "Native",
        "OwnerLink",
        "FileName",
        "Preferred DropEffect"
    };

    public static bool IsAllowedForTemplateBinaryStorage(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        return !BlockedFormatParts.Any(part => format.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
}

public static partial class ClipboardHtmlBuilder
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    public static string BuildClipboardHtml(string html)
    {
        var document = NormalizeToDocument(html ?? string.Empty);

        var startFragmentChar = document.IndexOf(StartMarker, StringComparison.Ordinal);
        var endFragmentChar = document.IndexOf(EndMarker, StringComparison.Ordinal);

        if (startFragmentChar < 0 || endFragmentChar < 0 || endFragmentChar < startFragmentChar)
        {
            document = $"<html><body>{StartMarker}{document}{EndMarker}</body></html>";
            startFragmentChar = document.IndexOf(StartMarker, StringComparison.Ordinal);
            endFragmentChar = document.IndexOf(EndMarker, StringComparison.Ordinal);
        }

        var fragmentStartContentChar = startFragmentChar + StartMarker.Length;
        var fragmentEndContentChar = endFragmentChar;

        var headerTemplate =
            "Version:1.0\r\n" +
            "StartHTML:{0:0000000000}\r\n" +
            "EndHTML:{1:0000000000}\r\n" +
            "StartFragment:{2:0000000000}\r\n" +
            "EndFragment:{3:0000000000}\r\n";

        var dummyHeader = string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(dummyHeader);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(document[..fragmentStartContentChar]);
        var endFragment = startHtml + Encoding.UTF8.GetByteCount(document[..fragmentEndContentChar]);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(document);

        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return header + document;
    }

    public static string ExtractFragment(string htmlClipboardOrDocument)
    {
        if (string.IsNullOrWhiteSpace(htmlClipboardOrDocument))
        {
            return string.Empty;
        }

        var text = htmlClipboardOrDocument;
        var startOffset = TryReadHeaderOffset(text, "StartFragment");
        var endOffset = TryReadHeaderOffset(text, "EndFragment");

        if (startOffset.HasValue && endOffset.HasValue && endOffset.Value > startOffset.Value)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (startOffset.Value >= 0 && endOffset.Value <= bytes.Length)
            {
                return Encoding.UTF8.GetString(bytes, startOffset.Value, endOffset.Value - startOffset.Value);
            }
        }

        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);

        if (start >= 0 && end > start)
        {
            return text[(start + StartMarker.Length)..end];
        }

        var bodyMatch = BodyRegex().Match(text);
        return bodyMatch.Success ? bodyMatch.Groups[1].Value : text;
    }

    private static string NormalizeToDocument(string html)
    {
        if (html.Contains(StartMarker, StringComparison.Ordinal) && html.Contains(EndMarker, StringComparison.Ordinal))
        {
            return html;
        }

        if (HtmlRegex().IsMatch(html))
        {
            var bodyOpen = BodyOpenRegex().Match(html);
            var bodyClose = BodyCloseRegex().Match(html);

            if (bodyOpen.Success && bodyClose.Success && bodyClose.Index > bodyOpen.Index)
            {
                var insertStart = bodyOpen.Index + bodyOpen.Length;
                return html.Insert(bodyClose.Index, EndMarker).Insert(insertStart, StartMarker);
            }

            return html + StartMarker + EndMarker;
        }

        return $"<html><body>{StartMarker}{html}{EndMarker}</body></html>";
    }

    private static int? TryReadHeaderOffset(string text, string name)
    {
        var match = Regex.Match(text, $"^{name}:(\\d+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    [GeneratedRegex("<html[\\s\\S]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlRegex();

    [GeneratedRegex("<body[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyOpenRegex();

    [GeneratedRegex("</body>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyCloseRegex();

    [GeneratedRegex("<body[^>]*>([\\s\\S]*)</body>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyRegex();
}

public static class DpiHelper
{
    public static WpfPoint PhysicalToDeviceIndependent(Window window, double x, double y)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            return new WpfPoint(x, y);
        }

        return source.CompositionTarget.TransformFromDevice.Transform(new WpfPoint(x, y));
    }
}

public static class FileNameSanitizer
{
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "template";
        }

        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}

public static class HashHelper
{
    public static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string text)
    {
        return Sha256Hex(Encoding.UTF8.GetBytes(text));
    }
}

public static class HtmlPreviewHelper
{
    public static async Task InitializeAsync(WebView2 webView)
    {
        await webView.EnsureCoreWebView2Async().ConfigureAwait(true);

        if (webView.CoreWebView2 is null)
        {
            return;
        }

        webView.CoreWebView2.Settings.IsScriptEnabled = false;
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
    }

    public static void NavigateSafeHtml(WebView2 webView, string html)
    {
        if (webView.CoreWebView2 is null)
        {
            return;
        }

        var safeHtml =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data: blob:; style-src 'unsafe-inline';\">" +
            "<style>body{font-family:Segoe UI,Arial,sans-serif;font-size:14px;margin:12px;color:#1f2937;}</style>" +
            "</head><body>" + ClipboardHtmlBuilder.ExtractFragment(html) + "</body></html>";

        webView.NavigateToString(safeHtml);
    }
}

public static partial class HtmlTextExtractor
{
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var fragment = ClipboardHtmlBuilder.ExtractFragment(html);
        fragment = BreakRegex().Replace(fragment, "\n");
        fragment = TagRegex().Replace(fragment, string.Empty);
        return WebUtility.HtmlDecode(fragment).Trim();
    }

    [GeneratedRegex("<(br|/p|/div|/li)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}

public static class IconResourceHelper
{
    public static Icon LoadApplicationIcon()
    {
        var info = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Ressources/templatepilot.ico", UriKind.Absolute));
        if (info?.Stream is null)
        {
            return SystemIcons.Application;
        }

        using (info.Stream)
        using (var icon = new Icon(info.Stream))
        {
            return (Icon)icon.Clone();
        }
    }
}

public sealed class ImageBytesToBitmapSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is byte[] bytes && bytes.Length > 0)
        {
            try
            {
                return ImageHelper.DecodeBitmap(bytes);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public static class ImageHelper
{
    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource DecodeBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static TemplatePilot.Models.TemplateImageItem LoadImageFile(string path, int maxBytes)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length > maxBytes)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Image exceeds configured limit: {0}", path));
        }

        var bitmap = DecodeBitmap(bytes);
        var png = EncodePng(bitmap);

        return new TemplatePilot.Models.TemplateImageItem
        {
            FileName = Path.GetFileNameWithoutExtension(path) + ".png",
            MediaType = "image/png",
            Bytes = png
        };
    }
}

public static class RtfHelper
{
    public static string EnsureRtf(string rtf, string fallbackPlainText)
    {
        if (!string.IsNullOrWhiteSpace(rtf) && rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return rtf;
        }

        return CreateFromPlainText(fallbackPlainText);
    }

    public static string CreateFromPlainText(string text)
    {
        return @"{\rtf1\ansi\deff0 " + EscapeText(text) + "}";
    }

    public static string EscapeText(string text)
    {
        var builder = new StringBuilder();

        foreach (var ch in text ?? string.Empty)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '{':
                    builder.Append(@"\{");
                    break;
                case '}':
                    builder.Append(@"\}");
                    break;
                case '\r':
                    break;
                case '\n':
                    builder.Append(@"\par ");
                    break;
                default:
                    if (ch <= 0x7f)
                    {
                        builder.Append(ch);
                    }
                    else
                    {
                        builder.Append(CultureInvariantUnicode(ch));
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    public static string ToPlainText(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf))
        {
            return string.Empty;
        }

        var document = new FlowDocument();
        var range = new TextRange(document.ContentStart, document.ContentEnd);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
        range.Load(stream, WpfDataFormats.Rtf);
        return range.Text.TrimEnd('\r', '\n');
    }

    private static string CultureInvariantUnicode(char ch)
    {
        return @"\u" + Convert.ToInt32(ch).ToString(System.Globalization.CultureInfo.InvariantCulture) + "?";
    }
}

public static class UnicodeTextNormalizer
{
    public static string NormalizeKey(string value)
    {
        return (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
    }

    public static bool EqualsKey(string left, string right)
    {
        return string.Equals(NormalizeKey(left), NormalizeKey(right), StringComparison.InvariantCultureIgnoreCase);
    }

    public static bool ContainsKey(string source, string query)
    {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            NormalizeKey(source),
            NormalizeKey(query),
            CompareOptions.IgnoreCase) >= 0;
    }
}
