using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;

namespace Game;

/// What you can build right now, and what it should make.
///
/// Two lists rather than one, because they answer different questions. The left
/// is "what am I carrying that I can put down" -- built from the inventory, so
/// it never offers something the player does not have. The right only appears
/// for machines, and is "what should this one make": a machine's recipe is
/// chosen before it is placed, since changing it afterwards would mean evicting
/// whatever is already inside.
public sealed partial class BuildMenu : PanelContainer
{
    /// Raised when the player picks something. The ghost follows this.
    public event System.Action<Buildable, Recipe?>? Selected;

    public event System.Action? Closed;

    private Label _title = null!;
    private ItemList _items = null!;
    private Label _recipeTitle = null!;
    private ItemList _recipes = null!;
    private Label _hint = null!;

    private BuildCatalogue _catalogue = null!;
    private World _world = null!;
    private Inventory _bag = null!;
    private ItemDatabase _names = null!;

    private readonly List<Buildable> _shown = new();
    private readonly List<Recipe> _shownRecipes = new();
    private Buildable? _picked;

    public bool IsShowing => Visible;

    /// How many kinds the menu is currently offering. Reported by the headless
    /// smoke run, where there is no screenshot to look at.
    public int OfferedCount => _shown.Count;

    public override void _Ready()
    {
        _title = GetNode<Label>("Margin/Rows/Title");
        _items = GetNode<ItemList>("Margin/Rows/Columns/Items");
        _recipeTitle = GetNode<Label>("Margin/Rows/Columns/RecipeSide/RecipeTitle");
        _recipes = GetNode<ItemList>("Margin/Rows/Columns/RecipeSide/Recipes");
        _hint = GetNode<Label>("Margin/Rows/Hint");

        // Pin the icon to about the height of a line of text. Left to itself
        // an ItemList grows its rows to the icon's natural size, which cost two
        // rows off the bottom of the list and clipped the count off the end of
        // every name -- an icon that hides "x3" has taken more than it gave.
        // The column is widened to pay for the space the icon takes, rather
        // than taking it out of the name.
        _items.FixedIconSize = new Vector2I(18, 18);
        _items.CustomMinimumSize = new Vector2(268f, _items.CustomMinimumSize.Y);
        _recipes.FixedIconSize = new Vector2I(18, 18);

        _items.ItemSelected += OnItemSelected;
        _recipes.ItemSelected += OnRecipeSelected;
        GetNode<Button>("Margin/Rows/Buttons/Close").Pressed += () => Closed?.Invoke();

        Visible = false;
    }

