using System.IO;
using System.Windows.Media.Imaging;
using TemplatePilot.Helpers;
using TemplatePilot.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfIDataObject = System.Windows.IDataObject;
using System.Text;
using System.Windows.Interop;
using TemplatePilot.Interop;
using WpfDataObject = System.Windows.DataObject;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace TemplatePilot.Services;

public sealed class ClipboardCaptureService
{
    private readonly ClipboardStaQueue _staQueue;
    private readonly ClipboardRetryService _retry;
    private readonly ClipboardFingerprintService _fingerprintService;
    private readonly SettingsService _settings;

    public ClipboardCaptureService(
        ClipboardStaQueue staQueue,
        ClipboardRetryService retry,
        ClipboardFingerprintService fingerprintService,
        SettingsService settings)
    {
        _staQueue = staQueue;
        _retry = retry;
        _fingerprintService = fingerprintService;
        _settings = settings;
    }

    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        return _retry.ExecuteAsync(
            token => _staQueue.InvokeAsync(CaptureCore, token),
            TimeSpan.FromMilliseconds(_settings.Current.ClipboardTimeoutMs),
            cancellationToken);
    }

    private ClipboardSnapshot CaptureCore()
    {
        var dataObject = WpfClipboard.GetDataObject();

        if (dataObject is null)
        {
            return ClipboardSnapshot.Empty;
        }

        var formats = new List<ClipboardFormatData>();

        foreach (var format in dataObject.GetFormats(autoConvert: false))
        {
            var captured = TryCaptureFormat(dataObject, format);
            if (captured is not null && captured.HasData)
            {
                formats.Add(captured);
            }
        }

        var fingerprint = _fingerprintService.Create(formats);
        return new ClipboardSnapshot(formats, fingerprint);
    }

    private static ClipboardFormatData? TryCaptureFormat(WpfIDataObject dataObject, string format)
    {
        try
        {
            var data = dataObject.GetData(format, autoConvert: false);

            if (data is null)
            {
                return null;
            }

            if (format == ClipboardFormatNames.UnicodeText || format == ClipboardFormatNames.Text)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Text, Text = Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty };
            }

            if (format == ClipboardFormatNames.Rtf)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Rtf, Text = Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty };
            }

            if (format == ClipboardFormatNames.Html)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Html, Text = Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty };
            }

            if (format == ClipboardFormatNames.Bitmap && data is BitmapSource bitmapSource)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Bitmap, Bytes = ImageHelper.EncodePng(bitmapSource) };
            }

            if (format == ClipboardFormatNames.Png)
            {
                var bytes = ReadBytes(data);
                return bytes is null ? null : new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Png, Bytes = bytes };
            }

            if (format == ClipboardFormatNames.DeviceIndependentBitmap)
            {
                var bytes = ReadBytes(data);
                return bytes is null ? null : new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Dib, Bytes = bytes };
            }

            if (!ClipboardFormatSafetyPolicy.IsAllowedForTemplateBinaryStorage(format))
            {
                return null;
            }

            var binary = ReadBytes(data);
            if (binary is not null)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Binary, Bytes = binary };
            }

            if (data is string text)
            {
                return new ClipboardFormatData { Format = format, Kind = ClipboardDataKind.Text, Text = text };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadBytes(object data)
    {
        switch (data)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case MemoryStream memoryStream:
                return memoryStream.ToArray();
            case Stream stream:
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            case BitmapSource bitmap:
                return ImageHelper.EncodePng(bitmap);
            default:
                return null;
        }
    }
}

public sealed class ClipboardFingerprintService
{
    public ClipboardFingerprint Create(IReadOnlyList<ClipboardFormatData> formats)
    {
        if (formats.Count == 0)
        {
            return ClipboardFingerprint.Empty;
        }

        var hashes = new List<ClipboardFormatHash>();
        var builder = new StringBuilder();

        foreach (var item in formats)
        {
            var bytes = item.Text is not null
                ? Encoding.UTF8.GetBytes(item.Text)
                : item.Bytes ?? Array.Empty<byte>();

            var hash = HashHelper.Sha256Hex(bytes);
            hashes.Add(new ClipboardFormatHash { Format = item.Format, Sha256 = hash });
            builder.Append(item.Format).Append('\0').Append(hash).Append('\0');
        }

        return new ClipboardFingerprint(HashHelper.Sha256Hex(builder.ToString()), hashes);
    }
}

public sealed class ClipboardPasteService
{
    private readonly ClipboardStaQueue _staQueue;
    private readonly ClipboardRetryService _retry;
    private readonly PlaceholderService _placeholders;
    private readonly SettingsService _settings;

