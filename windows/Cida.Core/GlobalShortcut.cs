namespace Cida.Core;

/// <summary>
/// A key combination that works from any application. Ported from upstream GlobalShortcut.swift;
/// key codes are Windows virtual-key codes (VK_*). On Windows the "option" modifier is Alt.
/// </summary>
public readonly record struct GlobalShortcut(ushort KeyCode, ShortcutModifiers Modifiers)
{
    public static readonly GlobalShortcut AltA = new(0x41, ShortcutModifiers.Alt); // VK 'A'
    public static readonly GlobalShortcut AltS = new(0x53, ShortcutModifiers.Alt); // VK 'S'
    public static readonly GlobalShortcut AltD = new(0x44, ShortcutModifiers.Alt); // VK 'D'

    /// <summary>At least one of these makes a combination a shortcut.</summary>
    public static readonly ShortcutModifiers Required = ShortcutModifiers.Control
        | ShortcutModifiers.Alt | ShortcutModifiers.Win;

    /// <summary>The same key with Shift added: Alt+D's Shift variant translates the whole window.</summary>
    public GlobalShortcut AddingShift() => this with { Modifiers = Modifiers | ShortcutModifiers.Shift };

    /// <summary>The chip text: modifier symbols, then the key, separated by spaces.</summary>
    public string DisplayText =>
        string.Join(" ", SymbolList().Append(KeyDisplayName));

    private IEnumerable<string> SymbolList()
    {
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) yield return "Shift";
        if (Modifiers.HasFlag(ShortcutModifiers.Win)) yield return "Win";
    }

    public string KeyDisplayName
    {
        get
        {
            if (KeyNamesByCode().TryGetValue(KeyCode, out var name))
            {
                return name;
            }
            return $"Key {KeyCode}";
        }
    }

    public override string ToString() => DisplayText;

    // MARK: Text form (the command line's spelling, e.g. "control+alt+t")

    /// <summary>What an action without a shortcut is written as.</summary>
    public const string NoneConfigurationText = "none";

    public static GlobalShortcut? FromConfigurationText(string text)
    {
        var parts = text.ToLowerInvariant().Split('+', StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0) return null;
        var keyName = parts[^1];
        if (keyName.Length == 0) return null;
        var keyCode = KeyCodeByName(keyName);
        if (keyCode == null) return null;
        var modifiers = ShortcutModifiers.None;
        foreach (var name in parts[..^1])
        {
            var modifier = name switch
            {
                "control" or "ctrl" or "⌃" => ShortcutModifiers.Control,
                "option" or "opt" or "alt" or "⌥" => ShortcutModifiers.Alt,
                "shift" or "⇧" => ShortcutModifiers.Shift,
                "command" or "cmd" or "win" or "meta" or "⌘" => ShortcutModifiers.Win,
                _ => (ShortcutModifiers?)null,
            };
            if (modifier == null) return null;
            modifiers |= modifier.Value;
        }
        if ((modifiers & Required) == 0) return null;
        return new GlobalShortcut(keyCode.Value, modifiers);
    }

    public string ConfigurationText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("control");
            if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("option");
            if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("shift");
            if (Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("command");
            parts.Add(KeyNameByCode(KeyCode) ?? $"key{KeyCode}");
            return string.Join("+", parts);
        }
    }

    private static readonly Dictionary<ushort, string> KeyNames = BuildKeyNames();

    private static Dictionary<ushort, string> BuildKeyNames()
    {
        var names = new Dictionary<ushort, string>
        {
            [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4",
            [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
            [0x41] = "a", [0x42] = "b", [0x43] = "c", [0x44] = "d", [0x45] = "e",
            [0x46] = "f", [0x47] = "g", [0x48] = "h", [0x49] = "i", [0x4A] = "j",
            [0x4B] = "k", [0x4C] = "l", [0x4D] = "m", [0x4E] = "n", [0x4F] = "o",
            [0x50] = "p", [0x51] = "q", [0x52] = "r", [0x53] = "s", [0x54] = "t",
            [0x55] = "u", [0x56] = "v", [0x57] = "w", [0x58] = "x", [0x59] = "y",
            [0x5A] = "z",
            [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\",
            [0xBA] = ";", [0xDE] = "'", [0xBC] = ",", [0xBE] = ".", [0xBF] = "/",
            [0xC0] = "`",
            [0x20] = "space", [0x0D] = "return", [0x09] = "tab", [0x08] = "delete",
            [0x2E] = "forward-delete", [0x1B] = "escape",
            [0x25] = "left", [0x27] = "right", [0x26] = "up", [0x28] = "down",
            [0x24] = "home", [0x23] = "end", [0x21] = "page-up", [0x22] = "page-down",
        };
        for (ushort key = 0x70; key <= 0x87; key++)
        {
            names[key] = $"f{key - 0x70 + 1}";
        }
        // Aliases.
        names[0x0D] = "return";
        return names;
    }

    private static Dictionary<ushort, string> KeyNamesByCode() => KeyNames;

    private static ushort? KeyCodeByName(string name)
    {
        var aliases = new Dictionary<string, string>
        {
            ["enter"] = "return", ["esc"] = "escape", ["backspace"] = "delete",
        };
        var canonical = aliases.GetValueOrDefault(name, name);
        foreach (var (code, keyName) in KeyNames)
        {
            if (keyName == canonical) return code;
        }
        return null;
    }

    private static string? KeyNameByCode(ushort code) => KeyNames.GetValueOrDefault(code);
}

[Flags]
public enum ShortcutModifiers : byte
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3,
}

/// <summary>What a global shortcut does; each action has its own combination.</summary>
public enum GlobalShortcutAction
{
    /// <summary>Shows or hides the panel, bringing in the frontmost selection.</summary>
    ShowPanel,

    /// <summary>Freezes the screen, lets the user frame some text, and translates it.</summary>
    CaptureText,

    /// <summary>Translates the paragraph under the pointer; with Shift, the whole window.</summary>
    TranslationLayer,
}

public static class GlobalShortcutActionExtensions
{
    public static GlobalShortcut DefaultShortcut(this GlobalShortcutAction action) => action switch
    {
        GlobalShortcutAction.ShowPanel => GlobalShortcut.AltA,
        GlobalShortcutAction.CaptureText => GlobalShortcut.AltS,
        GlobalShortcutAction.TranslationLayer => GlobalShortcut.AltD,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
