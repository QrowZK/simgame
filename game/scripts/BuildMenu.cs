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

        _items.ItemSelected += OnItemSelected;
        _recipes.ItemSelected += OnRecipeSelected;
        GetNode<Button>("Margin/Rows/Buttons/Close").Pressed += () => Closed?.Invoke();

        Visible = false;
    }

    public void Bind(BuildCatalogue catalogue, Inventory bag, ItemDatabase names)
    {
        _catalogue = catalogue;
        _bag = bag;
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

        foreach (var buildable in _catalogue.Offerable
                     .Where(b => _bag.Count(b.Item) > 0)
                     .OrderBy(b => b.Tier)
                     .ThenBy(b => b.Name))
        {
            _shown.Add(buildable);
            _items.AddItem($"{buildable.DisplayName}  x{_bag.Count(buildable.Item)}");
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
        var recipes = _catalogue.RecipesFor(buildable);
        if (recipes.Count == 0) return null;

        _recipes.Select(0);
        return recipes[0];
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

        foreach (var recipe in _catalogue.RecipesFor(buildable))
        {
            _shownRecipes.Add(recipe);
            _recipes.AddItem(Describe(recipe));
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