    public ClipboardPasteService(
        ClipboardStaQueue staQueue,
        ClipboardRetryService retry,
        PlaceholderService placeholders,
        SettingsService settings)
    {
        _staQueue = staQueue;
        _retry = retry;
        _placeholders = placeholders;
        _settings = settings;
    }

    public Task PutTemplateOnClipboardAsync(TemplatePackage template, CancellationToken cancellationToken)
    {
        return _retry.ExecuteAsync(
            token => _staQueue.InvokeAsync(() =>
            {
                var dataObject = BuildDataObject(template);
                WpfClipboard.SetDataObject(dataObject, copy: true);
                return true;
            }, token),
            TimeSpan.FromMilliseconds(_settings.Current.ClipboardTimeoutMs),
            cancellationToken);
    }

    private WpfDataObject BuildDataObject(TemplatePackage template)
    {
        var content = template.Content;
        var dataObject = new WpfDataObject();

        var plain = _placeholders.ApplyToText(content.PlainText);
        var html = _placeholders.ApplyToHtml(content.Html);
        var rtf = _placeholders.ApplyToRtf(content.Rtf);

        if (!string.IsNullOrWhiteSpace(html))
        {
            dataObject.SetData(ClipboardFormatNames.Html, ClipboardHtmlBuilder.BuildClipboardHtml(html));
        }

        if (!string.IsNullOrWhiteSpace(rtf))
        {
            dataObject.SetData(ClipboardFormatNames.Rtf, RtfHelper.EnsureRtf(rtf, plain));
        }

        if (string.IsNullOrEmpty(plain))
        {
            plain = !string.IsNullOrWhiteSpace(html)
                ? HtmlTextExtractor.ToPlainText(html)
                : (!string.IsNullOrWhiteSpace(rtf) ? SafeRtfToPlainText(rtf) : string.Empty);
        }

        if (!string.IsNullOrEmpty(plain))
        {
            dataObject.SetData(ClipboardFormatNames.UnicodeText, plain);
            dataObject.SetData(ClipboardFormatNames.Text, plain);
        }

        foreach (var image in content.Images.Where(x => x.Bytes.Length > 0))
        {
            var bitmap = ImageHelper.DecodeBitmap(image.Bytes);
            dataObject.SetImage(bitmap);
            dataObject.SetData(ClipboardFormatNames.Png, new MemoryStream(image.Bytes));
            break;
        }

        foreach (var item in content.BinaryItems.Where(x => x.Bytes.Length > 0 && ClipboardFormatSafetyPolicy.IsAllowedForTemplateBinaryStorage(x.Format)))
        {
            dataObject.SetData(item.Format, new MemoryStream(item.Bytes));
        }

        return dataObject;
    }

    private static string SafeRtfToPlainText(string rtf)
    {
        try
        {
            return RtfHelper.ToPlainText(rtf);
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed class ClipboardRetryService
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;

    public ClipboardRetryService(SettingsService settings, LoggingService logging)
    {
        _settings = settings;
        _logging = logging;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        var delay = TimeSpan.FromMilliseconds(_settings.Current.ClipboardRetryDelayMs);
        Exception? lastException = null;

        while (DateTime.UtcNow - start <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsClipboardBusy(exception))
            {
                lastException = exception;
                var owner = User32ClipboardInterop.GetOpenClipboardWindow();

                if (owner != IntPtr.Zero)
                {
                    _logging.Warning($"Clipboard locked by window 0x{owner.ToInt64():X}.");
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Clipboard operation timed out.", lastException);
    }

    private static bool IsClipboardBusy(Exception exception)
    {
        if (exception is COMException comException)
        {
            var code = unchecked((uint)comException.HResult);
            return code is 0x800401D0 or 0x800401D1;
        }

        return exception is System.Runtime.InteropServices.ExternalException
            || exception.InnerException is not null && IsClipboardBusy(exception.InnerException);
    }
}

public sealed class ClipboardStaQueue : IDisposable
{
    private readonly BlockingCollection<IStaWorkItem> _queue = new();
    private readonly Thread _thread;

    public ClipboardStaQueue()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TemplatePilot Clipboard STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (_queue.IsAddingCompleted)
        {
            return Task.FromException<T>(new ObjectDisposedException(nameof(ClipboardStaQueue)));
        }

        var item = new StaWorkItem<T>(action, cancellationToken);
        _queue.Add(item, cancellationToken);
        return item.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }

    private interface IStaWorkItem
    {
        void Execute();
    }

    private sealed class StaWorkItem<T> : IStaWorkItem
    {
        private readonly Func<T> _action;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StaWorkItem(Func<T> action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(_action());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
