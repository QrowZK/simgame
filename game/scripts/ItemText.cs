using System.Collections.Generic;
using System.Linq;

namespace Game;

/// The name a player reads for an item.
///
/// Items have two names. `ItemDatabase` is keyed by runtime id and returns the
/// *data* id -- "copper_fine_wire", "man_manual_crafting" -- which is what the
/// recipe graph is filed under and what every test and every save refers to.
/// The player-facing name lives in the item table as `Name`.
///
/// This exists because the difference has been got wrong twice: the machine
/// panel told players they were carrying "24 stone_deposit, 1 man_uplink", and
/// the build menu offered them "1 copper_plate &lt;- 1 copper_ingot". Both were
/// found by looking at a screenshot rather than by any test, because a raw id
/// is a perfectly valid string and nothing downstream can tell it is wrong.
/// One lookup, in one place, so the third occurrence has nowhere to come from.
public static class ItemText
{
    private static readonly Dictionary<string, string> Names =
        Sim.Data.Catalogue.Instance.Data.Items.ToDictionary(i => i.Id, i => i.Name);

    /// The readable name for a data id, or the id itself when the item is not
    /// in the table -- which should not happen, and reads as obviously wrong
    /// rather than as a blank if it ever does.
    public static string Of(string dataId) => Names.GetValueOrDefault(dataId, dataId);

    /// The readable name for a runtime id.
    public static string Of(Sim.ItemDatabase items, Sim.ItemId item) =>
        items.Count > item.Value ? Of(items.GetName(item)) : $"#{item.Value}";
}
