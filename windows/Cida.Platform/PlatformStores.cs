using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Cida.Core;

namespace Cida.Platform;

/// <summary>
/// The API key store: DPAPI-encrypted bytes under the user profile, current-user scope (the
/// Windows counterpart of the upstream Keychain store). Ported from SettingsStore.swift.
/// </summary>
public sealed class SecretStore(string? directory = null)
{
    private readonly string _directory = directory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cida");

    private string KeyPath => Path.Combine(_directory, "api-key.bin");

    public string? Read()
    {
        try
        {
            if (!File.Exists(KeyPath)) return null;
            var encrypted = File.ReadAllBytes(KeyPath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(string apiKey)
    {
        Directory.CreateDirectory(_directory);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, encrypted);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
        }
        catch (IOException)
        {
            // A locked file clears on the next write; nothing to surface to the user.
        }
    }

    public bool Exists() => File.Exists(KeyPath);
}

/// <summary>
/// The settings JSON and the last check record, stored under %AppData%\Cida. GUI and CLI
/// share the same files.
/// </summary>
public sealed class SettingsFileStore(string? directory = null)
{
    private readonly string _directory = directory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cida");

    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string LastCheckPath => Path.Combine(_directory, "last-check.json");

    public CidaSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new CidaSettings();
            return CidaSettings.FromJsonText(File.ReadAllText(SettingsPath));
        }
        catch (IOException)
        {
            return new CidaSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new CidaSettings();
        }
    }

    public void SaveSettings(CidaSettings settings)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, settings.ToJsonText());
    }

    public ModelServiceCheckRecord? LoadLastCheck()
    {
        try
        {
            if (!File.Exists(LastCheckPath)) return null;
            return ModelServiceCheckRecordExtensions.FromJsonText(File.ReadAllText(LastCheckPath));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SaveLastCheck(ModelServiceCheckRecord? record)
    {
        Directory.CreateDirectory(_directory);
        if (record == null)
        {
            if (File.Exists(LastCheckPath)) File.Delete(LastCheckPath);
            return;
        }
        File.WriteAllText(LastCheckPath, record.ToJsonText());
    }
}

/// <summary>
/// Login item through the HKCU Run key (the counterpart of SMAppService.mainApp).
/// </summary>
public sealed class LoginItem(string? executablePath = null)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cida";

    private string ExecutablePath => executablePath ?? Environment.ProcessPath ?? "";

    public bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(ExecutablePath))
            throw new FileNotFoundException("找不到辞达 GUI 程序，无法设置开机启动。", ExecutablePath);
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, "\"" + ExecutablePath + "\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>
/// Tells a running Cida that the configuration changed: a named pipe the GUI owns, the
/// counterpart of the upstream DistributedNotificationCenter ping.
/// </summary>
public static class ConfigurationChangeNotifier
{
    public const string PipeName = "cida-configuration-changed";

    /// <summary>What the GUI calls when the pipe receives a ping.</summary>
    public static void Listen(Action onChanged, CancellationToken cancellationToken)
    {
        var thread = new Thread(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        System.IO.Pipes.PipeDirection.In,
                        1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    reader.ReadLine();
                    server.Dispose();
                    server = null;
                    onChanged();
                }
                catch (Exception)
                {
                    server?.Dispose();
                    if (cancellationToken.IsCancellationRequested) return;
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
    }

    /// <summary>What the CLI calls after writing the configuration.</summary>
    public static void Ping()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("changed");
        }
        catch (Exception)
        {
            // No GUI running; the files are already written.
        }
    }
}

/// <summary>The production ConfigurationStore over the platform stores.</summary>
public static class PlatformConfiguration
{
    public static ConfigurationStore Production(
        string? directory = null, string? executablePath = null)
    {
        var settings = new SettingsFileStore(directory);
        var secrets = new SecretStore(directory);
        var loginItem = new LoginItem(executablePath);
        return new ConfigurationStore(
            LoadSettings: settings.LoadSettings,
            SaveSettings: settings.SaveSettings,
            ReadApiKey: secrets.Read,
            SaveApiKey: secrets.Save,
            ClearApiKey: secrets.Clear,
            HasApiKey: secrets.Exists,
            LoadLastCheck: settings.LoadLastCheck,
            SaveLastCheck: settings.SaveLastCheck,
            LaunchAtLogin: loginItem.IsEnabled,
            SetLaunchAtLogin: loginItem.SetEnabled,
            NotifyChange: ConfigurationChangeNotifier.Ping);
    }
}
