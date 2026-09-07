using System.Collections.Generic;
using Godot;

namespace Game;

/// The icon for an item, from the generated atlas.
///
/// 617 items need 617 icons, and the two obvious implementations both fail at
/// that count: 617 files is 617 resource loads at startup and 617 texture
/// bindings in a list, and hand-drawn icons would be permanently one item
/// behind `data/spec/progression.json`. So there is exactly one image --
/// `tools/generate_icons.py` draws every icon into `game/icons/items.png` from
/// what the data already says -- and every icon here is an `AtlasTexture`
/// pointing into that one texture. One load, one binding, and an item added to
/// the spec has an icon the moment the generator is re-run. ADR 0019.
///
/// `AtlasTexture` objects are made on demand and kept, so a menu showing forty
/// buildables allocates forty small resources over the life of the process, not
/// six hundred at startup.
public static class ItemIcons
{
    private const string AtlasPath = "res://icons/items.png";
    private const string IndexPath = "res://icons/items.index.json";

    private static Texture2D? _atlas;
    private static readonly Dictionary<string, int> Slots = new();
    private static readonly Dictionary<string, AtlasTexture> Cache = new();
    private static int _cell;
    private static int _gutter;
    private static int _columns;
    private static bool _tried;

    /// How many items the atlas covers. Reported by the headless smoke run: an
    /// atlas that failed to import and one that is simply blank look the same
    /// in a screenshot, and "every item has an icon" is a number, not a look.
    public static int Known
    {
        get
        {
            Load();
            return _atlas is null ? 0 : Slots.Count;
        }
    }

    /// The icon for an item id, or null when the atlas is missing or does not
    /// know it. Null is deliberate: `ItemList.AddItem` accepts it, so a stale
    /// atlas costs an icon rather than crashing a menu, and `Known` says so.
    public static Texture2D? For(string itemId)
    {
        Load();
        if (_atlas is null) return null;

        if (Cache.TryGetValue(itemId, out var cached)) return cached;
        if (!Slots.TryGetValue(itemId, out var slot)) return null;

        var icon = new AtlasTexture
        {
            Atlas = _atlas,
            Region = new Rect2(
                (slot % _columns) * _cell + _gutter,
                (slot / _columns) * _cell + _gutter,
                _cell - _gutter * 2,
                _cell - _gutter * 2),
        };

        Cache[itemId] = icon;
        return icon;
    }

    private static void Load()
    {
        if (_tried) return;
        _tried = true;

        _atlas = ResourceLoader.Load<Texture2D>(AtlasPath);
        if (_atlas is null)
        {
            GD.PushWarning($"item icons: no atlas at {AtlasPath}; " +
                           "run tools/generate_icons.py");
            return;
        }

        using var file = FileAccess.Open(IndexPath, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            GD.PushWarning($"item icons: no index at {IndexPath}");
            _atlas = null;
            return;
        }

        var parsed = Json.ParseString(file.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary)
        {
            GD.PushWarning("item icons: index is not a JSON object");
            _atlas = null;
            return;
        }

        var index = parsed.AsGodotDictionary();
        _cell = (int)index["cell"];
        _gutter = (int)index["gutter"];
        _columns = (int)index["columns"];

        // The index is read rather than assumed. Slot order is items.json
        // order, so an atlas generated before an item was added would still
        // resolve -- to the wrong icon, silently, for every item after the
        // insertion point. Matching on id makes a stale atlas lose icons
        // instead of scrambling them.
        var ids = index["ids"].AsGodotArray();
        for (var i = 0; i < ids.Count; i++)
            Slots[ids[i].AsString()] = i;
    }
}
