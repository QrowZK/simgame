using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;

namespace Game;

/// The machine inspection panel: what is this machine doing, with what, and how
/// far along. It is also how the first hour of the game is played, before any
/// inserter exists -- you walk up to a furnace, load it, and take the plates out.
///
/// The panel owns no state. It reads a Machine each frame and calls the sim's
/// hand operations; everything it shows is a snapshot, so holding the panel open
/// can never change what the simulation does.
public sealed partial class MachinePanel : PanelContainer
{
    private Label _title = null!;
    private Label _subtitle = null!;
    private ProgressBar _progress = null!;
    private Label _status = null!;
    private Label _inputs = null!;
    private Label _outputs = null!;
    private Label _carrying = null!;
    private Label _recipeTitle = null!;
    private ItemList _recipes = null!;
    private Label _retask = null!;
    private Button _load = null!;

    /// The recipes currently listed, in list order, and the machine they belong
    /// to. Rebuilt only when the panel is pointed at a different machine: the
    /// panel refreshes every frame, and rebuilding an ItemList under the
    /// player's cursor would make it impossible to click a row.
    private readonly List<Recipe> _shownRecipes = new();
    private int _index = -1;

    /// What the last retask did, shown until the panel moves to another
    /// machine. A recipe change hands items back, and a player who is not told
    /// how many has no way to know it happened.
    private string _retaskMessage = "";

    private Machine? _machine;
    private Miner? _miner;
    private MachinePlacement _placement;
    private ItemDatabase _names = null!;
    private Inventory _bag = null!;
    private World _world = null!;
    private BuildCatalogue _buildables = null!;

    /// Where a mutating click goes. Set by `GameRoot`; when it is null this
    /// panel is a read-only preview (the art harness, a capture rig) and its
    /// action buttons do nothing rather than reaching into the sim behind the
    /// router's back.
    public PlayerActions? Actions { get; set; }

    /// The message a shared world cannot avoid yet. Loading a machine by hand
    /// and emptying one are real changes to the world and there is no command
    /// kind for either (ADR 0037 has six, and these are not among them), so
    /// they are refused with the reason rather than desyncing the session.
    private const string NoHandOpsShared =
        "Loading and emptying a machine by hand do not cross the network yet -- " +
        "there is no command for them, and doing it locally would put this world " +
        "out of step with everyone else's. Use a belt or an inserter for now.";

    public override void _Ready()
    {
        _title = GetNode<Label>("Margin/Rows/Title");
        _subtitle = GetNode<Label>("Margin/Rows/Subtitle");
        _progress = GetNode<ProgressBar>("Margin/Rows/Progress");
        _status = GetNode<Label>("Margin/Rows/Status");
        _inputs = GetNode<Label>("Margin/Rows/Inputs");
        _outputs = GetNode<Label>("Margin/Rows/Outputs");
        _carrying = GetNode<Label>("Margin/Rows/Carrying");
        _recipeTitle = GetNode<Label>("Margin/Rows/RecipeTitle");
        _recipes = GetNode<ItemList>("Margin/Rows/Recipes");
        _retask = GetNode<Label>("Margin/Rows/Retask");

        // Same icon size and idiom as the build menu's recipe list: this is the
        // same question asked at a different moment, so it should not look like
        // a different control.
        _recipes.FixedIconSize = new Vector2I(18, 18);
        _recipes.ItemSelected += OnRecipePicked;

        _load = GetNode<Button>("Margin/Rows/Buttons/Load");

        // Buttons that say what they will do, on hover. "Load one cycle" is the
        // clearest label of the three and still does not say where the items
        // come from or how many that is.
        _load.TooltipText =
            "Takes exactly one cycle's worth of inputs out of your pockets and\n" +
            "puts them in this machine. One cycle, so the progress bar moves\n" +
            "once and you can see what the machine does with them.";

        GetNode<Button>("Margin/Rows/Buttons/Take").TooltipText =
            "Empties this machine's output buffer into your pockets.\n" +
            "A full output buffer is why a machine stops: it has nowhere to\n" +
            "put what it made.";
        _load.Pressed += LoadOneCycle;
        GetNode<Button>("Margin/Rows/Buttons/Take").Pressed += TakeOutput;

        Visible = false;
    }

