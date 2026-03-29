// =============================================================================
// ModConfigBridge.cs — Drop-in Template for ModConfig Integration
// =============================================================================
// Copy this file into your mod's Scripts/ folder, then:
//   1. Replace "YourMod" namespace and mod IDs with your own
//   2. Edit BuildEntries() to define your config items
//   3. Call ModConfigBridge.DeferredRegister() in your mod's Initialize()
//
// Zero DLL reference needed — everything is done via reflection.
// If ModConfig is not installed, your mod works normally (all GetValue calls
// return the fallback you provide).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace YourMod.Scripts;

internal static class ModConfigBridge
{
    // ─── State ──────────────────────────────────────────────────
    private static bool _available;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configTypeEnum;

    internal static bool IsAvailable => _available;

    // ─── Step 1: Call this in your Initialize() ─────────────────
    // ModConfig may load AFTER your mod (alphabetical order).
    // Deferring to the next frame ensures ModConfig is ready.

    internal static void DeferredRegister()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += OnNextFrame;
    }

    private static void OnNextFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame -= OnNextFrame;
        Detect();
        if (_available) Register();
    }

    // ─── Step 2: Detect ModConfig via reflection ────────────────

    private static void Detect()
    {
        try
        {
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .ToArray();

            _apiType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configTypeEnum = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            _available = _apiType != null && _entryType != null && _configTypeEnum != null;
        }
        catch
        {
            _available = false;
        }
    }

    // ─── Step 3: Register your config entries ───────────────────

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            var entries = BuildEntries();

            // Localized display name (shows in ModConfig's mod list)
            var displayNames = new Dictionary<string, string>
            {
                ["en"] = "Your Mod Name",
                ["zhs"] = "你的模组名字",
            };

            // ModConfig has 2 overloads: 3-param (no i18n) and 4-param (with i18n).
            // We prefer 4-param when available.
            var registerMethod = _apiType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

            if (registerMethod.GetParameters().Length == 4)
            {
                registerMethod.Invoke(null, new object[]
                {
                    "your.mod.id",          // Must match your mod's ID
                    displayNames["en"],     // Fallback display name
                    displayNames,           // Localized display names
                    entries
                });
            }
            else
            {
                registerMethod.Invoke(null, new object[]
                {
                    "your.mod.id",
                    displayNames["en"],
                    entries
                });
            }
        }
        catch (Exception e)
        {
            // Log but don't crash — ModConfig is optional
            GD.PrintErr($"[YourMod] ModConfig registration failed: {e}");
        }
    }

    // ─── Read/Write Config Values ───────────────────────────────

    /// <summary>Read a saved config value, with fallback if ModConfig absent.</summary>
    internal static T GetValue<T>(string key, T fallback)
    {
        if (!_available) return fallback;
        try
        {
            var result = _apiType!.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static)
                ?.MakeGenericMethod(typeof(T))
                ?.Invoke(null, new object[] { "your.mod.id", key });
            return result != null ? (T)result : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Sync a value back to ModConfig (for persistence).
    /// Call this when your mod changes a setting outside ModConfig's UI
    /// (e.g. via hotkey or your own settings menu).
    /// </summary>
    internal static void SetValue(string key, object value)
    {
        if (!_available) return;
        try
        {
            _apiType!.GetMethod("SetValue", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new object[] { "your.mod.id", key, value });
        }
        catch { }
    }

    // ═════════════════════════════════════════════════════════════
    //  EDIT BELOW: Define your config entries
    // ═════════════════════════════════════════════════════════════

    private static Array BuildEntries()
    {
        var list = new List<object>();

        // ─── Section Header (visual only) ───────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Label", "General");
            Set(cfg, "Labels", L("General", "常规设置"));
            Set(cfg, "Type", EnumVal("Header"));
        }));

        // ─── Toggle (bool) ─────────────────────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "featureEnabled");
            Set(cfg, "Label", "Enable Feature");
            Set(cfg, "Labels", L("Enable Feature", "启用功能"));
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "Turn this feature on or off");
            Set(cfg, "Descriptions", L("Turn this feature on or off", "开启或关闭此功能"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                // TODO: Update your settings
                // YourSettings.FeatureEnabled = Convert.ToBoolean(v);
            }));
        }));

        // ─── Slider (float) ────────────────────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "speedMultiplier");
            Set(cfg, "Label", "Speed (x)");
            Set(cfg, "Labels", L("Speed (x)", "速度倍率"));
            Set(cfg, "Type", EnumVal("Slider"));
            Set(cfg, "DefaultValue", (object)1.0f);
            Set(cfg, "Min", 0.5f);
            Set(cfg, "Max", 5.0f);
            Set(cfg, "Step", 0.5f);
            Set(cfg, "Format", "F1");  // "F0"=no decimal, "F1"=1 decimal, "P0"=percent
            Set(cfg, "Description", "Adjust speed multiplier");
            Set(cfg, "Descriptions", L("Adjust speed multiplier", "调整速度倍率"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                // TODO: float val = Convert.ToSingle(v);
            }));
        }));

        // ─── Separator (visual divider) ────────────────────────

        list.Add(Entry(cfg => Set(cfg, "Type", EnumVal("Separator"))));

        // ─── Dropdown (string from options) ────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "difficulty");
            Set(cfg, "Label", "Difficulty");
            Set(cfg, "Labels", L("Difficulty", "难度"));
            Set(cfg, "Type", EnumVal("Dropdown"));
            Set(cfg, "DefaultValue", (object)"Normal");
            Set(cfg, "Options", new[] { "Easy", "Normal", "Hard" });
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                // TODO: string selected = Convert.ToString(v);
            }));
        }));

        // ─── KeyBind (Godot Key as long) ───────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "toggleKey");
            Set(cfg, "Label", "Toggle Hotkey");
            Set(cfg, "Labels", L("Toggle Hotkey", "切换快捷键"));
            Set(cfg, "Type", EnumVal("KeyBind"));
            Set(cfg, "DefaultValue", (object)(long)Key.F9);
            Set(cfg, "Description", "Press to toggle the feature");
            Set(cfg, "Descriptions", L("Press to toggle the feature", "按下切换功能"));
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
                // TODO: Key key = (Key)Convert.ToInt64(v);
            }));
        }));

        // ─── TextInput (string) ────────────────────────────────

        // list.Add(Entry(cfg =>
        // {
        //     Set(cfg, "Key", "playerName");
        //     Set(cfg, "Label", "Player Name");
        //     Set(cfg, "Type", EnumVal("TextInput"));
        //     Set(cfg, "DefaultValue", (object)"");
        //     Set(cfg, "MaxLength", 32);
        //     Set(cfg, "Placeholder", "Enter name...");
        //     Set(cfg, "Validator", new Func<object, bool>(v =>
        //         !string.IsNullOrWhiteSpace(Convert.ToString(v))));
        //     Set(cfg, "OnChanged", new Action<object>(v => { }));
        // }));

        // ─── Button (action, no saved value) ───────────────────

        // list.Add(Entry(cfg =>
        // {
        //     Set(cfg, "Key", "resetAll");
        //     Set(cfg, "Label", "Reset Data");
        //     Set(cfg, "Labels", L("Reset Data", "重置数据"));
        //     Set(cfg, "Type", EnumVal("Button"));
        //     Set(cfg, "ButtonText", "Reset");
        //     Set(cfg, "ButtonTexts", L("Reset", "重置"));
        //     Set(cfg, "OnChanged", new Action<object>(_ => { /* do reset */ }));
        // }));

        // ─── ColorPicker (hex string #RRGGBB) ─────────────────

        // list.Add(Entry(cfg =>
        // {
        //     Set(cfg, "Key", "highlightColor");
        //     Set(cfg, "Label", "Highlight Color");
        //     Set(cfg, "Type", EnumVal("ColorPicker"));
        //     Set(cfg, "DefaultValue", (object)"#FF6600");
        //     Set(cfg, "OnChanged", new Action<object>(v => { }));
        // }));

        // ─── Pack into typed array ─────────────────────────────

        var result = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++)
            result.SetValue(list[i], i);
        return result;
    }

    // ═════════════════════════════════════════════════════════════
    //  Reflection helpers (don't need to modify these)
    // ═════════════════════════════════════════════════════════════

    private static object Entry(Action<object> configure)
    {
        var inst = Activator.CreateInstance(_entryType!)!;
        configure(inst);
        return inst;
    }

    private static void Set(object obj, string name, object value)
        => obj.GetType().GetProperty(name)?.SetValue(obj, value);

    private static Dictionary<string, string> L(string en, string zhs)
        => new() { ["en"] = en, ["zhs"] = zhs };

    private static object EnumVal(string name)
        => Enum.Parse(_configTypeEnum!, name);
}
