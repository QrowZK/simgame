namespace Sim;

public readonly record struct ItemId(int Value)
{
    public static implicit operator int(ItemId id) => id.Value;
}

public sealed class ItemDatabase
{
    private readonly Dictionary<string, ItemId> _idsByName = new();
    private readonly List<string> _namesById = new();

    public ItemId Register(string name)
    {
        if (_idsByName.TryGetValue(name, out var existing))
            return existing;

        var id = new ItemId(_namesById.Count);
        _namesById.Add(name);
        _idsByName[name] = id;
        return id;
    }

    public ItemId GetId(string name) => _idsByName[name];

    public bool TryGetId(string name, out ItemId id) => _idsByName.TryGetValue(name, out id);

    public string GetName(ItemId id) => _namesById[id.Value];

    public int Count => _namesById.Count;
}

public readonly struct ItemStack
{
    public readonly ItemId Item;
    public readonly int Count;

    public ItemStack(ItemId item, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Item = item;
        Count = count;
    }
}
