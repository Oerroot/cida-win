using System.Reflection;
using Cida.Core;
using Cida.Desktop;
using Xunit;

namespace Cida.Windows.Tests;

/// <summary>
/// The #73 recovery presentation of the panel. These tests exercise the pure decision helper and
/// the incomplete-image contract; they never construct a window, so the headless reflection
/// runner executes them without an interactive desktop.
/// </summary>
public sealed class FailureRecoveryPanelTests
{
    private static ProcessingFailure Failure(
        ProcessingFailureCategory category,
        string model = "deepseek-chat",
        string fingerprint = "fingerprint-a") => new(category, model, fingerprint);

    private static PanelPresentationInput Input(
        ProcessingFailure? failure = null,
        string currentFingerprint = "fingerprint-a",
        bool isProcessing = false,
        bool isRecognizing = false,
        bool isStale = false,
        bool hasOutput = false,
        bool hasSubmittableInput = true,
        bool repeatsSubmittedAction = true,
        ResultPhase phase = ResultPhase.Completed) => new()
    {
        Failure = failure,
        CurrentConfigurationFingerprint = currentFingerprint,
        IsProcessing = isProcessing,
        IsRecognizing = isRecognizing,
        IsStale = isStale,
        HasOutput = hasOutput,
        HasSubmittableInput = hasSubmittableInput,
        RepeatsSubmittedAction = repeatsSubmittedAction,
        Phase = phase,
    };

    [Fact]
    public void UnchangedConfigurationFailureOffersModelSettingsAndGatesEnter()
    {
        var failure = Failure(ProcessingFailureCategory.Configuration);
        var input = Input(failure, hasOutput: true);
        Assert.Equal(PanelBarAction.ModelSettings, PanelPresentation.ResolveBarAction(input));
        Assert.Equal("模型设置", PanelPresentation.ActionLabel(PanelBarAction.ModelSettings));
        Assert.False(PanelPresentation.ConfigurationChanged(input));
        Assert.False(PanelPresentation.CanSubmit(input));

        // Updating the configuration turns the same failure into an explicit retry.
        var changed = input with { CurrentConfigurationFingerprint = "fingerprint-b" };
        Assert.True(PanelPresentation.ConfigurationChanged(changed));
        Assert.Equal(PanelBarAction.Retry, PanelPresentation.ResolveBarAction(changed));
        Assert.Equal("重试", PanelPresentation.ActionLabel(PanelBarAction.Retry));
        Assert.True(PanelPresentation.CanSubmit(changed));

        // Restoring the failed configuration restores the model-settings gate.
        Assert.Equal(PanelBarAction.ModelSettings, PanelPresentation.ResolveBarAction(changed with
        {
            CurrentConfigurationFingerprint = "fingerprint-a",
        }));
        Assert.False(PanelPresentation.CanSubmit(changed with { CurrentConfigurationFingerprint = "fingerprint-a" }));

        // A changed source or action means the explanation belongs to the previous request.
        var stale = input with { IsStale = true };
        Assert.Equal(PanelBarAction.Execute, PanelPresentation.ResolveBarAction(stale));
        Assert.True(PanelPresentation.CanSubmit(stale));
    }

    [Fact]
    public void TransientFailuresOfferAnExplicitRetry()
    {
        foreach (var category in new[]
                 {
                     ProcessingFailureCategory.Timeout,
                     ProcessingFailureCategory.Offline,
                     ProcessingFailureCategory.Limited,
                     ProcessingFailureCategory.SecureConnection,
                     ProcessingFailureCategory.Unknown,
                 })
        {
            var input = Input(Failure(category), hasOutput: true);
            Assert.Equal(PanelBarAction.Retry, PanelPresentation.ResolveBarAction(input));
            Assert.True(PanelPresentation.CanSubmit(input), category.ToString());
        }
    }

    [Fact]
    public void LoadingAndOrdinaryStatesKeepExecuteOrReexecute()
    {
        var loading = Input(isProcessing: true, repeatsSubmittedAction: true);
        Assert.Equal(PanelBarAction.Stop, PanelPresentation.ResolveBarAction(loading));
        Assert.Equal("停止", PanelPresentation.ActionLabel(PanelBarAction.Stop));
        Assert.False(PanelPresentation.CanSubmit(loading));

        Assert.Equal(PanelBarAction.Execute, PanelPresentation.ResolveBarAction(Input(repeatsSubmittedAction: false)));
        Assert.Equal("执行", PanelPresentation.ActionLabel(PanelBarAction.Execute));
        Assert.Equal(PanelBarAction.Reexecute, PanelPresentation.ResolveBarAction(Input(repeatsSubmittedAction: true)));
        Assert.Equal("重新执行", PanelPresentation.ActionLabel(PanelBarAction.Reexecute));

        // Local OCR reading the capture keeps execution disabled, as before #73.
        Assert.False(PanelPresentation.CanSubmit(Input(isRecognizing: true)));
        Assert.False(PanelPresentation.CanSubmit(Input(hasSubmittableInput: false)));
    }