    /// Wires the panel to the world it is inspecting. It needs the world and
    /// the build catalogue as well as the names now, because retasking a placed
    /// machine is a sim operation and the panel is where a player asks for it.
    public void Bind(ItemDatabase names, Inventory bag, World world, BuildCatalogue buildables)
    {
        _names = names;
        _bag = bag;
        _world = world;
        _buildables = buildables;
    }

    public void Show(Machine machine, in MachinePlacement placement, int index)
    {
        var moved = !ReferenceEquals(_machine, machine);
        _machine = machine;
        _miner = null;
        _placement = placement;
        _index = index;
        if (moved)
        {
            _retaskMessage = "";
            RebuildRecipeList(machine);
        }
        Visible = true;
        Refresh();
    }

    /// The recipes this machine could be making instead. Built from
    /// `BuildCatalogue.RecipesFor` -- the same source and the same ordering the
    /// build menu offers before placing, so the two lists can never disagree
    /// about what a machine is allowed to run.
    private void RebuildRecipeList(Machine machine)
    {
        _recipes.Clear();
        _shownRecipes.Clear();

        if (machine.SourceItem is not { } item || !_buildables.TryGet(item, out var buildable))
        {
            // An empty list still reserves its height, which reads as a control
            // that failed to load rather than one that does not apply.
            _recipes.Visible = false;
            _recipeTitle.Text = "Makes: (this machine cannot be retasked)";
            return;
        }

        // The Uplink has exactly one "recipe" and it makes nothing, so a picker
        // on it would offer a single row reading "<- nothing". It is the one
        // machine in the game with no choice to make about what it does.
        if (machine.Recipe.Id == Research.UplinkRecipe)
        {
            _recipes.Visible = false;
            _recipeTitle.Text = "";
            return;
        }

        _recipes.Visible = true;

        _recipeTitle.Text = "Makes -- pick another to retask:";

        foreach (var recipe in _buildables.RecipesFor(buildable, _world.Research))
        {
            _shownRecipes.Add(recipe);
            _recipes.AddItem(Describe(recipe),
                             recipe.Outputs.Count > 0
                                 ? ItemIcons.For(_names.GetName(recipe.Outputs[0].Item))
                                 : null);
        }

        var current = _shownRecipes.FindIndex(r => ReferenceEquals(r, machine.Recipe));
        if (current >= 0) _recipes.Select(current);
    }

    /// A recipe as the player judges it: what comes out, from what. Same shape
    /// as the build menu's rows, for the same reason.
    private string Describe(Recipe recipe)
    {
        var outputs = string.Join(" + ", recipe.Outputs.Select(o => $"{o.Count} {ItemName(o.Item)}"));
        var inputs = recipe.Inputs.Count == 0
            ? "nothing"
            : string.Join(" + ", recipe.Inputs.Select(i => $"{i.Count} {ItemName(i.Item)}"));
        return $"{outputs}  <-  {inputs}";
    }

    /// Retasking, from the player's click. The sim decides whether it is
    /// allowed and hands back whatever was inside; every outcome gets its own
    /// sentence, because "it already makes that" and "it cannot make that" are
    /// not the same thing to the person who just clicked.
    private void OnRecipePicked(long row)
    {
        if (_machine is null || row < 0 || row >= _shownRecipes.Count) return;

        if (Actions is null) return;

        // By tile, not by index: the command layer resolves the machine when it
        // is applied, so a removal that moved this machine's index in the six
        // ticks between the click and the change cannot retask the wrong one.
        Actions.Retask(_placement.X, _placement.Y, _shownRecipes[(int)row]);
        if (Actions.Shared)
            _retaskMessage = "Retasking -- waiting for everyone (about 100 ms).";
    }

    /// Miners get the same panel. A player should not have to learn two
    /// different readouts for "what is this building doing".
    public void Show(Miner miner, in MachinePlacement placement, int remainingInPatch)
    {
        _miner = miner;
        _machine = null;
        _index = -1;
        _retaskMessage = "";
        _recipes.Clear();
        _shownRecipes.Clear();
        _recipes.Visible = false;
        _recipeTitle.Text = "";
        _placement = placement;
        _patchRemaining = remainingInPatch;
        Visible = true;
        Refresh();
    }

    private int _patchRemaining;

    public void Close()
    {
        _machine = null;
        _miner = null;
        _index = -1;
        Visible = false;
    }

    public bool IsShowing => _machine is not null || _miner is not null;

