using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace ModConfigSCAgent;

/// <summary>
/// 注入「SCAgent」设置页：展示<strong>本程序集</strong> <see cref="ModConfigApi.Register"/> 的<strong>全部</strong>模组配置。
/// 与官方 ModConfig 的注册表、标签页彼此独立；官方仍走其「Mods」页。
/// Zero Harmony — pure Godot public API + reflection for private field access.
/// </summary>
internal static class SettingsTabInjector
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // 主菜单 / 暂停菜单各一套内容容器
    private static readonly List<WeakReference<VBoxContainer>> _allContainers = new();
    private static readonly List<WeakReference<NSettingsTab>> _allTabs = new();
    /// <summary>同一 modId+key 可能对应多个 OptionButton（多实例设置界面），刷新选项时全部重建。</summary>
    private static readonly Dictionary<(string ModId, string Key), List<WeakReference<OptionButton>>> _dropdownWidgets = new();

    /// <summary>可折叠配置行根节点（含表单项与底部分隔线），供运行时按逻辑显示/隐藏整行。</summary>
    private static readonly Dictionary<(string ModId, string Key), List<WeakReference<Control>>> _entryRowHosts = new();
    /// <summary>每个配置项登记的可交互控件，用于运行时切只读/恢复。</summary>
    private static readonly Dictionary<(string ModId, string Key), List<ReadonlyBinding>> _readonlyBindings = new();
    /// <summary>每个配置项当前是否处于只读态，以及其提示文案。</summary>
    private static readonly Dictionary<(string ModId, string Key), EntryReadonlyState> _entryReadonlyStates = new();
    private static bool _i18nSubscribed;
    private static CanvasLayer? _readonlyNoticeLayer;

    // KeyBind capture state
    private static Button? _activeKeyBindButton;
    private static string _activeKeyBindModId = "";
    private static ConfigEntry? _activeKeyBindEntry;

    // Collapsed state now persisted via ModConfigManager.CollapsedMods
    private static readonly Dictionary<(string ModId, string Key), List<LiveBinding>> _liveBindings = new();

    // Cached game font for manually created labels (fixes Linux font rendering)
    private static Font? _gameFont;

    private sealed class LiveBinding
    {
        public LiveBinding(Func<object, bool> apply)
        {
            Apply = apply;
        }

        public Func<object, bool> Apply { get; }
    }

    private sealed class ReadonlyBinding
    {
        public required WeakReference<Control> ControlRef { get; init; }
        public required Action<Control, bool> ApplyReadonlyState { get; init; }
        public required Func<string?> ResolveNormalTooltip { get; init; }
    }

    private sealed class EntryReadonlyState
    {
        public bool IsReadonly { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class UiUpdateGuard
    {
        public bool Suppress { get; set; }
    }

    private sealed class SectionRenderState
    {
        public required string ModId { get; init; }
        public required ConfigEntry HeaderEntry { get; init; }
        public required VBoxContainer Body { get; init; }
        public required Label HeaderLabel { get; init; }
        public required bool IsCollapsible { get; init; }
    }

    // ─── Colors matching the game's settings screen palette ─────────
    private static readonly Color CreamGold = new("D4C88E");
    private static readonly Color DimText = new("8A7E5C");
    private static readonly Color TextColor = new(0.9f, 0.85f, 0.75f);
    private static readonly Color ResetColor = new(0.8f, 0.5f, 0.4f);
    private static readonly Color KeyBindListening = new(1.0f, 0.85f, 0.3f);
    private static readonly Color ModHeaderBg = new("2C434F");
    private static readonly Color SectionHeaderBg = new("22333D");
    private static readonly Color RowSeparatorColor = new("2C434F", 0.5f);
    private static readonly Color ReadonlyRowModulate = new(0.72f, 0.72f, 0.72f, 0.92f);

    internal static void Initialize()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.NodeAdded += OnNodeAdded;
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is not NSettingsTabManager)
            return;

        // 本 fork 专用标签名，勿与官方 ModConfig 的 "Mods" 重名；已注入则跳过（主菜单 / 暂停各一次）
        if (node.GetNodeOrNull("SCAgent") != null)
            return;

        node.Connect("ready",
            Callable.From(() => InjectModsTab((NSettingsTabManager)node)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    /// <summary>注入一页：列出所有通过 <c>ModConfigSCAgent.ModConfigApi</c> 注册的模组（含多 modId）。</summary>
    private static void InjectModsTab(NSettingsTabManager tabManager)
    {
        try
        {
            var tabsField = typeof(NSettingsTabManager).GetField("_tabs", PrivateInstance);
            if (tabsField == null)
            {
                MainFile.Log.Error("Cannot find _tabs field on NSettingsTabManager");
                return;
            }

            var tabs = tabsField.GetValue(tabManager) as IDictionary;
            if (tabs == null || tabs.Count == 0)
            {
                MainFile.Log.Error("_tabs dictionary is empty or null");
                return;
            }

            NSettingsTab? firstTab = null;
            NSettingsPanel? firstPanel = null;
            foreach (DictionaryEntry entry in tabs)
            {
                firstTab = entry.Key as NSettingsTab;
                firstPanel = entry.Value as NSettingsPanel;
                break;
            }

            if (firstTab == null || firstPanel == null)
            {
                MainFile.Log.Error("Could not find existing tab/panel to clone");
                return;
            }

            CacheGameFont(firstPanel);

            InjectOneSettingsPage(tabManager, tabs, firstTab, firstPanel,
                tabNodeName: "SCAgent",
                panelNodeName: "SCAgentSettings",
                tabLabel: I18n.TabScagent);

            MainFile.Log.Info("ModConfig-SCAgent tab injected into settings screen.");
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to inject settings tabs: {e}");
        }
    }

    /// <summary>克隆原生设置页结构，挂接一个自定义标签与面板。</summary>
    private static void InjectOneSettingsPage(
        NSettingsTabManager tabManager,
        IDictionary tabs,
        NSettingsTab firstTab,
        NSettingsPanel firstPanel,
        string tabNodeName,
        string panelNodeName,
        string tabLabel)
    {
        var modsTab = (NSettingsTab)firstTab.Duplicate();
        modsTab.Name = tabNodeName;

        var tabImage = modsTab.GetNodeOrNull<TextureRect>("TabImage");
        if (tabImage?.Material is ShaderMaterial shader)
            tabImage.Material = (ShaderMaterial)shader.Duplicate();

        tabManager.AddChild(modsTab);
        modsTab.SetLabel(tabLabel);
        modsTab.Deselect();
        PositionNewTab(tabs, modsTab);

        var modsPanel = (NSettingsPanel)firstPanel.Duplicate();
        modsPanel.Name = panelNodeName;
        modsPanel.Visible = false;
        Control? preReadyFocusSentinel = CreatePreReadyFocusSentinel(firstPanel);

        var contentName = firstPanel.Content?.Name;
        VBoxContainer? contentContainer = null;

        foreach (var child in modsPanel.GetChildren().ToArray())
        {
            bool keepAsContent =
                child is VBoxContainer &&
                ((contentName != null && child.Name == contentName) ||
                 (contentName == null && contentContainer == null));

            if (keepAsContent && child is VBoxContainer vbox)
            {
                contentContainer = vbox;
                foreach (var inner in vbox.GetChildren().ToArray())
                {
                    vbox.RemoveChild(inner);
                    inner.Free();
                }
            }
            else
            {
                modsPanel.RemoveChild(child);
                child.Free();
            }
        }

        if (contentContainer != null && preReadyFocusSentinel != null)
        {
            preReadyFocusSentinel.Name = "__PreReadyFocusSentinel";
            preReadyFocusSentinel.Visible = false;
            preReadyFocusSentinel.MouseFilter = Control.MouseFilterEnum.Ignore;
            contentContainer.AddChild(preReadyFocusSentinel);
        }

        firstPanel.GetParent().AddChild(modsPanel);

        if (contentContainer == null)
            contentContainer = modsPanel.Content;

        _allContainers.Add(new WeakReference<VBoxContainer>(contentContainer));

        tabs.Add(modsTab, modsPanel);

        modsTab.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(delegate
            {
                try { tabManager.Call("SwitchTabTo", modsTab); }
                catch (Exception e) { MainFile.Log.Error($"Tab switch failed: {e}"); }
            }));

        _allTabs.Add(new WeakReference<NSettingsTab>(modsTab));
        if (!_i18nSubscribed)
        {
            I18n.Changed += OnLanguageChanged;
            _i18nSubscribed = true;
        }

        try
        {
            float maxHeight = firstPanel.Size.Y;
            if (maxHeight < 100)
                maxHeight = modsPanel.GetParent<Control>().Size.Y * 0.85f;
            modsPanel.Size = new Vector2(modsPanel.Size.X, maxHeight);

            var viewport = modsPanel.GetViewport();
            if (viewport != null)
                OverrideRefreshSize(viewport, modsPanel, maxHeight);
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to cap panel height: {e}");
        }

        PopulateInto(contentContainer);
        RebuildFocusTargets(modsPanel, contentContainer, preReadyFocusSentinel);
    }

    private static void PositionNewTab(IDictionary tabs, NSettingsTab modsTab)
    {
        var existingTabs = new List<NSettingsTab>();
        foreach (DictionaryEntry entry in tabs)
            existingTabs.Add((NSettingsTab)entry.Key);

        if (existingTabs.Count < 2) return;

        float spacing = existingTabs[1].Position.X - existingTabs[0].Position.X;
        var lastTab = existingTabs.Last();

        modsTab.Position = new Vector2(lastTab.Position.X + spacing, lastTab.Position.Y);
        modsTab.Size = existingTabs[0].Size;

        var tabManager = modsTab.GetParent<Control>();
        float rightEdge = modsTab.Position.X + modsTab.Size.X;
        if (rightEdge > tabManager.Size.X && tabManager.Size.X > 0)
        {
            int totalTabs = existingTabs.Count + 1;
            float tabWidth = existingTabs[0].Size.X;
            float newSpacing = tabManager.Size.X / totalTabs;
            float startX = (newSpacing - tabWidth) / 2f;

            for (int i = 0; i < existingTabs.Count; i++)
                existingTabs[i].Position = new Vector2(startX + newSpacing * i, existingTabs[i].Position.Y);

            modsTab.Position = new Vector2(startX + newSpacing * existingTabs.Count, existingTabs[0].Position.Y);
        }
    }

    private static void OnLanguageChanged()
    {
        for (int i = _allTabs.Count - 1; i >= 0; i--)
        {
            if (!_allTabs[i].TryGetTarget(out var tab) || !GodotObject.IsInstanceValid(tab))
            {
                _allTabs.RemoveAt(i);
                continue;
            }

            if (tab.Name == "SCAgent")
                tab.SetLabel(I18n.TabScagent);
        }

        RefreshUI();
    }

    internal static void RefreshUI()
    {
        _liveBindings.Clear();
        _dropdownWidgets.Clear();
        _entryRowHosts.Clear();

        for (int i = _allContainers.Count - 1; i >= 0; i--)
        {
            if (!_allContainers[i].TryGetTarget(out var container) ||
                !GodotObject.IsInstanceValid(container))
            {
                _allContainers.RemoveAt(i);
                continue;
            }

            foreach (var child in container.GetChildren().ToArray())
                child.QueueFree();
            PopulateInto(container);

            var panel = FindOwningSettingsPanel(container);
            if (panel != null)
                RebuildFocusTargets(panel, container, null);
        }
    }

    private static Control? CreatePreReadyFocusSentinel(NSettingsPanel firstPanel)
    {
        // Try 1: Duplicate the default focused control (usually an NButton/NTickbox)
        try
        {
            if (firstPanel.DefaultFocusedControl is Control defaultFocused &&
                GodotObject.IsInstanceValid(defaultFocused) &&
                defaultFocused.Duplicate() is Control duplicate)
            {
                duplicate.FocusMode = Control.FocusModeEnum.All;
                duplicate.Visible = false;
                return duplicate;
            }
        }
        catch { /* fall through to search */ }

        // Try 2: Find any game settings control from original panel's content tree
        try
        {
            if (firstPanel.Content is Control content)
            {
                var candidate = FindFirstGameSettingsControl(content);
                if (candidate != null && candidate.Duplicate() is Control dup2)
                {
                    dup2.FocusMode = Control.FocusModeEnum.All;
                    dup2.Visible = false;
                    return dup2;
                }
            }
        }
        catch { /* fall through */ }

        return null;
    }

    /// <summary>
    /// Search for a control that NSettingsPanel.IsSettingsOption() would recognize:
    /// NTickbox, NSettingsSlider, NDropdownPositioner, NPaginator, or enabled NButton.
    /// Uses type name check to avoid hard import dependencies on game UI types.
    /// </summary>
    private static Control? FindFirstGameSettingsControl(Control parent)
    {
        foreach (var child in parent.GetChildren().OfType<Control>())
        {
            string typeName = child.GetType().Name;
            if (typeName is "NTickbox" or "NSettingsSlider" or "NDropdownPositioner" or "NPaginator")
                return child;

            var found = FindFirstGameSettingsControl(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Override RefreshSize's panel expansion on window resize.
    /// We can't cleanly disconnect the delegate-based Callable that _Ready() creates,
    /// so instead we re-cap the height after each resize using CallDeferred (runs after RefreshSize).
    /// </summary>
    private static void OverrideRefreshSize(Viewport viewport, NSettingsPanel modsPanel, float maxHeight)
    {
        var panelRef = new WeakReference<NSettingsPanel>(modsPanel);
        viewport.SizeChanged += () =>
        {
            if (!panelRef.TryGetTarget(out var p) || !GodotObject.IsInstanceValid(p)) return;
            p.CallDeferred("set_size", new Vector2(p.Size.X, maxHeight));
        };
    }

    /// <summary>
    /// Cache the game's theme font from an existing settings panel for use in manually created labels.
    /// Fixes font rendering issues on Linux where default system font may lack CJK glyphs.
    /// </summary>
    private static void CacheGameFont(NSettingsPanel panel)
    {
        if (_gameFont != null) return;
        try
        {
            // Walk the original panel's content to find an existing Label with a theme font
            if (panel.Content == null) return;
            foreach (var child in panel.Content.GetChildren())
            {
                if (child is Label label)
                {
                    _gameFont = label.GetThemeFont("font");
                    if (_gameFont != null) return;
                }
                // Check one level deeper
                if (child is Control container)
                {
                    foreach (var inner in container.GetChildren())
                    {
                        if (inner is Label innerLabel)
                        {
                            _gameFont = innerLabel.GetThemeFont("font");
                            if (_gameFont != null) return;
                        }
                    }
                }
            }
        }
        catch { /* non-critical, fall back to default font */ }
    }

    /// <summary>Apply cached game font to a label if available.</summary>
    private static void ApplyGameFont(Label label)
    {
        if (_gameFont != null)
            label.AddThemeFontOverride("font", _gameFont);
    }

    private static NSettingsPanel? FindOwningSettingsPanel(Control control)
    {
        Node? current = control;
        while (current != null)
        {
            if (current is NSettingsPanel panel)
                return panel;
            current = current.GetParent();
        }

        return null;
    }

    private static void RebuildFocusTargets(
        NSettingsPanel panel,
        VBoxContainer contentContainer,
        Control? preReadyFocusSentinel)
    {
        try
        {
            List<Control> focusables = new();
            CollectFocusableControls(contentContainer, focusables);

            if (preReadyFocusSentinel != null &&
                GodotObject.IsInstanceValid(preReadyFocusSentinel) &&
                preReadyFocusSentinel.GetParent() == contentContainer &&
                focusables.Count > 0)
            {
                contentContainer.RemoveChild(preReadyFocusSentinel);
                preReadyFocusSentinel.QueueFree();
                preReadyFocusSentinel = null;
            }

            if (focusables.Count == 0 &&
                preReadyFocusSentinel != null &&
                GodotObject.IsInstanceValid(preReadyFocusSentinel))
            {
                focusables.Add(preReadyFocusSentinel);
            }

            for (int i = 0; i < focusables.Count; i++)
            {
                Control control = focusables[i];
                control.FocusNeighborLeft = control.GetPath();
                control.FocusNeighborRight = control.GetPath();
                control.FocusNeighborTop = (i > 0 ? focusables[i - 1] : control).GetPath();
                control.FocusNeighborBottom = (i < focusables.Count - 1 ? focusables[i + 1] : control).GetPath();
            }

            FieldInfo? firstControlField = typeof(NSettingsPanel).GetField("_firstControl", PrivateInstance);
            if (firstControlField != null)
                firstControlField.SetValue(panel, focusables.FirstOrDefault());
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to rebuild settings panel focus targets: {e}");
        }
    }

    private static void CollectFocusableControls(Control parent, List<Control> focusables)
    {
        foreach (Control child in parent.GetChildren().OfType<Control>())
        {
            if (!child.Visible)
                continue;

            if (child.FocusMode == Control.FocusModeEnum.All)
                focusables.Add(child);

            CollectFocusableControls(child, focusables);
        }
    }

    internal static void NotifyValueChanged(string modId, string key, object value)
    {
        var bindingKey = (modId, key);
        if (!_liveBindings.TryGetValue(bindingKey, out var bindings))
            return;

        for (int i = bindings.Count - 1; i >= 0; i--)
        {
            try
            {
                if (!bindings[i].Apply(value))
                    bindings.RemoveAt(i);
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Live config UI update failed [{modId}.{key}]: {e}");
                bindings.RemoveAt(i);
            }
        }

        if (bindings.Count == 0)
            _liveBindings.Remove(bindingKey);
    }

    private static void RegisterLiveBinding(string modId, string key, Func<object, bool> apply)
    {
        var bindingKey = (modId, key);
        if (!_liveBindings.TryGetValue(bindingKey, out var bindings))
        {
            bindings = new List<LiveBinding>();
            _liveBindings[bindingKey] = bindings;
        }

        bindings.Add(new LiveBinding(apply));
    }

    /// <summary>登记下拉控件，供 <see cref="ModConfigApi.SetDropdownOptions"/> 运行时改选项。</summary>
    private static void TrackDropdown(string modId, string key, OptionButton dropdown)
    {
        var dictKey = (modId, key);
        if (!_dropdownWidgets.TryGetValue(dictKey, out List<WeakReference<OptionButton>>? list))
        {
            list = new List<WeakReference<OptionButton>>();
            _dropdownWidgets[dictKey] = list;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out OptionButton? alive) || !GodotObject.IsInstanceValid(alive))
                list.RemoveAt(i);
        }

        list.Add(new WeakReference<OptionButton>(dropdown));
    }

    /// <summary>
    /// 登记某一配置项对应的整行容器（多实例设置页各登记一次）。
    /// </summary>
    private static void RegisterEntryRowHost(string modId, string key, Control host)
    {
        var dictKey = (modId, key);
        if (!_entryRowHosts.TryGetValue(dictKey, out List<WeakReference<Control>>? list))
        {
            list = new List<WeakReference<Control>>();
            _entryRowHosts[dictKey] = list;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out Control? alive) || !GodotObject.IsInstanceValid(alive))
                list.RemoveAt(i);
        }

        list.Add(new WeakReference<Control>(host));
        host.GuiInput += inputEvent => HandleReadonlyRowGuiInput(host, modId, key, inputEvent);
        ApplyReadonlyStateToRow(host, modId, key);
    }

    private static void RegisterReadonlyBinding(
        string modId,
        string key,
        Control control,
        Action<Control, bool> applyReadonlyState,
        Func<string?> resolveNormalTooltip)
    {
        var dictKey = (modId, key);
        if (!_readonlyBindings.TryGetValue(dictKey, out List<ReadonlyBinding>? list))
        {
            list = new List<ReadonlyBinding>();
            _readonlyBindings[dictKey] = list;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].ControlRef.TryGetTarget(out Control? alive) || !GodotObject.IsInstanceValid(alive))
                list.RemoveAt(i);
        }

        list.Add(new ReadonlyBinding
        {
            ControlRef = new WeakReference<Control>(control),
            ApplyReadonlyState = applyReadonlyState,
            ResolveNormalTooltip = resolveNormalTooltip
        });
        ApplyReadonlyStateToControl(control, modId, key, applyReadonlyState, resolveNormalTooltip);
    }

    /// <summary>
    /// 由 <see cref="ModConfigApi.SetEntryRowVisible"/> 调用：显示或隐藏已登记的整行（含描述与分隔线）。
    /// </summary>
    internal static void ApplyEntryRowVisibility(string modId, string key, bool visible)
    {
        if (!_entryRowHosts.TryGetValue((modId, key), out List<WeakReference<Control>>? list))
            return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out Control? row) || !GodotObject.IsInstanceValid(row))
            {
                list.RemoveAt(i);
                continue;
            }

            row.Visible = visible;
        }
    }

    internal static void ApplyEntryReadonly(string modId, string key, bool isReadonly, string? readonlyReason)
    {
        var dictKey = (modId, key);
        if (isReadonly)
        {
            _entryReadonlyStates[dictKey] = new EntryReadonlyState
            {
                IsReadonly = true,
                Reason = readonlyReason
            };
        }
        else
        {
            _entryReadonlyStates.Remove(dictKey);
        }

        if (_entryRowHosts.TryGetValue(dictKey, out List<WeakReference<Control>>? rowList))
        {
            for (int i = rowList.Count - 1; i >= 0; i--)
            {
                if (!rowList[i].TryGetTarget(out Control? row) || !GodotObject.IsInstanceValid(row))
                {
                    rowList.RemoveAt(i);
                    continue;
                }

                ApplyReadonlyStateToRow(row, modId, key);
            }
        }

        if (_readonlyBindings.TryGetValue(dictKey, out List<ReadonlyBinding>? bindings))
        {
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                if (!bindings[i].ControlRef.TryGetTarget(out Control? control) || !GodotObject.IsInstanceValid(control))
                {
                    bindings.RemoveAt(i);
                    continue;
                }

                ApplyReadonlyStateToControl(control, modId, key, bindings[i].ApplyReadonlyState, bindings[i].ResolveNormalTooltip);
            }
        }
    }

    private static void ApplyReadonlyStateToRow(Control row, string modId, string key)
    {
        bool isReadonly = TryGetReadonlyState(modId, key, out EntryReadonlyState? state);
        row.Modulate = isReadonly ? ReadonlyRowModulate : Colors.White;
        row.TooltipText = isReadonly
            ? ResolveReadonlyReason(state)
            : ResolveEntryTooltip(modId, key);
        row.MouseFilter = isReadonly ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Pass;
    }

    private static void ApplyReadonlyStateToControl(
        Control control,
        string modId,
        string key,
        Action<Control, bool> applyReadonlyState,
        Func<string?> resolveNormalTooltip)
    {
        bool isReadonly = TryGetReadonlyState(modId, key, out EntryReadonlyState? state);
        applyReadonlyState(control, isReadonly);
        control.TooltipText = isReadonly
            ? ResolveReadonlyReason(state)
            : (resolveNormalTooltip() ?? string.Empty);
    }

    private static bool TryGetReadonlyState(string modId, string key, out EntryReadonlyState? state)
    {
        return _entryReadonlyStates.TryGetValue((modId, key), out state) && state.IsReadonly;
    }

    private static string ResolveReadonlyReason(EntryReadonlyState? state)
    {
        return string.IsNullOrWhiteSpace(state?.Reason)
            ? I18n.ReadonlySyncedFromHost
            : state!.Reason!;
    }

    private static string ResolveEntryTooltip(string modId, string key)
    {
        ConfigEntry? entry = ModConfigManager.TryGetConfigEntry(modId, key);
        if (entry == null)
            return string.Empty;

        string desc = ResolveDescription(entry);
        return string.IsNullOrWhiteSpace(desc) ? string.Empty : desc;
    }

    private static void HandleReadonlyRowGuiInput(Control row, string modId, string key, InputEvent inputEvent)
    {
        if (!TryGetReadonlyState(modId, key, out EntryReadonlyState? state))
            return;

        if (inputEvent is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            return;

        ShowReadonlyNotice(ResolveReadonlyReason(state));
        row.GetViewport()?.SetInputAsHandled();
    }

    private static async void ShowReadonlyNotice(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Engine.GetMainLoop() is not SceneTree tree)
            return;

        if (_readonlyNoticeLayer != null && GodotObject.IsInstanceValid(_readonlyNoticeLayer))
            _readonlyNoticeLayer.QueueFree();

        CanvasLayer layer = new CanvasLayer();
        PanelContainer panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0f,
            AnchorRight = 0.5f,
            AnchorBottom = 0f,
            OffsetLeft = -240f,
            OffsetTop = 80f,
            OffsetRight = 240f,
            OffsetBottom = 136f
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.17f, 0.2f, 0.96f),
            BorderColor = CreamGold,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14,
            ContentMarginTop = 10,
            ContentMarginRight = 14,
            ContentMarginBottom = 10
        });

        Label label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeFontSizeOverride("font_size", 18);
        ApplyGameFont(label);
        panel.AddChild(label);
        layer.AddChild(panel);
        tree.Root.AddChild(layer);
        _readonlyNoticeLayer = layer;

        SceneTreeTimer timer = tree.CreateTimer(1.8);
        await tree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        if (GodotObject.IsInstanceValid(layer))
            layer.QueueFree();
        if (ReferenceEquals(_readonlyNoticeLayer, layer))
            _readonlyNoticeLayer = null;
    }

    /// <summary>按 <see cref="ModConfigManager.TryGetConfigEntry"/> 里最新的 <c>Options</c> 重建下拉条目（不发 ItemSelected）。</summary>
    internal static void ApplyDropdownOptions(string modId, string key)
    {
        ConfigEntry? entry = ModConfigManager.TryGetConfigEntry(modId, key);
        if (entry?.Options == null)
            return;

        if (!_dropdownWidgets.TryGetValue((modId, key), out List<WeakReference<OptionButton>>? list))
            return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out OptionButton? dd) || !GodotObject.IsInstanceValid(dd))
            {
                list.RemoveAt(i);
                continue;
            }

            dd.SetBlockSignals(true);
            dd.Clear();
            for (int j = 0; j < entry.Options.Length; j++)
                dd.AddItem(ResolveDropdownOption(entry, j), j);

            dd.SetBlockSignals(false);
        }
    }

    // ─── Content Population ──────────────────────────────────────

    /// <summary>渲染本 fork 的 <see cref="ModConfigManager.Registrations"/>（凡调用本程序集 Register 的模组均在此页，按 modId 分节）。</summary>
    private static void PopulateInto(VBoxContainer contentContainer)
    {
        if (contentContainer == null) return;

        var registrations = ModConfigManager.Registrations;
        if (registrations.Count == 0)
        {
            AddCenteredLabel(contentContainer, I18n.NoConfigs);
            return;
        }

        // Wrap all content in a ScrollContainer so it scrolls when many mods register
        var scroll = new ScrollContainer();
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.FollowFocus = true;
        contentContainer.AddChild(scroll);

        var target = new VBoxContainer();
        target.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(target);

        bool first = true;
        foreach (var (modId, reg) in registrations)
        {
            // ── Mod separator (thicker, between mods) ──
            if (!first)
                AddModSeparator(target);
            first = false;

            bool isCollapsed = ModConfigManager.CollapsedMods.Contains(modId);

            // ── Mod header row (▼/▶ Name ─── Reset) with background ──
            string localizedName = reg.GetLocalizedName();
            var (headerPanel, headerLabel) = AddModHeaderWithReset(target, modId, localizedName, isCollapsed);

            // ── Entries container (collapsible) ──
            var entriesBox = new VBoxContainer { Name = $"Entries_{modId}" };
            target.AddChild(entriesBox);

            // Wire up collapse toggle with direct reference (no node lookup)
            var capturedEntriesBox = entriesBox;
            var capturedLabel = headerLabel;
            var capturedReg = reg;
            headerPanel.GuiInput += (InputEvent @event) =>
            {
                if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    if (ModConfigManager.CollapsedMods.Contains(modId))
                        ModConfigManager.CollapsedMods.Remove(modId);
                    else
                        ModConfigManager.CollapsedMods.Add(modId);

                    bool collapsed = ModConfigManager.CollapsedMods.Contains(modId);
                    capturedEntriesBox.Visible = !collapsed;
                    ApplyCollapsibleHeaderText(capturedLabel, capturedReg.GetLocalizedName(), collapsed);
                    ModConfigManager.SaveUiState();
                }
            };

            VBoxContainer currentTopLevelSectionBody = entriesBox;
            VBoxContainer currentEntryParent = entriesBox;
            for (int i = 0; i < reg.Entries.Length; i++)
            {
                var entry = reg.Entries[i];

                switch (entry.Type)
                {
                    case ConfigType.Header:
                    {
                        bool isTopLevelSection =
                            string.IsNullOrWhiteSpace(entry.Key)
                            || entry.Key.StartsWith("section.", StringComparison.Ordinal);
                        VBoxContainer sectionParent = isTopLevelSection ? entriesBox : currentTopLevelSectionBody;
                        var sectionHost = new VBoxContainer { Name = $"Section_{modId}_{entry.Key}" };
                        sectionHost.AddThemeConstantOverride("separation", 4);
                        sectionParent.AddChild(sectionHost);
                        if (!string.IsNullOrWhiteSpace(entry.Key))
                            RegisterEntryRowHost(modId, entry.Key, sectionHost);

                        bool isCollapsible = !string.IsNullOrWhiteSpace(entry.Key);
                        bool collapsed = isCollapsible && ModConfigManager.CollapsedSections.Contains(BuildSectionStateKey(modId, entry.Key));
                        var (sectionHeaderPanel, sectionHeaderLabel) = AddSectionHeader(sectionHost, ResolveLabel(entry), collapsed, isCollapsible);
                        var sectionBody = new VBoxContainer { Name = $"SectionBody_{modId}_{entry.Key}" };
                        sectionBody.AddThemeConstantOverride("separation", 2);
                        sectionBody.Visible = !collapsed;
                        sectionHost.AddChild(sectionBody);

                        var currentSection = new SectionRenderState
                        {
                            ModId = modId,
                            HeaderEntry = entry,
                            Body = sectionBody,
                            HeaderLabel = sectionHeaderLabel,
                            IsCollapsible = isCollapsible
                        };
                        if (isTopLevelSection)
                            currentTopLevelSectionBody = sectionBody;
                        currentEntryParent = sectionBody;

                        if (isCollapsible)
                        {
                            var capturedSection = currentSection;
                            sectionHeaderPanel.GuiInput += (InputEvent @event) =>
                            {
                                if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                                    return;

                                string sectionStateKey = BuildSectionStateKey(modId, entry.Key);
                                bool nowCollapsed;
                                if (ModConfigManager.CollapsedSections.Contains(sectionStateKey))
                                {
                                    ModConfigManager.CollapsedSections.Remove(sectionStateKey);
                                    nowCollapsed = false;
                                }
                                else
                                {
                                    ModConfigManager.CollapsedSections.Add(sectionStateKey);
                                    nowCollapsed = true;
                                }

                                capturedSection.Body.Visible = !nowCollapsed;
                                ApplyCollapsibleHeaderText(capturedSection.HeaderLabel, ResolveLabel(entry), nowCollapsed);
                                ModConfigManager.SaveUiState();
                            };
                        }
                        break;
                    }
                    case ConfigType.Separator:
                        AddSeparator(currentEntryParent);
                        break;
                    case ConfigType.Toggle:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddToggle(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.Slider:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddSlider(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.Dropdown:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddDropdown(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.KeyBind:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddKeyBind(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.TextInput:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddTextInput(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.Button:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddButton(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                    case ConfigType.ColorPicker:
                    {
                        var rowHost = new VBoxContainer { Name = $"CfgRow_{modId}_{entry.Key}" };
                        currentEntryParent.AddChild(rowHost);
                        RegisterEntryRowHost(modId, entry.Key, rowHost);
                        AddColorPicker(rowHost, modId, entry);
                        AddRowSeparator(rowHost);
                        break;
                    }
                }
            }

            // Apply collapsed state
            if (isCollapsed)
                entriesBox.Visible = false;

            ModConfigApi.NotifyModSectionPopulated(modId);
        }
    }

    private static string BuildSectionStateKey(string modId, string key) => $"{modId}:{key}";

    private static void ApplyCollapsibleHeaderText(Label label, string text, bool isCollapsed)
    {
        label.Text = $"{(isCollapsed ? "\u25b6" : "\u25bc")}  {text}";
    }

    // ─── Label Resolution ────────────────────────────────────────

    private static string ResolveLabel(ConfigEntry entry)
    {
        if (entry.Labels is { Count: > 0 })
        {
            var resolved = ResolveLangDict(entry.Labels);
            if (resolved != null) return resolved;
        }
        if (!string.IsNullOrEmpty(entry.LabelKey))
            return I18n.Get(entry.LabelKey, entry.Label);
        return entry.Label;
    }

    private static string ResolveDescription(ConfigEntry entry)
    {
        if (entry.Descriptions is { Count: > 0 })
        {
            var resolved = ResolveLangDict(entry.Descriptions);
            if (resolved != null) return resolved;
        }
        if (!string.IsNullOrEmpty(entry.DescriptionKey))
            return I18n.Get(entry.DescriptionKey, entry.Description);
        return entry.Description;
    }

    private static string? ResolveLangDict(Dictionary<string, string> dict)
    {
        string lang = I18n.CurrentLang;
        if (dict.TryGetValue(lang, out var exact))
            return exact;
        foreach (var (key, value) in dict)
            if (lang.StartsWith(key) || key.StartsWith(lang))
                return value;
        return null;
    }

    private static string ResolveDropdownOption(ConfigEntry entry, int index)
    {
        if (entry.OptionsKeys != null && index < entry.OptionsKeys.Length && entry.Options != null && index < entry.Options.Length)
            return I18n.Get(entry.OptionsKeys[index], entry.Options[index]);
        if (entry.Options != null && index < entry.Options.Length)
            return entry.Options[index];
        return "";
    }

    private static string ResolveButtonText(ConfigEntry entry)
    {
        if (entry.ButtonTexts is { Count: > 0 })
        {
            var resolved = ResolveLangDict(entry.ButtonTexts);
            if (resolved != null) return resolved;
        }
        return !string.IsNullOrEmpty(entry.ButtonText) ? entry.ButtonText : ResolveLabel(entry);
    }

    // ─── Description Helper ──────────────────────────────────────

    private static void AddDescriptionIfPresent(VBoxContainer parent, ConfigEntry entry)
    {
        var desc = ResolveDescription(entry);
        if (string.IsNullOrEmpty(desc)) return;

        var label = new Label
        {
            Text = $"      {desc}",
            CustomMinimumSize = new Vector2(0, 20),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeColorOverride("font_color", DimText);
        label.AddThemeFontSizeOverride("font_size", 15);
        ApplyGameFont(label);
        parent.AddChild(label);
    }

    // ─── Tooltip Helper ──────────────────────────────────────────

    private static void ApplyTooltip(Control control, ConfigEntry entry)
    {
        var desc = ResolveDescription(entry);
        if (!string.IsNullOrEmpty(desc))
            control.TooltipText = desc;
    }

    // ─── Separator Helpers ───────────────────────────────────────

    /// <summary>Thicker separator between mods with more spacing.</summary>
    private static void AddModSeparator(VBoxContainer parent)
    {
        parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 4) };
        sep.AddThemeConstantOverride("separation", 4);
        var sepStyle = new StyleBoxFlat
        {
            BgColor = new Color(ModHeaderBg, 0.7f),
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        parent.AddChild(sep);

        parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
    }

    /// <summary>Thin row separator between config items, matching game palette.</summary>
    private static void AddRowSeparator(VBoxContainer parent)
    {
        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 1) };
        sep.AddThemeConstantOverride("separation", 1);
        var sepStyle = new StyleBoxFlat
        {
            BgColor = RowSeparatorColor,
            ContentMarginTop = 0,
            ContentMarginBottom = 0,
        };
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        parent.AddChild(sep);
    }

    // ─── UI Factory Methods ──────────────────────────────────────

    /// <summary>Returns (bgPanel, label) so the caller can wire up collapse toggle.</summary>
    private static (PanelContainer, Label) AddModHeaderWithReset(VBoxContainer parent, string modId, string text, bool isCollapsed)
    {
        // Background panel for mod header — MouseFilter.Stop so it receives clicks
        var bgPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        bgPanel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var bgStyle = new StyleBoxFlat
        {
            BgColor = ModHeaderBg,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        bgPanel.AddThemeStyleboxOverride("panel", bgStyle);

        var hbox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 40),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        string arrow = isCollapsed ? "\u25b6" : "\u25bc";
        var label = new Label
        {
            Text = $"{arrow}  {text}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", CreamGold);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var resetBtn = new Button
        {
            Text = I18n.ResetDefaults,
            CustomMinimumSize = new Vector2(0, 28),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        resetBtn.AddThemeColorOverride("font_color", ResetColor);
        resetBtn.AddThemeFontSizeOverride("font_size", 14);
        resetBtn.Pressed += () =>
        {
            if (ModConfigManager.ResetToDefaults(modId))
                RefreshUI();
        };

        hbox.AddChild(label);
        hbox.AddChild(resetBtn);
        bgPanel.AddChild(hbox);
        parent.AddChild(bgPanel);
        return (bgPanel, label);
    }

    private static (PanelContainer, Label) AddSectionHeader(VBoxContainer parent, string text, bool isCollapsed, bool isCollapsible)
    {
        var bgPanel = new PanelContainer
        {
            MouseFilter = isCollapsible ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore,
            MouseDefaultCursorShape = isCollapsible ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow,
        };
        var bgStyle = new StyleBoxFlat
        {
            BgColor = SectionHeaderBg,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        bgPanel.AddThemeStyleboxOverride("panel", bgStyle);

        var label = new Label
        {
            Text = isCollapsible ? string.Empty : text,
            HorizontalAlignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 34),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", CreamGold);
        label.AddThemeFontSizeOverride("font_size", 18);
        ApplyGameFont(label);
        if (isCollapsible)
            ApplyCollapsibleHeaderText(label, text, isCollapsed);

        bgPanel.AddChild(label);
        parent.AddChild(bgPanel);
        return (bgPanel, label);
    }

    private static void AddCenteredLabel(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 50),
        };
        label.AddThemeColorOverride("font_color", DimText);
        label.AddThemeFontSizeOverride("font_size", 16);
        ApplyGameFont(label);
        parent.AddChild(label);
    }

    private static void AddSeparator(VBoxContainer parent)
    {
        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 8) };
        sep.AddThemeConstantOverride("separation", 6);
        parent.AddChild(sep);
    }

    // ─── Toggle ──────────────────────────────────────────────────

    private static void AddToggle(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var toggle = new CheckButton
        {
            ButtonPressed = ModConfigManager.GetValue<bool>(modId, entry.Key),
        };
        var guard = new UiUpdateGuard();
        var toggleRef = new WeakReference<CheckButton>(toggle);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(toggleRef, out var liveToggle))
                return false;
            if (!TryConvertValue(value, out bool pressed))
                return true;
            if (liveToggle.ButtonPressed == pressed)
                return true;

            guard.Suppress = true;
            liveToggle.ButtonPressed = pressed;
            guard.Suppress = false;
            return true;
        });

        toggle.Toggled += pressed =>
        {
            if (guard.Suppress)
                return;
            ModConfigManager.SetValue(modId, entry.Key, pressed);
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            toggle,
            static (control, isReadonly) =>
            {
                if (control is BaseButton button)
                    button.Disabled = isReadonly;
            },
            static () => string.Empty);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(toggle);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── Slider (with Format + debounced save) ───────────────────

    private static void AddSlider(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 15);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var slider = new HSlider
        {
            MinValue = entry.Min,
            MaxValue = entry.Max,
            Step = entry.Step,
            CustomMinimumSize = new Vector2(200, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        slider.Value = ModConfigManager.GetValue<float>(modId, entry.Key);

        string fmt = entry.Format ?? "F0";
        var valueLabel = new Label
        {
            CustomMinimumSize = new Vector2(70, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = slider.Value.ToString(fmt),
        };
        valueLabel.AddThemeColorOverride("font_color", CreamGold);
        valueLabel.AddThemeFontSizeOverride("font_size", 18);

        var guard = new UiUpdateGuard();
        var sliderRef = new WeakReference<HSlider>(slider);
        var valueLabelRef = new WeakReference<Label>(valueLabel);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(sliderRef, out var liveSlider))
                return false;
            if (!TryConvertValue(value, out float numericValue))
                return true;

            if (Math.Abs(liveSlider.Value - numericValue) > 0.0001d)
            {
                guard.Suppress = true;
                liveSlider.Value = numericValue;
                guard.Suppress = false;
            }

            if (TryGetLiveTarget(valueLabelRef, out var liveValueLabel))
                liveValueLabel.Text = numericValue.ToString(fmt);

            return true;
        });

        slider.ValueChanged += value =>
        {
            valueLabel.Text = ((float)value).ToString(fmt);
            if (guard.Suppress)
                return;
            ModConfigManager.SetValue(modId, entry.Key, (float)value);
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            slider,
            static (control, isReadonly) =>
            {
                control.MouseFilter = isReadonly ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
                control.FocusMode = isReadonly ? Control.FocusModeEnum.None : Control.FocusModeEnum.All;
            },
            static () => string.Empty);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(slider);
        hbox.AddChild(valueLabel);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── Dropdown (with i18n OptionsKeys) ────────────────────────

    private static void AddDropdown(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var dropdown = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };

        var currentValue = ModConfigManager.GetValue<string>(modId, entry.Key);
        if (entry.Options != null)
        {
            for (int i = 0; i < entry.Options.Length; i++)
            {
                dropdown.AddItem(ResolveDropdownOption(entry, i), i);
                if (entry.Options[i] == currentValue)
                    dropdown.Selected = i;
            }
        }

        TrackDropdown(modId, entry.Key, dropdown);

        var guard = new UiUpdateGuard();
        var dropdownRef = new WeakReference<OptionButton>(dropdown);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(dropdownRef, out var liveDropdown))
                return false;
            if (!TryConvertValue(value, out string selectedValue) || entry.Options == null)
                return true;

            int selectedIndex = Array.IndexOf(entry.Options, selectedValue);
            if (selectedIndex < 0 || liveDropdown.Selected == selectedIndex)
                return true;

            guard.Suppress = true;
            liveDropdown.Selected = selectedIndex;
            guard.Suppress = false;
            return true;
        });

        dropdown.ItemSelected += index =>
        {
            if (guard.Suppress)
                return;
            if (entry.Options != null && index < entry.Options.Length)
                ModConfigManager.SetValue(modId, entry.Key, entry.Options[index]);
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            dropdown,
            static (control, isReadonly) =>
            {
                if (control is OptionButton optionButton)
                    optionButton.Disabled = isReadonly;
            },
            static () => string.Empty);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(dropdown);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── KeyBind ─────────────────────────────────────────────────

    private static void AddKeyBind(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        long currentKey = ModConfigManager.GetValue<long>(modId, entry.Key);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(140, 32),
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = I18n.KeyBindTooltip,
        };
        btn.AddThemeFontSizeOverride("font_size", 17);
        ApplyKeyBindButtonState(btn, currentKey);

        var buttonRef = new WeakReference<Button>(btn);
        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(buttonRef, out var liveButton))
                return false;
            if (!TryConvertValue(value, out long keyCode))
                return true;
            if (_activeKeyBindButton != null &&
                GodotObject.IsInstanceValid(_activeKeyBindButton) &&
                ReferenceEquals(_activeKeyBindButton, liveButton) &&
                _activeKeyBindModId == modId &&
                _activeKeyBindEntry?.Key == entry.Key)
            {
                return true;
            }

            ApplyKeyBindButtonState(liveButton, keyCode);
            return true;
        });

        btn.Pressed += () =>
        {
            if (_activeKeyBindButton != null && GodotObject.IsInstanceValid(_activeKeyBindButton))
            {
                // Cancel previous listening
                long prevKey = ModConfigManager.GetValue<long>(_activeKeyBindModId, _activeKeyBindEntry!.Key);
                ApplyKeyBindButtonState(_activeKeyBindButton, prevKey);
                CancelKeyCapture();
            }

            _activeKeyBindButton = btn;
            _activeKeyBindModId = modId;
            _activeKeyBindEntry = entry;
            btn.Text = I18n.PressAnyKey;
            btn.AddThemeColorOverride("font_color", KeyBindListening);

            StartKeyCapture(modId, entry, btn);
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            btn,
            static (control, isReadonly) =>
            {
                if (control is Button button)
                    button.Disabled = isReadonly;
            },
            static () => I18n.KeyBindTooltip);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(btn);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    private static string KeyToDisplayString(long keyCode)
    {
        if (keyCode == 0) return I18n.KeyUnbound;
        var key = (Key)keyCode;
        return OS.GetKeycodeString(key);
    }

    private static void ApplyKeyBindButtonState(Button button, long keyCode)
    {
        button.Text = KeyToDisplayString(keyCode);
        button.TooltipText = I18n.KeyBindTooltip;

        if (keyCode == 0)
            button.AddThemeColorOverride("font_color", DimText);
        else
            button.RemoveThemeColorOverride("font_color");
    }

    // Temporary Node used to capture _UnhandledKeyInput
    private static KeyCaptureNode? _captureNode;

    private static void StartKeyCapture(string modId, ConfigEntry entry, Button btn)
    {
        CancelKeyCapture();
        _captureNode = new KeyCaptureNode();
        _captureNode.OnKeyCaptured = keyCode =>
        {
            ModConfigManager.SetValue(modId, entry.Key, keyCode);
            ApplyKeyBindButtonState(btn, keyCode);
            _activeKeyBindButton = null;
            _activeKeyBindModId = "";
            _activeKeyBindEntry = null;
            CancelKeyCapture();
        };
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_captureNode);
    }

    private static void CancelKeyCapture()
    {
        if (_captureNode != null && GodotObject.IsInstanceValid(_captureNode))
        {
            _captureNode.QueueFree();
            _captureNode = null;
        }
    }

    // ─── TextInput ───────────────────────────────────────────────

    private static void AddTextInput(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var lineEdit = new LineEdit
        {
            Text = ModConfigManager.GetValue<string>(modId, entry.Key),
            MaxLength = entry.MaxLength,
            PlaceholderText = entry.Placeholder,
            CustomMinimumSize = new Vector2(200, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        lineEdit.AddThemeFontSizeOverride("font_size", 17);

        var lineEditRef = new WeakReference<LineEdit>(lineEdit);
        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(lineEditRef, out var liveLineEdit))
                return false;
            if (liveLineEdit.HasFocus())
                return true;
            if (!TryConvertValue(value, out string textValue))
                return true;
            if (liveLineEdit.Text == textValue)
                return true;

            liveLineEdit.Text = textValue;
            return true;
        });

        // Validation visual feedback
        StyleBoxFlat? validStyle = null;
        StyleBoxFlat? invalidStyle = null;
        if (entry.Validator != null)
        {
            validStyle = new StyleBoxFlat { BgColor = new Color(0.15f, 0.15f, 0.15f), BorderColor = new Color(0.3f, 0.3f, 0.3f), BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 };
            invalidStyle = new StyleBoxFlat { BgColor = new Color(0.2f, 0.1f, 0.1f), BorderColor = new Color(0.8f, 0.3f, 0.3f), BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2, CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 };
        }

        void ApplyValidation(LineEdit le, string text)
        {
            if (entry.Validator == null) return;

            try
            {
                bool isValid = entry.Validator(text);
                le.AddThemeStyleboxOverride("normal", isValid ? validStyle! : invalidStyle!);
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Validator error [{modId}.{entry.Key}]: {e}");
            }
        }

        // Save on focus lost or Enter pressed
        lineEdit.TextSubmitted += text =>
        {
            ApplyValidation(lineEdit, text);
            ModConfigManager.SetValue(modId, entry.Key, text);
        };
        lineEdit.FocusExited += () =>
        {
            ApplyValidation(lineEdit, lineEdit.Text);
            ModConfigManager.SetValue(modId, entry.Key, lineEdit.Text);
        };
        lineEdit.TextChanged += text => ApplyValidation(lineEdit, text);
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            lineEdit,
            static (control, isReadonly) =>
            {
                if (control is LineEdit editable)
                {
                    editable.Editable = !isReadonly;
                    editable.FocusMode = isReadonly ? Control.FocusModeEnum.None : Control.FocusModeEnum.All;
                }
            },
            static () => string.Empty);

        // Apply initial validation state
        ApplyValidation(lineEdit, lineEdit.Text);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(lineEdit);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── Button (action trigger, no persisted value) ─────────────

    private static void AddButton(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var btn = new Button
        {
            Text = ResolveButtonText(entry),
            CustomMinimumSize = new Vector2(140, 32),
            FocusMode = Control.FocusModeEnum.All,
        };
        btn.AddThemeFontSizeOverride("font_size", 17);

        btn.Pressed += () =>
        {
            try { entry.OnChanged?.Invoke(true); }
            catch (Exception e) { MainFile.Log.Error($"Button callback error [{modId}.{entry.Key}]: {e}"); }
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            btn,
            static (control, isReadonly) =>
            {
                if (control is Button button)
                    button.Disabled = isReadonly;
            },
            static () => string.Empty);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(btn);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── ColorPicker ─────────────────────────────────────────────

    private static void AddColorPicker(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);
        ApplyGameFont(label);

        var currentHex = ModConfigManager.GetValue<string>(modId, entry.Key);
        var currentColor = ParseHexColor(currentHex);

        var picker = new ColorPickerButton
        {
            Color = currentColor,
            CustomMinimumSize = new Vector2(60, 32),
            FocusMode = Control.FocusModeEnum.All,
            EditAlpha = false,
            TooltipText = I18n.ColorPickerTooltip,
        };

        var hexLabel = new Label
        {
            Text = currentColor.ToHtml(false).ToUpperInvariant(),
            CustomMinimumSize = new Vector2(80, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        hexLabel.AddThemeColorOverride("font_color", CreamGold);
        hexLabel.AddThemeFontSizeOverride("font_size", 16);
        ApplyGameFont(hexLabel);

        var guard = new UiUpdateGuard();
        var pickerRef = new WeakReference<ColorPickerButton>(picker);
        var hexLabelRef = new WeakReference<Label>(hexLabel);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(pickerRef, out var livePicker))
                return false;
            if (!TryConvertValue(value, out string hexValue))
                return true;

            var color = ParseHexColor(hexValue);
            guard.Suppress = true;
            livePicker.Color = color;
            guard.Suppress = false;

            if (TryGetLiveTarget(hexLabelRef, out var liveHexLabel))
                liveHexLabel.Text = color.ToHtml(false).ToUpperInvariant();

            return true;
        });

        picker.ColorChanged += color =>
        {
            hexLabel.Text = color.ToHtml(false).ToUpperInvariant();
            if (guard.Suppress) return;
            ModConfigManager.SetValue(modId, entry.Key, "#" + color.ToHtml(false).ToUpperInvariant());
        };
        RegisterReadonlyBinding(
            modId,
            entry.Key,
            picker,
            static (control, isReadonly) =>
            {
                if (control is ColorPickerButton pickerButton)
                    pickerButton.Disabled = isReadonly;
            },
            static () => I18n.ColorPickerTooltip);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(picker);
        hbox.AddChild(hexLabel);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    private static Color ParseHexColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.White;
        try
        {
            hex = hex.TrimStart('#');
            return Color.FromHtml(hex);
        }
        catch { return Colors.White; }
    }

    private static bool TryGetLiveTarget<T>(WeakReference<T> reference, out T target) where T : GodotObject
    {
        if (reference.TryGetTarget(out var liveTarget) && GodotObject.IsInstanceValid(liveTarget))
        {
            target = liveTarget;
            return true;
        }

        target = null!;
        return false;
    }

    private static bool TryConvertValue<T>(object value, out T converted)
    {
        try
        {
            if (value is T typedValue)
            {
                converted = typedValue;
                return true;
            }

            object? changedValue = Convert.ChangeType(value, typeof(T));
            if (changedValue is T finalValue)
            {
                converted = finalValue;
                return true;
            }
        }
        catch
        {
        }

        converted = default!;
        return false;
    }
}
