using System.Linq;
using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;

namespace ModConfigSCAgent;

/// <summary>
/// Internal state management and persistence for mod configurations.
/// </summary>
internal static class ModConfigManager
{
    private static readonly Dictionary<string, ModRegistration> _registrations = new();
    private static readonly Dictionary<string, Dictionary<string, object>> _values = new();

    // Save debounce: collect dirty modIds and flush after a short delay
    private static readonly HashSet<string> _dirtyMods = new();
    private static bool _saveScheduled;

    /// <summary>与官方 ModConfig 的 user://ModConfig/ 分离，避免配置混用。</summary>
    private const string ConfigDir = "user://ModConfigSCAgent/";
    private const string UiStateFile = "user://ModConfigSCAgent/_ui_state.json";

    internal static IReadOnlyDictionary<string, ModRegistration> Registrations => _registrations;

    /// <summary>查找已注册的配置项（供运行时更新下拉选项等）。</summary>
    internal static ConfigEntry? TryGetConfigEntry(string modId, string key)
    {
        if (!_registrations.TryGetValue(modId, out ModRegistration? reg))
        {
            return null;
        }

        // 不可使用 static lambda：需捕获参数 key。
        return reg.Entries.FirstOrDefault(entry => entry.Key == key);
    }

    // Persistent collapsed state for mod sections
    internal static HashSet<string> CollapsedMods { get; private set; } = new();

    // Persistent collapsed state for per-mod large section headers (e.g. "大模型接入")
    internal static HashSet<string> CollapsedSections { get; private set; } = new();

    internal static void Initialize()
    {
        DirAccess.MakeDirRecursiveAbsolute(ConfigDir);
        LoadUiState();
    }

    internal static void Register(ModRegistration reg)
    {
        NormalizeEntryDefaults(reg);
        _registrations[reg.ModId] = reg;

        if (!_values.ContainsKey(reg.ModId))
            _values[reg.ModId] = new Dictionary<string, object>();

        LoadValues(reg);
        SettingsTabInjector.RefreshUI();

        MainFile.Log.Info($"Registered config: {reg.DisplayName} ({reg.Entries.Length} entries)");
    }

    private static void NormalizeEntryDefaults(ModRegistration reg)
    {
        foreach (var entry in reg.Entries)
        {
            if (entry.Type != ConfigType.ColorPicker)
                continue;

            string normalized = NormalizeColorHexOrDefault(entry.DefaultValue);
            if (entry.DefaultValue is string current && string.Equals(current, normalized, StringComparison.Ordinal))
                continue;

            entry.DefaultValue = normalized;
            MainFile.Log.Info($"Normalized ColorPicker default [{reg.ModId}.{entry.Key}] -> {normalized}");
        }
    }