    /// How many recipes the picker is offering, and whether the one the machine
    /// is actually running is the highlighted row. Reported by the headless
    /// smoke run: a picker with no rows and a picker that has lost track of
    /// what the machine makes both photograph as a box with text in it.
    public int RecipeOptions => _shownRecipes.Count;

    public bool CurrentRecipeIsSelected =>
        _machine is not null && _recipes.IsAnythingSelected() &&
        _recipes.GetSelectedItems().Length == 1 &&
        _shownRecipes.Count > _recipes.GetSelectedItems()[0] &&
        ReferenceEquals(_shownRecipes[_recipes.GetSelectedItems()[0]], _machine.Recipe);

    public override void _Process(double delta)
    {
        if (IsShowing)
            Refresh();
    }

    /// Whether the machine this panel is pointed at is the Uplink. Recognised
    /// by its recipe rather than by a flag on the panel, so a second Uplink --
    /// they are craftable -- reads the same as the first.
    private bool IsUplink => _machine is not null && _machine.Recipe.Id == Research.UplinkRecipe;

    private void Refresh()
    {
        if (_miner is not null)
        {
            RefreshMiner(_miner);
            return;
        }

        if (IsUplink)
        {
            RefreshUplink(_machine!);
            return;
        }

        var machine = _machine!;

        _load.Text = "Load one cycle";
        _load.TooltipText =
            "Takes exactly one cycle's worth of inputs out of your pockets and\n" +
            "puts them in this machine. One cycle, so the progress bar moves\n" +
            "once and you can see what the machine does with them.";
        _title.Text = $"{machine.Recipe.Id}   [{_placement.Size}x{_placement.Size}]";

        // A parallel machine's real per-cycle amounts, not the recipe card's.
        // Showing the card would make the panel lie about the machine it is on.
        // Fluids are marked, because "why is this starved" has a different
        // answer for a pipe than for a belt and the player needs to know which
        // they are looking at.
        var consumes = string.Join(", ", machine.Recipe.Inputs.Select(
            i => $"{machine.InputPerCycle(i.Item)} {ItemName(i.Item)}{(i.IsFluid ? " (piped)" : "")}"));
        var makes = string.Join(", ", machine.Recipe.Outputs.Select(
            o => $"{machine.OutputPerCycle(o.Item)} {ItemName(o.Item)}{(o.IsFluid ? " (piped)" : "")}"));

        _subtitle.Text = $"{consumes}  ->  {makes}   ({machine.Recipe.DurationTicks} ticks)";

        _progress.Value = machine.Progress;
        _status.Text = machine.State switch
        {
            MachineState.Working => $"Working -- {machine.TicksRemaining} ticks left",
            // The failure states are the whole reason this panel exists, so
            // they say what to do about it rather than naming themselves.
            MachineState.Starved => "Starved -- waiting on " + Missing(machine),
            MachineState.Blocked => "Blocked -- output full, nothing is taking it away",
            MachineState.Unpowered => machine.Energy > 0
                ? $"Browning out -- {machine.Energy}/{machine.PowerDraw} charged, needs more supply"
                : "No power -- not connected to a grid, or the grid has none spare",
            _ => "Idle",
        };

        if (machine.PowerDraw > 0)
            _subtitle.Text += $"   [{machine.PowerDraw}/tick]";

        _inputs.Text = "In:  " + Describe(machine.InputContents);
        _outputs.Text = "Out: " + Describe(machine.OutputContents);
        _carrying.Text = "Carrying: " + Describe(_bag.Contents);
        _retask.Text = _retaskMessage;
    }

