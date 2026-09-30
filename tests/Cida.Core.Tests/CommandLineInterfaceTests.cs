using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class CommandLineInterfaceTests
{
    [Fact]
    public async Task ConfigShowListsModelServiceFieldsAndDefaults()
    {
        var output = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(outputLines: output);
        var code = await cli.RunAsync(["config", "show"]);
        Assert.Equal(0, code);
        var shown = output.Single();
        Assert.Contains("endpoint", shown);
        Assert.Contains("未设置", shown);
    }

    [Fact]
    public async Task ConfigSetWritesValidFieldsAtomically()
    {
        var (cli, store) = InMemoryStore.MakeCli();
        var code = await cli.RunAsync(
        [
            "config", "set",
            "endpoint=https://api.deepseek.com/chat/completions",
            "model=deepseek-chat",
        ]);
        Assert.Equal(0, code);
        Assert.Equal("https://api.deepseek.com/chat/completions", store.Settings.ModelService.Endpoint);
        Assert.Equal("deepseek-chat", store.Settings.ModelService.Model);
        Assert.Equal(1, store.ChangeNotifications);
    }

    [Fact]
    public async Task ConfigSetRejectsInvalidValueAndKeepsOriginal()
    {
        var errors = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "endpoint=not-a-url", "model=x"]);
        Assert.Equal(64, code);
        Assert.Equal("", store.Settings.ModelService.Model);
        Assert.Single(errors);
        Assert.Contains("没有改动任何配置", errors[0]);
        Assert.Equal(0, store.ChangeNotifications);
    }

    [Fact]
    public async Task ApiKeyRefusesInlineValue()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "api-key=secret"]);
        Assert.Equal(64, code);
        Assert.Contains("不接受写在命令里的值", errors[0]);
    }

    [Fact]
    public async Task ApiKeyReadsFromStandardInput()
    {
        var output = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(
            stdin: "sk-stdin-9999"u8.ToArray(), outputLines: output);
        var code = await cli.RunAsync(["config", "set", "api-key", "--stdin"]);
        Assert.Equal(0, code);
        Assert.Equal("sk-stdin-9999", store.ApiKey);
        Assert.Contains("已把 API Key 存进系统密钥库", output[0]);
    }

    [Fact]
    public async Task ApiKeyReadsFromEnvironment()
    {
        var (cli, store) = InMemoryStore.MakeCli(
            environment: new Dictionary<string, string> { ["MY_KEY"] = "sk-env-42" });
        var code = await cli.RunAsync(["config", "set", "api-key", "--env", "MY_KEY"]);
        Assert.Equal(0, code);
        Assert.Equal("sk-env-42", store.ApiKey);
    }

    [Fact]
    public async Task MissingEnvironmentVariableIsAnError()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "api-key", "--env", "NOPE"]);
        Assert.Equal(64, code);
        Assert.Contains("环境变量 NOPE 不存在或为空", errors[0]);
    }

    [Fact]
    public async Task ShowJsonHidesKeyAndReportsCompleteness()
    {
        var output = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(outputLines: output);
        store.ApiKey = "sk-real-key";
        await cli.RunAsync(["config", "show", "--json"]);
        var text = output.Single();
        Assert.Contains("\"api-key\": \"stored\"", text);
        Assert.DoesNotContain("sk-real-key", text);
        Assert.Contains("\"complete\": false", text);
        Assert.Contains("\"missing\": [\"endpoint\", \"model\"]", text);
    }

    [Fact]
    public async Task SchemaListsAllFieldsWithSources()
    {
        var output = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(outputLines: output);
        await cli.RunAsync(["config", "schema", "--json"]);
        var text = output.Single();
        foreach (var field in System.Linq.Enumerable.Select(
                     Enum.GetValues<ConfigurationField>(), f => f.RawValue()))
        {
            Assert.Contains($"\"name\": \"{field}\"", text);
        }
        Assert.Contains("\"secret\": true", text);
    }

    [Fact]
    public async Task UnsetRestoresDefault()
    {
        var (cli, store) = InMemoryStore.MakeCli();
        store.Settings = store.Settings with
        {
            ModelService = store.Settings.ModelService with { Model = "custom" },
        };
        var code = await cli.RunAsync(["config", "unset", "model"]);
        Assert.Equal(0, code);
        Assert.Equal("", store.Settings.ModelService.Model);
    }

    [Fact]
    public async Task DuplicateShortcutsAreRejected()
    {
        var errors = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "capture-shortcut=option+a"]);
        Assert.Equal(64, code);
        Assert.Contains("不能与 shortcut 相同", errors[0]);
        Assert.Equal(0, store.ChangeNotifications);
    }

    [Fact]
    public async Task LayerShortcutWithShiftIsRejected()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "layer-shortcut=option+shift+d"]);
        Assert.Equal(64, code);
        Assert.Contains("不能带 shift", errors[0]);
    }

    [Fact]
    public async Task NoneShortcutClearsIt()
    {
        var (cli, store) = InMemoryStore.MakeCli();
        var code = await cli.RunAsync(["config", "set", "shortcut=none"]);
        Assert.Equal(0, code);
        Assert.Null(store.Settings.Shortcut);
    }

    [Fact]
    public async Task ResetClearsEverythingIncludingKey()
    {
        var output = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(outputLines: output);
        store.ApiKey = "sk-will-die";
        store.Settings = store.Settings with
        {
            ModelService = store.Settings.ModelService with { Model = "gone" },
        };
        var code = await cli.RunAsync(["config", "reset"]);
        Assert.Equal(0, code);
        Assert.Null(store.ApiKey);
        Assert.Equal("", store.Settings.ModelService.Model);
        Assert.Null(store.LastCheck);
    }

    [Fact]
    public async Task CheckSucceedsAndRecordsOutcome()
    {
        var output = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(outputLines: output);
        store.Settings = store.Settings with
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.example.com/chat/completions",
                Model = "test-model",
            },
            ApiKey = "sk-check-1",
        };
        store.ApiKey = "sk-check-1";
        var code = await cli.RunAsync(["check"]);
        Assert.Equal(0, code);
        Assert.Contains("✓ 可用 · test-model", output[0]);
        Assert.NotNull(store.LastCheck);
        Assert.True(store.LastCheck!.Passed);
    }

    [Fact]
    public async Task CheckWithIncompleteConfigFailsWithMissingFields()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["check"]);
        Assert.Equal(64, code);
        Assert.Contains("配置还不完整，缺少", errors[0]);
    }

    [Fact]
    public async Task CheckFailureExitCodeIs69AndRecordsReason()
    {
        var output = new List<string>();
        var (cli, store) = InMemoryStore.MakeCli(outputLines: output);
        store.Settings = store.Settings with
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.example.com/chat/completions",
                Model = "test-model",
            },
            ApiKey = "sk-fail",
        };
        store.ApiKey = "sk-fail";
        var cli2 = new CommandLineInterface(
            store.ToStore(),
            new Dictionary<string, string>(),
            () => Array.Empty<byte>(),
            path => throw new FileNotFoundException(path),
            text => output.Add(text),
            text => { },
            _ => Task.FromResult(new ModelServiceCheckResult(
                "test-model", TimeSpan.FromSeconds(1), "",
                ModelServiceErrorInfo.Http(401, "Incorrect API key", "{\"error\":{}}"),
                null, 401, "{\"error\":{}}",
                new ModelServiceCheckRecord(false, 401, "服务商拒绝了 API Key", DateTime.Now, "f"))));
        var code = await cli2.RunAsync(["check"]);
        Assert.Equal(69, code);
        Assert.Contains("✗ 检查失败 · HTTP 401 · 服务商拒绝了 API Key", output[0]);
        Assert.NotNull(store.LastCheck);
        Assert.False(store.LastCheck!.Passed);
    }

    [Fact]
    public async Task UnknownCommandIsUsageError()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["frobnicate"]);
        Assert.Equal(64, code);
        Assert.Contains("不认识的命令", errors[0]);
    }

    [Fact]
    public async Task HelpListsCommands()
    {
        var output = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(outputLines: output);
        var code = await cli.RunAsync(["help"]);
        Assert.Equal(0, code);
        Assert.Contains("辞达的命令行", output[0]);
        Assert.Contains("config schema", output[0]);
    }

    [Fact]
    public async Task HeadersMustBeJSONObject()
    {
        var errors = new List<string>();
        var (cli, _) = InMemoryStore.MakeCli(errorLines: errors);
        var code = await cli.RunAsync(["config", "set", "headers=[1,2]"]);
        Assert.Equal(64, code);
        Assert.Contains("要是一个 JSON 对象", errors[0]);
    }

    [Fact]
    public async Task LaunchAtLoginUpdatesSeparateFlag()
    {
        var (cli, store) = InMemoryStore.MakeCli();
        var code = await cli.RunAsync(["config", "set", "launch-at-login=true"]);
        Assert.Equal(0, code);
        Assert.True(store.LaunchAtLogin);
        Assert.True(store.Settings.LaunchAtLogin);
    }
}
