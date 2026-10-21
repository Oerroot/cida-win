using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class ConfigurationPromptTests
{
    private static CidaSettings CompleteSettings() => new()
    {
        ModelService = new ModelConfiguration
        {
            Endpoint = "https://api.deepseek.com/chat/completions",
            Format = ModelRequestFormat.ChatCompletions,
            Model = "deepseek-chat",
        },
        ApiKey = "sk-super-secret-key",
    };

    private static ProcessingFailure Failure(ProcessingFailureCategory category, CidaSettings settings) =>
        new(category, settings.ModelService.Model, settings.ModelServiceFingerprint);

    [Fact]
    public void BuildCarriesTheWindowsCommandLineAndCurrentConfiguration()
    {
        var settings = CompleteSettings();
        var prompt = ConfigurationPromptBuilder.Build(settings);
        Assert.Contains("Windows", prompt);
        Assert.Contains("Cida.Cli.exe config show --json", prompt);
        Assert.Contains("Cida.Cli.exe config schema", prompt);
        Assert.Contains("Cida.Cli.exe check", prompt);
        Assert.Contains("Cida.Cli.exe check --verbose", prompt);
        Assert.Contains("Cida.Cli.exe config set 字段=值", prompt);
        Assert.Contains("Cida.Cli.exe config set api-key --stdin", prompt);
        Assert.Contains("type 密钥.txt", prompt);
        Assert.Contains("--env", prompt);
        Assert.Contains("--file", prompt);
        Assert.Contains(ConfigurationPromptBuilder.CurrentConfiguration(settings), prompt);
        Assert.Contains("deepseek-chat", prompt);
        Assert.DoesNotContain("pbpaste", prompt);
        Assert.DoesNotContain("macOS", prompt);
    }

    [Fact]
    public void TheConfigurationStateSelectsTheTask()
    {
        var unset = new CidaSettings();
        Assert.True(unset.ModelService.IsUnset);
        Assert.Contains("从头配置", ConfigurationPromptBuilder.Build(unset));
        Assert.Contains("还没配置", ConfigurationPromptBuilder.Build(unset));

        var incomplete = new CidaSettings
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.deepseek.com/chat/completions",
                Model = "",
            },
            ApiKey = "",
        };
        Assert.False(incomplete.ModelService.IsUnset);
        Assert.False(incomplete.IsModelServiceComplete);
        var prompt = ConfigurationPromptBuilder.Build(incomplete);
        Assert.Contains("继续配置", prompt);
        Assert.Contains("model", prompt);
        Assert.Contains("api-key", prompt);

        // A complete configuration without failures asks what to adjust.
        Assert.Contains("调整配置", ConfigurationPromptBuilder.Build(CompleteSettings()));
    }

    [Fact]
    public void BothFailureContextsAppearInTheDecisionOrder()
    {
        var settings = CompleteSettings();
        var prompt = ConfigurationPromptBuilder.Build(
            settings,
            requestFailure: Failure(ProcessingFailureCategory.Configuration, settings),
            previewFailure: Failure(ProcessingFailureCategory.Timeout, settings));
        Assert.Contains("继续检查", prompt);
        Assert.Contains("面板请求未完成，错误类别为 configuration", prompt);
        Assert.Contains("动作试运行未完成，错误类别为 timeout", prompt);
        Assert.Contains("这不是一次连接检查的结果", prompt);
        Assert.Contains("普通连接检查成功不能证明原请求已恢复", prompt);
        Assert.Contains("请求原文、输出和服务商原文未附带", prompt);
        Assert.Contains("Cida.Cli.exe config show --json", prompt);
        Assert.Contains("Cida.Cli.exe check --verbose", prompt);
    }

    [Fact]
    public void FailuresOfAnOlderConfigurationAreFiltered()
    {
        var settings = CompleteSettings();
        var requestFailure = Failure(ProcessingFailureCategory.Configuration, settings);
        var stale = requestFailure with { ConfigurationFingerprint = "an-old-fingerprint" };
        var prompt = ConfigurationPromptBuilder.Build(settings, requestFailure: stale);
        Assert.DoesNotContain("面板请求未完成", prompt);
        Assert.Contains("调整配置", prompt);

        // The same failure is filtered once the model changes underneath it.
        var changed = settings with { ModelService = settings.ModelService with { Model = "another-model" } };
        var movedOn = ConfigurationPromptBuilder.Build(changed, requestFailure: requestFailure);
        Assert.DoesNotContain("面板请求未完成", movedOn);
        Assert.Contains("调整配置", movedOn);
    }

    [Fact]
    public void ACurrentFailedCheckIsUsedAndAnOlderOneIsFiltered()
    {
        var settings = CompleteSettings();
        var failure = Failure(ProcessingFailureCategory.Configuration, settings);
        var record = new ModelServiceCheckRecord(
            false, 401, "服务商拒绝了 API Key", new DateTime(2026, 10, 8, 5, 51, 19, DateTimeKind.Utc),
            settings.ModelServiceFingerprint);
        var prompt = ConfigurationPromptBuilder.Build(settings, record, requestFailure: failure);
        // The panel failure is the current evidence; the failed check still describes the same
        // configuration, but the failure branch decides first.
        Assert.Contains("面板请求未完成", prompt);
        Assert.DoesNotContain("最近一次检查未通过", prompt);

        var checkOnly = ConfigurationPromptBuilder.Build(settings, record);
        Assert.Contains("最近一次检查未通过", checkOnly);
        Assert.Contains("2026-10-08T05:51:19Z", checkOnly);
        Assert.Contains("Cida.Cli.exe check --verbose", checkOnly);

        var oldRecord = record with { Fingerprint = "an-old-fingerprint" };
        Assert.DoesNotContain("最近一次检查未通过", ConfigurationPromptBuilder.Build(settings, oldRecord));
        var passed = record with { Passed = true };
        Assert.DoesNotContain("最近一次检查未通过", ConfigurationPromptBuilder.Build(settings, passed));
    }

    [Fact]
    public void PromptNeverLeaksTheKeySourceOrOutput()
    {
        var settings = CompleteSettings();
        const string source = "The user's private source text";
        const string output = "The model's private output";
        var prompt = ConfigurationPromptBuilder.Build(
            settings,
            requestFailure: Failure(ProcessingFailureCategory.Unknown, settings),
            previewFailure: Failure(ProcessingFailureCategory.SecureConnection, settings));
        Assert.DoesNotContain(settings.ApiKey, prompt);
        Assert.DoesNotContain("sk-super-secret-key", prompt);
        Assert.DoesNotContain(source, prompt);
        Assert.DoesNotContain(output, prompt);
        Assert.Contains("错误类别为 secureConnection", prompt);
        Assert.Contains("错误类别为 unknown", prompt);
    }

    [Fact]
    public void PartialConfigurationListsMissingFieldsWithoutLeakingTheKey()
    {
        var settings = new CidaSettings
        {
            ModelService = new ModelConfiguration
            {
                Endpoint = "https://api.example.com/v1/chat/completions",
                Model = "some-model",
            },
            ApiKey = "",
        };
        var description = ConfigurationPromptBuilder.CurrentConfiguration(settings);
        Assert.Contains("some-model", description);
        Assert.Contains("api-key", description);

        // Once the key is set the configuration is complete, and a summary still never echoes it.
        var withKey = settings with { ApiKey = "sk-not-yet-complete" };
        var summary = ConfigurationPromptBuilder.CurrentConfiguration(withKey);
        Assert.Contains("some-model", summary);
        Assert.DoesNotContain("sk-not-yet-complete", summary);
    }

    [Fact]
    public void ExecutablePathCanBeSetForAnInstalledLayout()
    {
        var previous = ConfigurationPromptBuilder.ExecutablePath;
        try
        {
            ConfigurationPromptBuilder.ExecutablePath = @"C:\Program Files\Cida\Cida.Cli.exe";
            var prompt = ConfigurationPromptBuilder.Build(CompleteSettings());
            Assert.Contains(@"C:\Program Files\Cida\Cida.Cli.exe", prompt);
        }
        finally
        {
            ConfigurationPromptBuilder.ExecutablePath = previous;
        }
    }
}