    /// A miner's readout. It reports what is left in the ground, because that is
    /// the number that decides whether to keep building here or move on -- and
    /// it is the only number the machine panel cannot infer.
    private void RefreshMiner(Miner miner)
    {
        _load.Text = "Load one cycle";
        _load.TooltipText =
            "Takes exactly one cycle's worth of inputs out of your pockets and\n" +
            "puts them in this machine. One cycle, so the progress bar moves\n" +
            "once and you can see what the machine does with them.";
        _title.Text = $"Mining {ItemName(miner.Item)}   [{_placement.Size}x{_placement.Size}]";
        _subtitle.Text = $"{miner.YieldPerCycle} per {miner.CycleTicks} ticks   " +
                         $"({_patchRemaining} left in this patch)";

        _progress.Value = miner.Progress;
        _status.Text = miner.State switch
        {
            MachineState.Working => $"Mining -- {miner.TicksRemaining} ticks left",
            MachineState.Blocked => "Full -- nothing is taking the ore away",
            MachineState.Depleted => "Worked out -- this patch is finished",
            MachineState.Unpowered => miner.Energy > 0
                ? $"Browning out -- {miner.Energy}/{miner.PowerDraw} charged"
                : "No power -- not connected to a grid, or the grid has none spare",
            _ => "Idle",
        };

        _inputs.Text = "In:  the ground";
        _outputs.Text = $"Out: {miner.Buffered} {ItemName(miner.Item)}";
        _carrying.Text = "Carrying: " + Describe(_bag.Contents);
        // The miner's readout has no retask line of its own, so it is where a
        // refused Take says why. Blanking it unconditionally, as this did,
        // meant emptying a miner from across the map failed in silence.
        _retask.Text = _retaskMessage;
    }

    /// The Uplink's readout: what research is waiting on, and what is sitting
    /// in the hopper. It runs no cycle, so the fields that would show one say
    /// what the building is for instead -- a progress bar frozen at zero is the
    /// most confusing thing this panel could show on the one machine that never
    /// works.
    private void RefreshUplink(Machine machine)
    {
        var research = _world.Research;

        // "Load one cycle" is meaningless on a machine with no cycle, and a
        // button whose label lies about what it does is worse than no button.
        _load.Text = "Deliver what I carry";
        _load.TooltipText =
            "Hands over everything you are carrying that an open objective\n" +
            "wants. Delivered items are spent -- this is what research costs.\n" +
            "An inserter or a belt can do the same job without you walking.";
        _title.Text = $"Uplink   [{_placement.Size}x{_placement.Size}]";
        _subtitle.Text = "Deliver research here -- by hand, by inserter or by drone.";
        _progress.Value = 0;

        if (research is null)
        {
            _status.Text = "This world has no research.";
        }
        else if (research.SeedDelivered)
        {
            _status.Text = "The Seed is away. Nothing more is wanted.";
        }
        else
        {
            // Grouped by item, not listed per objective. Four Steam techs want
            // the same hull, and naming each of them turned one short line into
            // four long ones that pushed this panel off the side of the screen.
            var wants = research.Objectives
                .SelectMany(o => o.Needs.Where(n => !n.Met))
                .GroupBy(n => n.Item)
                .Select(g => $"{g.Sum(n => n.Outstanding)} x {(g.First().IsGroup ? g.Key : ItemLabel(g.Key))}")
                .ToList();

            _status.Text = wants.Count == 0
                ? "Nothing is wanted right now."
                : "Wants: " + string.Join("\n       ", wants);
        }

        // What is in the hopper is what nothing wanted: the Uplink credits what
        // it can each tick and leaves the rest, so a full hopper is a misrouted
        // belt rather than a queue.
        _inputs.Text = "Unwanted, still in the hopper: " + Describe(machine.InputContents);
        _outputs.Text = "";
        _carrying.Text = "Carrying: " + Describe(_bag.Contents);
        _retask.Text = _retaskMessage;
    }

    /// Hand-delivers everything the player is carrying that research wants.
    /// The `Load` button, on the one machine where "load one cycle" means
    /// nothing -- an Uplink has no cycle.
    /// The noises this panel makes. Set by `GameRoot`; null in a headless run
    /// and in the art preview harness, and every call site tolerates that.
    public Sounds? Audio { get; set; }

    /// Whether the player could put a hand on what this panel is showing.
    ///
    /// Only the *actions* ask. Opening the panel never does: looking is not
    /// touching, so a machine across the map can be inspected from anywhere and
    /// only its Load and Take buttons refuse (ADR 0033).
    private bool InReach => _world.InHandReach(_placement);

    private static readonly string TooFar =
        $"Too far to reach -- walk closer. Your hands go {Sim.Player.HandReachTiles} tiles.";