    /// The menu needs the world, not just the bag, because what may be built
    /// is now a question of research as well as of inventory (ADR 0023).
    public void Bind(BuildCatalogue catalogue, World world, ItemDatabase names)
    {
        _catalogue = catalogue;
        _world = world;
        _bag = world.PlayerInventory;
        _names = names;
    }

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        Visible = false;
        _picked = null;
    }

    /// Rebuilds the carried list. Called on open and after every build, so the
    /// count beside each entry is what the player actually still has -- a menu
    /// that still offers the last furnace after you placed it is worse than no
    /// menu.
    public void Refresh()
    {
        var keepItem = _picked?.Item;

        _items.Clear();
        _shown.Clear();

        foreach (var buildable in _catalogue.OfferableWith(_world.Research)
                     .Where(b => _bag.Count(b.Item) > 0)
                     .OrderBy(b => b.Tier)
                     .ThenBy(b => b.Name))
        {
            _shown.Add(buildable);

            // The icon is the point of this list. Every entry here is a name
            // and a number, and at eight tiers of near-identical wording --
            // "VLT Assembler", "ARC Assembler" -- the name is the slowest way
            // to tell two rows apart. The shape says what kind of thing it is
            // and the pips say which tier, before the text is read at all.
            _items.AddItem($"{buildable.DisplayName}  x{_bag.Count(buildable.Item)}",
                           ItemIcons.For(_names.GetName(buildable.Item)));

            // What the row is, without having to place one to find out. There
            // was no tooltip anywhere in this game: every question a player had
            // about a row could only be answered by trying it.
            _items.SetItemTooltip(_items.ItemCount - 1, Explain(buildable));
        }

        if (_shown.Count == 0)
        {
            _title.Text = "Build  --  nothing to build";
            _hint.Text = "Craft a machine first. Everything you can place appears here.";
            ShowRecipes(null);
            return;
        }

        _title.Text = $"Build  --  {_shown.Count} kinds carried";
        _hint.Text = "Click a tile to place.   Esc or right-click to stop building.";

        // Keep the player's selection across a refresh where possible: placing
        // one of five poles should not throw you back to the top of the list.
        var index = keepItem is null ? 0 : _shown.FindIndex(b => b.Item.Equals(keepItem.Value));
        if (index < 0) index = 0;

        _items.Select(index);
        OnItemSelected(index);
    }

    private void OnItemSelected(long index)
    {
        if (index < 0 || index >= _shown.Count) return;

        _picked = _shown[(int)index];
        ShowRecipes(_picked);

        // A non-machine is ready the moment it is picked. A machine is not
        // ready until a recipe is chosen, so it announces itself with none and
        // the build path refuses it until one is.
        Selected?.Invoke(_picked, _picked.Kind == BuildKind.Machine ? FirstRecipe(_picked) : null);
    }

    private Recipe? FirstRecipe(Buildable buildable)
    {
        var recipes = _catalogue.RecipesFor(buildable, _world.Research);
        if (recipes.Count == 0) return null;

        _recipes.Select(0);
        return recipes[0];
    }


    /// How many rows carry an explanation, for the headless report. A tooltip
    /// that is silently empty is indistinguishable from the nothing that was
    /// here before, and nobody hovers in a screenshot.
    public int Explained
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _items.ItemCount; i++)
                if (_items.GetItemTooltip(i).Length > 0) n++;
            return n;
        }
    }

    /// One row's worth of "what is this", for the tooltip.
    ///
    /// Footprint first, because footprint is the only throughput dial in this
    /// game -- a 3x3 runs nine batches a cycle and costs nine times the floor
    /// -- and a player who does not know that reads every machine as the same
    /// machine at a different price.
    private string Explain(Buildable buildable)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            $"{buildable.DisplayName}  --  {buildable.Tier} tier",
            $"{buildable.Size}x{buildable.Size} tiles, {buildable.Size * buildable.Size} " +
            (buildable.Size == 1 ? "batch per cycle" : "batches per cycle"),
        };

        if (buildable.TierPower > 0)
            lines.Add($"Draws {buildable.TierPower} power while it runs.");

        var recipes = _catalogue.RecipesFor(buildable, _world.Research).Count;
        if (recipes > 0)
            lines.Add(recipes == 1
                ? "Makes one thing. Click it to see what."
                : $"Makes any of {recipes} things, one at a time. Click it to choose.");

        lines.Add($"You have {_bag.Count(buildable.Item)}.");
        return string.Join("\n", lines);
    }

    private void ShowRecipes(Buildable? buildable)
    {
        _recipes.Clear();
        _shownRecipes.Clear();

        if (buildable is null || buildable.Kind != BuildKind.Machine)
        {
            _recipeTitle.Text = buildable is null ? "" : $"{buildable.DisplayName} -- no recipe needed";
            return;
        }

        _recipeTitle.Text = $"{buildable.DisplayName} makes:";

        foreach (var recipe in _catalogue.RecipesFor(buildable, _world.Research))
        {
            _shownRecipes.Add(recipe);

            // The recipe list is read as "which one makes the thing I want",
            // so it takes the icon of what comes out, not of the machine.
            _recipes.AddItem(Describe(recipe),
                             recipe.Outputs.Count > 0
                                 ? ItemIcons.For(_names.GetName(recipe.Outputs[0].Item))
                                 : null);
        }
    }

    private void OnRecipeSelected(long index)
    {
        if (_picked is null || index < 0 || index >= _shownRecipes.Count) return;
        Selected?.Invoke(_picked, _shownRecipes[(int)index]);
    }

    /// A recipe as the player judges it: what comes out, from what. The output
    /// leads, because "which one makes plates" is the question being asked.
    private string Describe(Recipe recipe)
    {
        var outputs = string.Join(" + ", recipe.Outputs.Select(o => $"{o.Count} {_names.GetName(o.Item)}"));
        var inputs = recipe.Inputs.Count == 0
            ? "nothing"
            : string.Join(" + ", recipe.Inputs.Select(i => $"{i.Count} {_names.GetName(i.Item)}"));

        return $"{outputs}  <-  {inputs}";
    }
}
