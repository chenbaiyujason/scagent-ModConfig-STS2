using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ModConfigSCAgent;

[ModInitializer("Initialize")]
public class MainFile
{
    /// <summary>与 mod_manifest / ModConfigSCAgent.json 的 id 一致，避免与官方 ModConfig 冲突。</summary>
    internal const string ModId = "sts2.scagent.modconfig";
    internal const string Version = "0.2.2-scagent";
    internal static readonly Logger Log = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        I18n.Initialize();
        ModConfigManager.Initialize();
        SettingsTabInjector.Initialize();

        Log.Info($"ModConfig-SCAgent v{Version} initialized! (zero Harmony, cross-platform)");
    }
}
