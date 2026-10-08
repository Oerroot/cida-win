using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

public sealed class ExperienceTests
{
    [Fact]
    public void MalformedActionEntriesDoNotCrashMigrationOrEnableAnEmptyPrompt()
    {
        var settings = CidaSettings.FromJsonText("{\"actions\":[null,{\"id\":\"custom1\",\"name\":\"Custom\",\"prompt\":null}]}");
        Assert.Contains(settings.EnabledActions, action => action.Id == ProcessingAction.TranslationId);
        Assert.False(settings.EffectiveActions.Single(action => action.Id == "custom1").Enabled);
        Assert.DoesNotContain(settings.EnabledActions, action => action.Id == "custom1");
    }
    [Fact]
    public void EditsAndActionChangesDoNotMutateTheRunningSnapshot()
    {
        var state = new PanelState { Source = "source", ActionId = "translate" };
        var request = state.Begin(state.Source, state.ActionId, "config");
        state.Source = "edited"; state.ActionId = "custom";
        Assert.True(state.IsStale);
        Assert.True(state.Append(request, "original result"));
        Assert.Equal("source", state.SubmittedSource); Assert.Equal("translate", state.SubmittedActionId);
        Assert.True(state.Finish(request, ResultPhase.Completed));
        Assert.Equal("original result", state.Result); Assert.True(state.IsStale);
        state.Source = "source"; state.ActionId = "translate"; Assert.False(state.IsStale);
    }
    [Fact]
    public void CancelAndNewRequestRejectLateNetworkPieces()
    {
        var state = new PanelState(); var first = state.Begin("one", "translate", "a");
        state.Append(first, "partial"); state.Stop();
        Assert.False(state.Append(first, "late")); Assert.Equal("partial", state.Result);
        var second = state.Begin("two", "improve", "a");
        Assert.False(state.Finish(first, ResultPhase.Failed, "late failure"));
        Assert.True(state.Append(second, "second")); Assert.Equal("second", state.Result);
    }
    [Fact]
    public void LegacySettingsMigrateCustomPromptsAndDefaultFourthShortcut()
    {
        var settings = CidaSettings.FromJsonText("""{"promptContractVersion":2,"translationPrompt":"old custom policy"}""");
        Assert.Equal("old custom policy", settings.EnabledActions[0].Prompt);
        Assert.Equal(GlobalShortcut.AltF, settings.ImproveShortcut);
        var restored = CidaSettings.FromJsonText(settings.ToJsonText());
        Assert.Equal(settings.EffectiveActions, restored.EffectiveActions);
        Assert.Equal(settings.ToJsonText(), restored.ToJsonText());
    }
    [Fact]
    public void ActionsKeepStableOrderIdsAndProtectTranslation()
    {
        var custom = new ProcessingAction { Id = "custom-1", Name = "摘要", Prompt = "Summarize" };
        var settings = new CidaSettings { ApiKey = "secret-never-in-json", Actions = [custom, new() { Id = "translate", Name = "翻译", Prompt = "Translate", Enabled = false }] };
        var restored = CidaSettings.FromJsonText(settings.ToJsonText());
        Assert.Equal(custom, restored.EnabledActions.First());
        Assert.Contains(restored.EnabledActions, a => a.Id == "translate");
        Assert.DoesNotContain("secret-never-in-json", settings.ToJsonText());
    }
    [Fact]
    public void CustomActionPolicyDoesNotInheritImprovementLanguageRule()
    {
        var request = new ProcessingRequest { Text = "source", Mode = ProcessingMode.Custom, ActionPrompt = "Summarize in French", MyLanguage = "Chinese", ForeignLanguage = "English" };
        var prompt = ModelPromptBuilder.Build(request, new CidaSettings());
        Assert.Equal(ModelLanguageBehavior.PromptDefined, prompt.Parameters.LanguageBehavior);
        Assert.StartsWith("Summarize in French", prompt.SystemMessage);
        Assert.Contains("\"operation\":\"custom\"", prompt.SystemMessage);
    }
    [Theory]
    [InlineData("[{\"id\":0,\"text\":\"a\"},{\"id\":0,\"text\":\"b\"}]", 2)]
    [InlineData("[{\"id\":1,\"text\":\"a\"}]", 2)]
    [InlineData("[{\"id\":2,\"text\":\"a\"}]", 1)]
    [InlineData("[{\"id\":0,\"text\":\" \"}]", 1)]
    public void InvalidBatchCannotBePlacedOnSourceParagraphs(string json, int expected) => Assert.Null(LayerTranslationRequest.ParseReply(json, expected));
    [Fact]
    public void FourthShortcutConflictsWithWholeWindowCombination()
    {
        Assert.False((new CidaSettings { ImproveShortcut = GlobalShortcut.AltD.AddingShift() }).HasValidShortcuts);
        Assert.False((new CidaSettings { ImproveShortcut = GlobalShortcut.AltA }).HasValidShortcuts);
    }
}
