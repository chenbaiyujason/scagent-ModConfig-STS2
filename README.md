# ModConfig — Universal Mod Configuration Framework for Slay the Spire 2

# ModConfig — 杀戮尖塔2 通用模组配置框架

<p align="center">
  <img src="ModConfig/mod_image.png" alt="ModConfig" width="400"/>
</p>

Adds a **"Mods"** tab to the game's Settings screen. Mod authors register config entries via a simple API — controls are rendered automatically.

在游戏设置界面注入 **「Mods」** 标签页。模组作者通过 API 注册配置项，控件自动渲染。

## Download / 下载

- **Nexus Mods**: [ModConfig on Nexus Mods](https://www.nexusmods.com/slaythespire2/mods/27)
- **Video Tutorial / 视频教程**: [Bilibili](https://www.bilibili.com/video/BV12ZcrzmEo)

## For Players / 玩家须知

Install ModConfig alongside any mod that supports it. A new **"Mods"** tab will appear in Settings where you can adjust mod options without editing config files.

安装 ModConfig 后，支持该框架的模组会在设置页出现配置选项，无需手动改文件。

**Installation / 安装：** Extract `ModConfig.dll` + `ModConfig.pck` into `<Game>/mods/ModConfig/`.

**安装方法：** 将 `ModConfig.dll` 和 `ModConfig.pck` 放入游戏目录 `mods/ModConfig/` 即可。

## Supported Controls / 支持控件

| Control | Description | 说明 |
|---------|-------------|------|
| **Toggle** | On/Off switch | 开关 |
| **Slider** | Numeric range with step | 滑条（支持步长和格式化） |
| **Dropdown** | Select from options | 下拉框 |
| **KeyBind** | Keyboard shortcut capture | 快捷键绑定（支持组合键） |
| **TextInput** | Free-form text entry (with optional validation) | 文本输入框（支持校验） |
| **Button** | Action trigger, no persisted value | 动作按钮，不存储值 |
| **ColorPicker** | Hex color picker with live preview | 颜色选择器（`#RRGGBB`） |
| **Header** | Section title (visual only) | 分组标题 |
| **Separator** | Visual divider (visual only) | 分隔线 |

## Compatibility / 兼容性

- Windows / macOS / Linux (AnyCPU)
- Bilingual: English & 简体中文 (auto-detected)
- Does not conflict with other mods

---

# For Mod Authors / 开发者接入指南

## Core Principle / 核心原则

**Zero-dependency integration** — your mod calls ModConfig via **reflection**. If ModConfig is not installed, your mod still works normally. No DLL reference needed.

**零依赖接入** — 你的模组通过**反射**调用 ModConfig。玩家没装 ModConfig 时模组照常运行，无需引用 DLL。

## Quick Start / 快速开始

### 1. Copy the template / 复制模板

Download **[`examples/ModConfigBridge.cs`](examples/ModConfigBridge.cs)** into your mod's `Scripts/` folder. This is a **complete, drop-in file** with all control types included — just replace the namespace, mod ID, and edit `BuildEntries()`.

将 **[`examples/ModConfigBridge.cs`](examples/ModConfigBridge.cs)** 复制到你模组的 `Scripts/` 目录。这是一个**完整可直接使用的文件**，包含所有控件类型的示例——只需替换命名空间、模组 ID，然后编辑 `BuildEntries()`。

The template includes working examples for: Toggle, Slider, Dropdown, KeyBind (uncommented) + TextInput, Button, ColorPicker (commented, uncomment to use).

模板内含可用示例：开关、滑条、下拉框、快捷键（已启用）+ 文本输入、按钮、颜色选择器（已注释，取消注释即可用）。

### 2. Call DeferredRegister() in Initialize() / 在 Initialize 中调用

```csharp
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace YourMod;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Godot.Node
{
    internal const string ModId = "your.mod.id";

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // One line — deferred registration is built into the template
        ModConfigBridge.DeferredRegister();
    }
}
```

> **Why deferred?** Your mod may load before ModConfig (alphabetical order). `DeferredRegister()` waits one frame so ModConfig's types are available for reflection. This logic is built into the template — just call it.
>
> **为什么要延迟？** 你的模组可能比 ModConfig 先加载（按字母顺序）。`DeferredRegister()` 会等一帧确保 ModConfig 已就绪。模板已内置此逻辑，直接调用即可。

### 3. Read/write config values at runtime / 运行时读写配置

```csharp
// Read a saved value (returns fallback if ModConfig not installed)
bool enabled = ModConfigBridge.GetValue("featureEnabled", true);
float speed  = ModConfigBridge.GetValue("speedMultiplier", 1.0f);

// Write back when your mod changes a setting outside ModConfig's UI
// (e.g. via hotkey or your own menu) — ensures the value persists
ModConfigBridge.SetValue("featureEnabled", false);
```

## Bilingual Labels / 多语言标签

The template uses a helper `L(en, zhs)` for bilingual labels — just pass English and Chinese:

模板使用 `L(en, zhs)` 辅助函数实现双语标签——只需传入英文和中文：

```csharp
list.Add(Entry(cfg =>
{
    Set(cfg, "Key", "myToggle");
    Set(cfg, "Label", "My Feature");
    Set(cfg, "Labels", L("My Feature", "我的功能"));           // bilingual label
    Set(cfg, "Type", EnumVal("Toggle"));
    Set(cfg, "DefaultValue", (object)true);
    Set(cfg, "Description", "Enable this feature");
    Set(cfg, "Descriptions", L("Enable this feature", "启用此功能"));  // bilingual desc
    Set(cfg, "OnChanged", new Action<object>(v => { /* ... */ }));
}));
```

## ConfigEntry Properties / 配置项属性

| Property | Type | Used By | Description |
|----------|------|---------|-------------|
| `Key` | string | All (except Header/Separator) | Unique key for persistence |
| `Label` | string | All | Display text (English fallback) |
| `Labels` | Dict | All | Per-language labels `{"en":"...", "zhs":"..."}` |
| `Type` | ConfigType | All | Control type to render |
| `DefaultValue` | object | Toggle/Slider/Dropdown/KeyBind/TextInput/ColorPicker | Default value (`"#RRGGBB"` for ColorPicker) |
| `Min` | float | Slider | Minimum value |
| `Max` | float | Slider | Maximum value (default: 100) |
| `Step` | float | Slider | Step increment (default: 1) |
| `Format` | string | Slider | Display format: `"F0"`, `"F2"`, `"P0"` |
| `Options` | string[] | Dropdown | Selectable options |
| `MaxLength` | int | TextInput | Max characters (default: 64) |
| `Placeholder` | string | TextInput | Placeholder text |
| `ButtonText` | string | Button | Text displayed on the button |
| `ButtonTexts` | Dict | Button | Per-language button texts |
| `Validator` | Func\<object, bool\> | TextInput | Returns true if valid; red border on false |
| `OnChanged` | Action\<object\> | All data types | Callback when value changes |
| `Description` | string | All | Tooltip / description text |
| `Descriptions` | Dict | All | Per-language descriptions |

## Real-World Examples / 实际案例

These mods already integrate with ModConfig (all open-source):

这些模组已接入 ModConfig（均已开源）：

- **[Skada: Damage Meter](https://www.nexusmods.com/slaythespire2/mods/14)** — 5 settings (FontScale, Opacity, MaxBars, AutoReset, AutoSwitch)
- **[SpeedX](https://www.nexusmods.com/slaythespire2/mods/18)** — 22 settings (10 Toggle + 3 Slider + 5 KeyBind + Headers)
- **[Rewind](https://www.nexusmods.com/slaythespire2/mods/26)** — 8 settings (Toggle + Slider + Dropdown + KeyBind)
- **[QuickLink](https://www.nexusmods.com/slaythespire2/mods/30)** — 8 settings (Toggle + Slider + Dropdown)

## Common Pitfalls / 常见问题

| Problem | Solution |
|---------|----------|
| Config entries don't appear | Use deferred registration (2-frame delay). ModConfig may not be loaded yet at your `Initialize()` time. |
| `OnChanged` not firing | Make sure you set the `OnChanged` property via reflection (`SetProp`). |
| Slider shows wrong format | Set `Format` property (e.g., `"F1"` for 1 decimal, `"P0"` for percentage). |
| KeyBind value type | KeyBind uses `long` (Godot keycode with modifiers). Cast from `object` to `long`. |
| Config not saved | Values auto-save to `user://ModConfig/<modId>.json`. No manual save needed. |
| ModConfig not installed | Your mod works normally — `IsAvailable` returns `false`, all `GetValue` calls return fallback. |

| 问题 | 解决方案 |
|------|---------|
| 配置项不显示 | 使用延迟注册（两帧回调）。`Initialize()` 时 ModConfig 可能还没加载完。 |
| `OnChanged` 不触发 | 确保通过反射正确设置了 `OnChanged` 属性。 |
| 滑条格式不对 | 设置 `Format` 属性（如 `"F1"` 一位小数，`"P0"` 百分比）。 |
| 快捷键值类型 | KeyBind 使用 `long`（Godot 键码含修饰键），从 `object` 转换为 `long`。 |
| 配置没保存 | 值会自动保存到 `user://ModConfig/<modId>.json`，无需手动保存。 |
| 玩家没装 ModConfig | 模组正常运行——`IsAvailable` 返回 `false`，`GetValue` 返回 fallback 值。 |

---

## Building from Source / 从源码构建

### Prerequisites / 前置条件
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download)
- [Godot 4.5.1 Mono](https://godotengine.org/)
- Slay the Spire 2 installed

### Build / 构建

1. Update `Sts2Dir` in `ModConfig.csproj` to your game install path
2. `dotnet build ModConfig.csproj`
3. DLL auto-copies to game's `mods/ModConfig/` folder

### Export PCK / 导出 PCK

```bash
Godot_v4.5.1-stable_mono_win64.exe --headless --path . --export-pack "Windows Desktop" "mods/ModConfig/ModConfig.pck"
```

---

## License / 许可

MIT License — free to use, modify, and redistribute.

## Author / 作者

**皮一下就很凡** — [Bilibili](https://space.bilibili.com/26786884) | [Nexus Mods](https://www.nexusmods.com/slaythespire2/users/56800967)