    [Fact]
    public void ResultHeaderNamesTheFailureStateAndTheFailedModel()
    {
        var failure = Failure(ProcessingFailureCategory.Timeout);
        var empty = Input(failure);
        Assert.Equal("请求失败", PanelPresentation.ResultTitle(empty));
        Assert.Equal("· deepseek-chat", PanelPresentation.ResultDetail(empty));

        var partial = Input(failure, hasOutput: true);
        Assert.Equal("未完成", PanelPresentation.ResultTitle(partial));
        Assert.Equal("· deepseek-chat", PanelPresentation.ResultDetail(partial));

        var stalePartial = Input(failure, hasOutput: true, isStale: true);
        Assert.Equal("上次请求未完成", PanelPresentation.ResultTitle(stalePartial));

        var staleEmpty = Input(failure, isStale: true);
        Assert.Equal("上次请求失败", PanelPresentation.ResultTitle(staleEmpty));

        // While running, the header says what is happening.
        Assert.Equal("正在执行", PanelPresentation.ResultTitle(Input(isProcessing: true)));

        // Without a failure the stale result keeps its own wording and detail.
        var stale = Input(isStale: true, repeatsSubmittedAction: true);
        Assert.Equal("上次结果", PanelPresentation.ResultTitle(stale));
        Assert.Equal("· 待更新", PanelPresentation.ResultDetail(stale));

        Assert.Equal("已停止", PanelPresentation.ResultTitle(Input(phase: ResultPhase.Stopped)));
        Assert.Equal("结果", PanelPresentation.ResultTitle(Input()));
        Assert.Equal("", PanelPresentation.ResultDetail(Input()));
    }

    [Fact]
    public void FailureExplanationDescribesStalenessChangedConfigurationAndCategory()
    {
        Assert.Null(PanelPresentation.FailureExplanation(Input()));

        // An unchanged configuration failure shows the category explanation, never provider prose.
        var configuration = Failure(ProcessingFailureCategory.Configuration);
        Assert.Equal(configuration.Explanation, PanelPresentation.FailureExplanation(Input(configuration)));

        var unknown = Failure(ProcessingFailureCategory.Unknown);
        Assert.Contains("请求没有完成", PanelPresentation.FailureExplanation(Input(unknown)));

        var changed = Input(configuration) with { CurrentConfigurationFingerprint = "fingerprint-b" };
        Assert.Contains("模型配置已更新", PanelPresentation.FailureExplanation(changed));
        Assert.Contains("重试", PanelPresentation.FailureExplanation(changed));

        var stale = Input(unknown, isStale: true);
        Assert.Contains("上次请求", PanelPresentation.FailureExplanation(stale));
    }

    [Fact]
    public void CopyLabelKeepsExistingTextCopyableAndTextStaysVerbatim()
    {
        var failure = Failure(ProcessingFailureCategory.Timeout);
        Assert.Equal("复制已有文字", PanelPresentation.CopyLabel(Input(failure, hasOutput: true)));
        Assert.Equal("复制上次结果", PanelPresentation.CopyLabel(Input(isStale: true)));
        Assert.Equal("复制结果", PanelPresentation.CopyLabel(Input()));

        // The streamed prefix stays in the result; only the image is marked as incomplete.
        var state = new PanelState();
        var serial = state.Begin("source text", ProcessingAction.TranslationId, "configuration");
        Assert.True(state.Append(serial, "Partial output"));
        Assert.True(state.Fail(serial, failure));
        Assert.Equal("Partial output", state.Result);
        Assert.Equal(ResultPhase.Failed, state.Phase);
        Assert.False(state.Append(serial, "late chunk"));
        Assert.Equal("Partial output", state.Result);
    }

    [Fact]
    public void IncompleteImagesMarkInterruptionAndStoppedGeneration()
    {
        Assert.Equal("未完成 · 请求中断", PanelPresentation.IncompleteMarkText(ResultPhase.Failed));
        Assert.Equal("未完成 · 生成时已停止", PanelPresentation.IncompleteMarkText(ResultPhase.Stopped));
        Assert.Null(PanelPresentation.IncompleteMarkText(ResultPhase.Completed));
        Assert.Null(PanelPresentation.IncompleteMarkText(ResultPhase.Streaming));
        Assert.Null(PanelPresentation.IncompleteMarkText(ResultPhase.Unrecognized));

        Assert.Equal("Partial…", PanelPresentation.ImageResultText("Partial", ResultPhase.Failed));
        Assert.Equal("Partial…", PanelPresentation.ImageResultText("Partial", ResultPhase.Stopped));
        Assert.Equal("Partial", PanelPresentation.ImageResultText("Partial", ResultPhase.Completed));
        Assert.Equal("", PanelPresentation.ImageResultText("", ResultPhase.Completed));
    }

    [Fact]
    public void ResultDocumentExposesTheMarkedImageOverloadForThePanelCopy()
    {
        var type = typeof(PanelWindow).Assembly.GetType("Cida.Desktop.ResultDocument");
        Assert.NotNull(type);

        // The panel's copy-image path passes State.Phase through this overload (#73).
        var marked = type!.GetMethod("Image", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(string), typeof(ResultPhase) }, null);
        Assert.NotNull(marked);
        Assert.Equal(typeof(System.Windows.Media.Imaging.BitmapSource), marked!.ReturnType);

        // The long-standing two-argument entry point keeps working for existing callers.
        var finished = type.GetMethod("Image", BindingFlags.Static | BindingFlags.Public, null,
            new[] { typeof(string), typeof(string) }, null);
        Assert.NotNull(finished);
        Assert.Equal(typeof(System.Windows.Media.Imaging.BitmapSource), finished!.ReturnType);
    }
}