    private static string NormalizeColorHexOrDefault(object? value)
    {
        if (value is string text)
        {
            text = text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    var color = Color.FromHtml(text.TrimStart('#'));
                    return "#" + color.ToHtml(false).ToUpperInvariant();
                }
                catch
                {
                }
            }
        }

        return "#FFFFFF";
    }

    internal static T GetValue<T>(string modId, string key)
    {
        if (_values.TryGetValue(modId, out var modValues) &&
            modValues.TryGetValue(key, out var value))
        {
            try { return (T)Convert.ChangeType(value, typeof(T)); }
            catch { /* fall through to default */ }
        }

        if (_registrations.TryGetValue(modId, out var reg))
        {
            var entry = reg.Entries.FirstOrDefault(e => e.Key == key);
            if (entry != null)
            {
                try { return (T)Convert.ChangeType(entry.DefaultValue, typeof(T)); }
                catch { /* fall through */ }
            }
        }

        return default!;
    }

    internal static void SetValue(string modId, string key, object value)
    {
        SetValueCore(modId, key, value, invokeOnChanged: true, persistToDisk: true);
    }

    internal static void SetValueWithoutSave(string modId, string key, object value)
    {
        SetValueCore(modId, key, value, invokeOnChanged: false, persistToDisk: false);
    }

    private static void SetValueCore(string modId, string key, object value, bool invokeOnChanged, bool persistToDisk)
    {
        if (!_values.ContainsKey(modId))
            _values[modId] = new Dictionary<string, object>();

        _values[modId][key] = value;

        if (invokeOnChanged && _registrations.TryGetValue(modId, out var reg))
        {
            var entry = reg.Entries.FirstOrDefault(e => e.Key == key);
            try { entry?.OnChanged?.Invoke(value); }
            catch (Exception e) { MainFile.Log.Error($"Config callback error [{modId}.{key}]: {e}"); }
        }

        SettingsTabInjector.NotifyValueChanged(modId, key, value);
        if (persistToDisk)
            ScheduleSave(modId);
    }

    /// <summary>
    /// Reset all config values for a mod to their defaults.
    /// Returns true if any values were actually changed.
    /// </summary>
    internal static bool ResetToDefaults(string modId)
    {
        if (!_registrations.TryGetValue(modId, out var reg))
            return false;

        if (!_values.ContainsKey(modId))
            _values[modId] = new Dictionary<string, object>();

        bool changed = false;
        foreach (var entry in reg.Entries)
        {
            if (entry.Type is ConfigType.Header or ConfigType.Separator or ConfigType.Button)
                continue;

            var oldValue = _values[modId].GetValueOrDefault(entry.Key);
            _values[modId][entry.Key] = entry.DefaultValue;

            if (!Equals(oldValue, entry.DefaultValue))
            {
                changed = true;
                try { entry.OnChanged?.Invoke(entry.DefaultValue); }
                catch (Exception e) { MainFile.Log.Error($"Config callback error [{modId}.{entry.Key}]: {e}"); }
            }
        }

        if (changed)
            ScheduleSave(modId);

        return changed;
    }

    // ─── Save Debounce ───────────────────────────────────────────

    private static void ScheduleSave(string modId)
    {
        _dirtyMods.Add(modId);
        if (_saveScheduled) return;
        _saveScheduled = true;

        // Flush on next idle frame — batches rapid slider changes into one write
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame += FlushSaves;
        else
            FlushSaves(); // fallback: save immediately
    }

    private static void FlushSaves()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame -= FlushSaves;

        _saveScheduled = false;
        foreach (var modId in _dirtyMods)
            SaveValues(modId);
        _dirtyMods.Clear();
    }

    private static void LoadValues(ModRegistration reg)
    {
        var path = ConfigDir + reg.ModId + ".json";

        Dictionary<string, JsonElement>? saved = null;
        if (FileAccess.FileExists(path))
        {
            try
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                var json = file.GetAsText();
                saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Failed to load config for {reg.ModId}: {e}");
            }
        }

        foreach (var entry in reg.Entries)
        {
            if (entry.Type is ConfigType.Header or ConfigType.Separator or ConfigType.Button)
                continue;

            if (saved != null && saved.TryGetValue(entry.Key, out var element))
            {
                try
                {
                    _values[reg.ModId][entry.Key] = entry.Type switch
                    {
                        ConfigType.Toggle => element.GetBoolean(),
                        ConfigType.Slider => (float)element.GetDouble(),
                        ConfigType.Dropdown => element.GetString() ?? (string)entry.DefaultValue,
                        ConfigType.KeyBind => element.GetInt64(),
                        ConfigType.TextInput => element.GetString() ?? (string)entry.DefaultValue,
                        ConfigType.ColorPicker => element.GetString() ?? (string)entry.DefaultValue,
                        _ => entry.DefaultValue
                    };
                    continue;
                }
                catch { /* fall through to default */ }
            }

            _values[reg.ModId][entry.Key] = entry.DefaultValue;
        }
    }

    private static void SaveValues(string modId)
    {
        if (!_values.TryGetValue(modId, out var modValues))
            return;

        var path = ConfigDir + modId + ".json";
        try
        {
            var json = JsonSerializer.Serialize(modValues, new JsonSerializerOptions { WriteIndented = true });
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            file.StoreString(json);
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to save config for {modId}: {e}");
        }
    }

    internal static void SaveAll()
    {
        foreach (var modId in _values.Keys)
            SaveValues(modId);
    }

    // ─── UI State Persistence ─────────────────────────────────────

    internal static void SaveUiState()
    {
        try
        {
            var state = new Dictionary<string, object>
            {
                ["collapsed_mods"] = CollapsedMods.ToArray(),
                ["collapsed_sections"] = CollapsedSections.ToArray()
            };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            using var file = FileAccess.Open(UiStateFile, FileAccess.ModeFlags.Write);
            file.StoreString(json);
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to save UI state: {e}");
        }
    }

    private static void LoadUiState()
    {
        try
        {
            if (!FileAccess.FileExists(UiStateFile)) return;
            using var file = FileAccess.Open(UiStateFile, FileAccess.ModeFlags.Read);
            var json = file.GetAsText();
            var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (state != null && state.TryGetValue("collapsed_mods", out var collapsed))
            {
                foreach (var item in collapsed.EnumerateArray())
                {
                    var val = item.GetString();
                    if (val != null) CollapsedMods.Add(val);
                }
            }

            if (state != null && state.TryGetValue("collapsed_sections", out var collapsedSections))
            {
                foreach (var item in collapsedSections.EnumerateArray())
                {
                    var val = item.GetString();
                    if (val != null) CollapsedSections.Add(val);
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to load UI state: {e}");
        }
    }
}