    /// Handing what you are carrying to the Uplink.
    ///
    /// One `Deliver` command per item the objectives want and the player
    /// actually has. Both filters are *reads*, not rules: the sim re-checks
    /// reach, carriage and want when the command lands, and refuses with its
    /// own reason. They are here so a click on a full pack does not fire forty
    /// commands, thirty-nine of which come back a tenth of a second later
    /// saying "you are not carrying that".
    public int DeliverByHand()
    {
        if (_machine is null || _world.Research is null || Actions is null) return 0;

        var offered = 0;

        foreach (var objective in _world.Research.Objectives.ToList())
            foreach (var need in objective.Needs)
            {
                if (need.Met) continue;

                // A need accepts a *set* of items -- the opening rungs take any
                // ore, any ingot -- so every one of them is offered, and the
                // need's own key is not an item id at all. Handing only
                // `need.Item` over made this button do nothing on the first
                // four objectives of the game.
                foreach (var wanted in need.Accepts)
                {
                    if (!_names.TryGetId(wanted, out var id)) continue;

                    var held = _bag.Count(id);
                    if (held <= 0) continue;

                    var want = System.Math.Min(held, need.Outstanding);
                    if (want <= 0) continue;

                    Actions.Deliver(wanted, want, $"Handing over {want} {ItemLabel(wanted)}");
                    offered++;
                }
            }

        if (offered == 0)
        {
            Audio?.Play(Sounds.Cue.Refuse);
            _retaskMessage = "You are carrying nothing it wants.";
            return 0;
        }

        _retaskMessage = Actions.Shared
            ? $"Handing over {offered} kind(s) -- waiting for everyone (about 100 ms)."
            : "";
        return offered;
    }

    private string Missing(Machine machine)
    {
        var short_ = machine.Recipe.Inputs
            .Where(i => machine.GetInputCount(i.Item) < machine.InputPerCycle(i.Item))
            .Select(i => $"{ItemName(i.Item)} " +
                         $"({machine.GetInputCount(i.Item)}/{machine.InputPerCycle(i.Item)})");
        var text = string.Join(", ", short_);
        return text.Length > 0 ? text : "space";
    }

    private string Describe(System.Collections.Generic.IReadOnlyDictionary<ItemId, int> contents)
    {
        if (contents.Count == 0) return "empty";
        return string.Join(", ", contents.OrderBy(kv => kv.Key.Value)
                                         .Select(kv => $"{kv.Value} {ItemName(kv.Key)}"));
    }

    /// The readable name for a data item id, for the Uplink's want list. The
    /// panel elsewhere names items through the world's table, which is keyed by
    /// runtime id; research speaks in data ids, so this is the other direction.
    private static string ItemLabel(string itemId) => ItemText.Of(itemId);

    /// The name a player reads, not the key the data is filed under.
    ///
    /// This used to hand back the item's data id, so a panel that had every
    /// other string right still told the player they were carrying
    /// "24 stone_deposit, 1 man_manual_crafting, 1 man_uplink". The id is what
    /// the recipe graph is keyed by and nobody outside the code should ever see
    /// one.
    private string ItemName(ItemId item) => ItemText.Of(_names, item);

    /// Loads exactly one cycle's worth from the player's inventory -- the
    /// smallest useful unit of hand-feeding, and the one that makes the progress
    /// bar move exactly once so the machine's behaviour is legible.
    private void LoadOneCycle()
    {
        if (_machine is null) return;      // nothing to hand-load into a miner

        if (IsUplink)
        {
            DeliverByHand();
            return;
        }

        if (Actions is { Shared: true })
        {
            Audio?.Play(Sounds.Cue.Refuse);
            _retaskMessage = NoHandOpsShared;
            return;
        }

        if (!InReach)
        {
            Audio?.Play(Sounds.Cue.Refuse);
            _retaskMessage = TooFar;
            return;
        }

        foreach (var input in _machine.Recipe.Inputs)
        {
            var want = _machine.InputPerCycle(input.Item) - _machine.GetInputCount(input.Item);
            if (want > 0)
                HandOps.Insert(_bag, _machine, input.Item, want);
        }
    }

    private void TakeOutput()
    {
        if (Actions is { Shared: true })
        {
            Audio?.Play(Sounds.Cue.Refuse);
            _retaskMessage = NoHandOpsShared;
            return;
        }

        if (!InReach)
        {
            Audio?.Play(Sounds.Cue.Refuse);
            _retaskMessage = TooFar;
            return;
        }

        if (_machine is not null)
            HandOps.ExtractAll(_machine, _bag);

        // Emptying a miner by hand is how the first ore moves, before there is
        // an inserter to do it.
        if (_miner is not null)
        {
            var taken = _miner.Pull(int.MaxValue);
            if (taken > 0) _bag.Add(_miner.Item, taken);
        }
    }
}
