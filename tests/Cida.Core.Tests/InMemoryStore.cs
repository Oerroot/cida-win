using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class InMemoryStore
{
    public CidaSettings Settings { get; set; } = new();
    public string? ApiKey { get; set; }
    public ModelServiceCheckRecord? LastCheck { get; set; }
    public bool LaunchAtLogin { get; set; }
    public int ChangeNotifications { get; set; }

    public ConfigurationStore ToStore() => new(
        LoadSettings: () => Settings,
        SaveSettings: value => Settings = value,
        ReadApiKey: () => ApiKey,
        SaveApiKey: value => ApiKey = value,
        ClearApiKey: () => ApiKey = null,
        HasApiKey: () => ApiKey != null,
        LoadLastCheck: () => LastCheck,
        SaveLastCheck: record => LastCheck = record,
        LaunchAtLogin: () => LaunchAtLogin,
        SetLaunchAtLogin: value => LaunchAtLogin = value,
        NotifyChange: () => ChangeNotifications++);

    public static (CommandLineInterface Cli, InMemoryStore Store) MakeCli(
        IReadOnlyDictionary<string, string>? environment = null,
        byte[]? stdin = null,
        CheckRunner? check = null,
        List<string>? outputLines = null,
        List<string>? errorLines = null)
    {
        var store = new InMemoryStore();
        outputLines ??= [];
        errorLines ??= [];
        var cli = new CommandLineInterface(
            store.ToStore(),
            environment ?? new Dictionary<string, string>(),
            () => stdin ?? Array.Empty<byte>(),
            path => throw new FileNotFoundException(path),
            text => outputLines.Add(text),
            text => errorLines.Add(text),
            check ?? (_ => Task.FromResult(new ModelServiceCheckResult(
                "test-model", TimeSpan.FromSeconds(1), "你好", null, null, 200, "",
                new ModelServiceCheckRecord(true, 200, null, DateTime.Now, "f")))));
        return (cli, store);
    }
}
