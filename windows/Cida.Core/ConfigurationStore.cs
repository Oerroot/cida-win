namespace Cida.Core;

/// <summary>
/// Everything the commands read and write, as closures over the platform stores; tests
/// replace the parts that touch the system. Ported from upstream SettingsStore.swift
/// ConfigurationStore.
/// </summary>
public sealed record ConfigurationStore(
    Func<CidaSettings> LoadSettings,
    Action<CidaSettings> SaveSettings,
    Func<string?> ReadApiKey,
    Action<string> SaveApiKey,
    Action ClearApiKey,
    Func<bool> HasApiKey,
    Func<ModelServiceCheckRecord?> LoadLastCheck,
    Action<ModelServiceCheckRecord?> SaveLastCheck,
    Func<bool> LaunchAtLogin,
    Action<bool> SetLaunchAtLogin,
    Action NotifyChange);

/// <summary>
/// The check's transport; tests replace it so no request leaves the machine.
/// </summary>
public delegate Task<ModelServiceCheckResult> CheckRunner(CidaSettings settings);
