using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Luna;

namespace Penumbra.Services;

public class ValidityChecker : IService
{
    public readonly string Version;
    public readonly string CommitHash;

    public unsafe string GameVersion
    {
        get
        {
            var framework = Framework.Instance();
            return framework == null ? string.Empty : framework->GameVersionString;
        }
    }

    public string GetMainWindowLabel()
        => Version.Length is 0
            ? "Penumbra###PenumbraConfigWindow"
            : $"Penumbra v{Version}###PenumbraConfigWindow";

    public ValidityChecker(IDalamudPluginInterface pi)
    {
        var assembly = GetType().Assembly;
        Version    = assembly.GetName().Version?.ToString() ?? string.Empty;
        CommitHash = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
    }
}
