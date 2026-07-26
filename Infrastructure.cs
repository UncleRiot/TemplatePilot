using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TemplatePilot.Infrastructure;

public static class AppConstants
{
    public const string AppName = "TemplatePilot";

    public static string AppVersion
    {
        get
        {
            System.Reflection.Assembly assembly =
                typeof(AppConstants).Assembly;

            System.Reflection.AssemblyInformationalVersionAttribute? attribute =
                assembly
                    .GetCustomAttributes(
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                        false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(attribute?.InformationalVersion))
            {
                return attribute.InformationalVersion.Split('+')[0];
            }

            System.Version? version = assembly.GetName().Version;

            return version == null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public const string TemplateExtension = ".tplpack";
    public const int CurrentSettingsVersion = 1;
    public const int CurrentTemplateFormatVersion = 1;
}

public sealed class AppPaths
{
    public AppPaths()
    {
        BasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConstants.AppName);
        DefaultTemplatesPath = Path.Combine(BasePath, "Templates");
        TemplatesPath = DefaultTemplatesPath;
        SettingsPath = Path.Combine(BasePath, "Settings");
        LogsPath = Path.Combine(BasePath, "Logs");
        TempPath = Path.Combine(BasePath, "Temp");
        CrashRecoveryPath = Path.Combine(SettingsPath, "CrashRecovery");

        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(TemplatesPath);
        Directory.CreateDirectory(SettingsPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(CrashRecoveryPath);
    }

    public string BasePath { get; }
    public string DefaultTemplatesPath { get; }
    public string TemplatesPath { get; private set; }
    public string SettingsPath { get; }
    public string LogsPath { get; }
    public string TempPath { get; }
    public string CrashRecoveryPath { get; }

    public string SettingsFile => Path.Combine(SettingsPath, "settings.json");

    public void SetTemplatesPath(string? templatesPath)
    {
        TemplatesPath = string.IsNullOrWhiteSpace(templatesPath)
            ? DefaultTemplatesPath
            : Path.GetFullPath(templatesPath.Trim());

        Directory.CreateDirectory(TemplatesPath);
    }
}

public static class AtomicFileWriter
{
    public static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        ReplaceFile(tempPath, path);
    }

    public static async Task WriteBytesAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        ReplaceFile(tempPath, path);
    }

    public static void ReplaceFile(string tempPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (!File.Exists(targetPath))
        {
            File.Move(tempPath, targetPath);
            return;
        }

        var backupPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.bak");

        try
        {
            File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }
        finally
        {
            TryDelete(backupPath);
            TryDelete(tempPath);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private Mutex? _mutex;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{AppConstants.AppName}.SingleInstance", out var createdNew);
        return createdNew;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _mutex = null;
    }
}

public sealed class UiDispatcher
{
    public Dispatcher Dispatcher => Application.Current.Dispatcher;

    public Task InvokeAsync(Action action)
    {
        return Dispatcher.InvokeAsync(action).Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        return Dispatcher.InvokeAsync(action).Task;
    }
}
