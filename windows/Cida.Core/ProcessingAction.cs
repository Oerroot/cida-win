using System.Text.Json.Serialization;

namespace Cida.Core;

public sealed record ProcessingAction
{
    public const string TranslationId = "translate";
    public const string ImprovementId = "improve";
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; init; } = "新动作";
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonIgnore] public ProcessingMode Mode => Id switch
    {
        TranslationId => ProcessingMode.Translate,
        ImprovementId => ProcessingMode.Improve,
        _ => ProcessingMode.Custom,
    };
    public static IReadOnlyList<ProcessingAction> Defaults(string translation, string improvement) =>
    [new() { Id = TranslationId, Name = "翻译", Prompt = translation },
     new() { Id = ImprovementId, Name = "改进", Prompt = improvement }];

    public static IReadOnlyList<ProcessingAction> Normalize(IReadOnlyList<ProcessingAction>? actions,
        string translation, string improvement)
    {
        if (actions == null) return Defaults(translation, improvement);
        var result = actions.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Name))
            .DistinctBy(a => a.Id).Select(a => a with { Name = a.Name.Trim(), Prompt = a.Prompt ?? "",
                Enabled = a.Enabled && (a.Mode != ProcessingMode.Custom || !string.IsNullOrWhiteSpace(a.Prompt)) }).ToList();
        var index = result.FindIndex(a => a.Id == TranslationId);
        if (index < 0) result.Insert(0, Defaults(translation, improvement)[0]);
        else result[index] = result[index] with { Enabled = true };
        return result;
    }
}
