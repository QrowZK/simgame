using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using Sim.Data;

namespace Game;

/// The progression screen: the tech graph drawn as a graph, with the quest list
/// folded into it.
///
/// In this game a tech and a quest are the same object seen from two sides --
/// research is a physical delivery into the Uplink, so "deliver three metal
/// ingots" *is* the node "First Metal". The old objectives list showed one side of that as
/// a list of sentences, which is the whole dependency graph flattened into
/// prose: a player can read what to deliver next, but not where they are, what
/// it leads to, or why the thing they want is out of reach. This is the other
/// side, and it is meant to replace the list rather than sit beside it. Two
/// places to look for one fact is the defect, not the fix.
///
/// Every node carries the three questions:
///
/// * **What it wants** -- the delivery, how much has been delivered, and how
///   much of it the player is carrying right now.
/// * **What it gives** -- in capability, not counts. "Gives you 2 Steam
///   Inserters, lets you build the Steam Alloy Smelter" is a reason to do
///   something; "unlocks 26 recipes" is a number.
/// * **Why it is locked** -- as a sentence, because the house rule is that
///   refusals carry reasons. A greyed-out box that will not say what is missing
///   is the failure mode of every opaque tech tree, and it is avoidable here:
///   the recipe graph knows which recipe makes the missing hull and which tech
///   opens that recipe.
///
/// Read from the data and from `Research` on every `Refresh`. The panel owns no
/// progression state and changes none: holding it open cannot touch the sim.
public sealed partial class TechTreePanel : PanelContainer
{
    /// How a node reads at a glance -- the four answers to "can I do this now",
    /// and the only thing colour encodes here.
    public enum NodeState
    {
        Done,

        /// Prerequisites met and the player is carrying enough: walk to the
        /// Uplink and this completes.
        Ready,

        /// Prerequisites met, not enough in hand. This is the work in front of you.
        Open,

        /// A prerequisite tech is missing.
        Locked,
    }

    public sealed class NodeView
    {
        public required TechDef Tech { get; init; }

        /// Column is the research line; row is dependency depth, not tier. See
        /// `Refresh` for why depth: the intro rungs are four techs in one tier
        /// and one line, and a tier-per-row grid stacks them on top of each
        /// other.
        public required int Column { get; init; }
        public required int Row { get; init; }

        public NodeState State { get; set; }
        public Rect2 Box { get; set; }

        public int Delivered { get; set; }
        public int Required { get; set; }

        /// How many of the wanted item -- or of anything the group accepts --
        /// the player is carrying.
        public int Held { get; set; }

        public string WantsLine { get; set; } = "";
        public string GivesLine { get; set; } = "";
        public string WhyLine { get; set; } = "";
        public string ShortWant { get; set; } = "";

        /// The first prerequisite still missing, by name. Drawn on the node
        /// itself: "locked" on a box tells a player nothing, and the name of
        /// the thing in the way is the whole answer at a glance.
        public string Blocker { get; set; } = "";
    }

    private Label _title = null!;
    private Label _legend = null!;
    private Label _premise = null!;
    private Label _detailTitle = null!;
    private RichTextLabel _detail = null!;
    private Graph _graph = null!;

    private World _world = null!;
    private readonly List<NodeView> _nodes = new();
    private string _selected = "";

    private static GameData Data => Sim.Data.Catalogue.Instance.Data;

    private static readonly Dictionary<string, string> ItemNames =
        Data.Items.ToDictionary(i => i.Id, i => i.Name);

    private static readonly Dictionary<string, string> TierNames =
        Data.Tiers.ToDictionary(t => t.Id, t => t.Name);

    private static readonly Dictionary<string, int> TierIndex =
        Data.Tiers.ToDictionary(t => t.Id, t => t.Index);

    private static readonly Dictionary<string, string> MachineNames =
        Data.Machines.ToDictionary(m => m.Id, m => m.Name);

    /// Item id -> the recipes that produce it. Built once. This is what lets a
    /// locked node name the thing that would unstick it instead of shrugging.
    private static readonly Dictionary<string, List<RecipeDef>> MadeBy = BuildMadeBy();

    public bool IsShowing => Visible;

    public override void _Ready()
    {
        _title = GetNode<Label>("Margin/Rows/Title");
        _legend = GetNode<Label>("Margin/Rows/Legend");
        _premise = GetNode<Label>("Margin/Rows/Premise");
        _detailTitle = GetNode<Label>("Margin/Rows/Body/Detail/DetailTitle");
        _detail = GetNode<RichTextLabel>("Margin/Rows/Body/Detail/DetailBody");
        GetNode<Button>("Margin/Rows/Buttons/Close").Pressed += Close;

        _graph = new Graph { Name = "Graph", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _graph.Picked += Select;
        GetNode<Control>("Margin/Rows/Body/GraphHolder").AddChild(_graph);
        // Anchors *and* offsets: anchors alone left the graph at its own tiny
        // natural size while it laid itself out for a full-size box, which drew
        // the bottom row and the goal bar outside the panel.
        _graph.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        Visible = false;
    }

    public void Bind(World world) => _world = world;

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    /// The three sentences a new game opens on, above the graph, shown once.
    ///
    /// Carried here, and nowhere else now, so that this screen can be the
    /// only progression screen: the premise belongs to the moment the game
    /// starts, and it would be lost if the list it currently lives on were
    /// replaced by the graph.
    public void OpenWithPremise()
    {
        _premise.Text = Premise;
        _premise.Visible = true;
        Open();
    }

    private const string Premise =
        "Your probe came apart on entry. The fabricator survived; nothing else did. " +
        "A Von Neumann probe exists to make another Von Neumann probe -- yours cannot yet, " +
        "so everything below is a thing to go and deliver, in the order the graph allows. " +
        "Build the industry. Build the Seed. Send it on.";

    public void Close()
    {
        Visible = false;
        // One-shot: three sentences re-read every time the graph is opened
        // become wallpaper.
        _premise.Visible = false;
    }

    /// Selects a node by tech id. Public so a caller can open the screen
    /// pointed at something specific: "why can't I build this" asked from a
    /// build-menu refusal has an obvious destination.
    public void Select(string techId)
    {
        _selected = techId;
        if (Visible) Refresh();
    }

    public string SelectedTech => _selected;

    /// Everything the panel is showing, as plain text, so a headless run can
    /// assert it says something real. A drawn graph photographs identically
    /// whether its labels are right or empty; this is the check a still cannot
    /// make.
    public string BodyText { get; private set; } = "";

    public IReadOnlyList<NodeView> Nodes => _nodes;

    public int CountOf(NodeState state) => _nodes.Count(n => n.State == state);

    public void Refresh()
    {
        var research = _world?.Research;
        _nodes.Clear();

        if (research is null)
        {
            _title.Text = "Progression";
            _detailTitle.Text = "";
            _detail.Text = "This world has no research.";
            BodyText = "This world has no research.";
            _graph.Layout(_nodes, System.Array.Empty<string>(), System.Array.Empty<string>());
            _graph.QueueRedraw();
            return;
        }

        var techs = Data.Techs;
        var byId = techs.ToDictionary(t => t.Id);
        var lines = techs.Select(t => t.Line).Distinct().ToList();

        // Rows are dependency depth: the longest chain of prerequisites behind
        // a tech. Tier was the obvious choice and is wrong -- the opening rungs
        // are four techs in the Manual tier of one line, which a tier grid draws
        // on top of each other. Depth also makes every edge point downwards,
        // which is what stops the graph reading as a scribble.
        var depth = new Dictionary<string, int>();

        int Depth(string id)
        {
            if (depth.TryGetValue(id, out var found)) return found;
            depth[id] = 0;   // guards a cycle in the data rather than hanging
            var tech = byId[id];
            var value = tech.Requires.Count == 0
                ? 0
                : tech.Requires.Where(byId.ContainsKey).Select(Depth).DefaultIfEmpty(-1).Max() + 1;
            return depth[id] = value;
        }

        foreach (var tech in techs) Depth(tech.Id);

        var rowCount = depth.Values.Max() + 1;

        foreach (var tech in techs)
        {
            var view = new NodeView
            {
                Tech = tech,
                Column = lines.IndexOf(tech.Line),
                Row = depth[tech.Id],
            };
            Describe(view, research);
            _nodes.Add(view);
        }

        // Row labels: the tier the row's techs belong to. Several rows share a
        // tier (a tier is four or eight techs deep), which is honest -- the
        // ladder is not one rung per tier.
        var rowTiers = new List<string>();
        for (var r = 0; r < rowCount; r++)
        {
            var here = _nodes.Where(n => n.Row == r)
                             .Select(n => n.Tech.Tier)
                             .GroupBy(t => t)
                             .OrderByDescending(g => g.Count())
                             .FirstOrDefault();
            rowTiers.Add(here?.Key ?? "");
        }

        _graph.Layout(_nodes, lines, rowTiers);

        var done = CountOf(NodeState.Done);
        var ready = CountOf(NodeState.Ready);
        _title.Text = $"Progression  --  {done}/{_nodes.Count} researched, " +
                      $"{ready} ready to deliver, {CountOf(NodeState.Open)} open";
        _legend.Text = "green: researched    amber: ready, you are carrying it    " +
                       "pale: open, go and make it    grey: locked, a tech is missing    " +
                       "click a node for what it wants, what it gives and why";

        // Default the detail pane at something a player can act on. An empty
        // detail pane wastes the third of the screen that answers "what next".
        if (_nodes.All(n => n.Tech.Id != _selected))
            _selected = "";
        if (_selected.Length == 0 && _nodes.Count > 0)
            _selected = (_nodes.FirstOrDefault(n => n.State == NodeState.Ready)
                      ?? _nodes.FirstOrDefault(n => n.State == NodeState.Open)
                      ?? _nodes[^1]).Tech.Id;

        var selected = _nodes.First(n => n.Tech.Id == _selected);
        WriteDetail(selected);
        WriteBodyText(research, selected);
        _graph.Selected = _selected;
        _graph.QueueRedraw();
    }

    // ---- what a node says --------------------------------------------------

    /// What a tech wants, read from the data rather than from an objective:
    /// `requires_items` is a group ("any metal ingot") whose label is not an
    /// item id, and `requires_count` may be more than one.
    private static IReadOnlyList<string> Accepts(TechDef tech)
        => tech.RequiresItems.Count > 0
            ? tech.RequiresItems
            : tech.RequiresItem is null ? System.Array.Empty<string>() : new[] { tech.RequiresItem };

    private static int RequiredCount(TechDef tech)
        => tech.RequiresItem is null ? 0 : System.Math.Max(1, tech.RequiresCount);

    private void Describe(NodeView view, Research research)
    {
        var tech = view.Tech;
        var unlocked = research.IsTechUnlocked(tech.Id);
        var missing = tech.Requires.Where(r => !research.IsTechUnlocked(r)).ToList();
        var accepts = Accepts(tech);

        view.Required = RequiredCount(tech);
        if (view.Required > 0)
        {
            view.Delivered = research.Delivered(tech.Id, tech.RequiresItem!);
            view.Held = accepts.Sum(Held);
        }

        view.State = unlocked ? NodeState.Done
                   : missing.Count > 0 ? NodeState.Locked
                   : view.Held >= view.Required - view.Delivered && view.Required > 0
                       ? NodeState.Ready
                       : NodeState.Open;

        var wanted = tech.RequiresItem is null ? "" : Named(tech.RequiresItem);
        view.ShortWant = view.Required > 1 ? $"{view.Required}x {wanted}" : wanted;

        view.WantsLine = tech.RequiresItem is null
            ? "Nothing. Open from the start."
            : $"{view.Required}x {wanted}  --  delivered {view.Delivered}/{view.Required}, " +
              $"you are carrying {view.Held}." +
              (accepts.Count > 1
                  ? "\nAny of: " + string.Join(", ", accepts.Select(Named)) + "."
                  : "");

        view.Blocker = missing.Count == 0 ? "" : NameOfTech(missing[0]);
        view.GivesLine = Gives(tech);
        view.WhyLine = Why(view, research, missing, accepts);
    }

    /// What unlocking this lets a player *do*. The handout first, because it is
    /// concrete and immediate; then the machines it opens, because a machine is
    /// a thing you place; then what those machines make.
    private static string Gives(TechDef tech)
    {
        var parts = new List<string>();

        if (tech.Rewards.Count > 0)
            parts.Add("Gives you " + string.Join(", ", tech.Rewards.Select(
                r => r.Count == 1 ? $"1 {Named(r.Item)}" : $"{r.Count}x {Named(r.Item)}")));

        var itemsById = Data.Items.ToDictionary(i => i.Id);
        var machines = new List<string>();
        var products = new List<string>();

        foreach (var recipe in Data.Recipes.Where(r => r.UnlockedBy == tech.Id))
            foreach (var output in recipe.Outputs)
            {
                if (!itemsById.TryGetValue(output.Item, out var item)) continue;
                var into = item.Category == "machine" ? machines : products;
                if (!into.Contains(item.Name)) into.Add(item.Name);
            }

        if (machines.Count > 0)
            parts.Add((parts.Count > 0 ? "lets you build " : "Lets you build ") + Listed(machines, 3));
        if (products.Count > 0)
            parts.Add((parts.Count > 0 ? "and makes " : "Makes ") + Listed(products, 3));

        return parts.Count == 0 ? "Nothing new to build." : string.Join(", ", parts) + ".";
    }

    /// The sentence a node owes the player. Never just "locked": either a tech
    /// is missing and it is named, or the item is missing and this says what
    /// makes it -- and, when nothing buildable does, which tech opens the recipe
    /// that would.
    private string Why(NodeView view, Research research, List<string> missingTechs,
                       IReadOnlyList<string> accepts)
    {
        if (view.State == NodeState.Done)
            return "Researched. Everything behind it is open.";

        if (missingTechs.Count > 0)
            return $"Needs {Listed(missingTechs.Select(NameOfTech).ToList(), 3)} first.";

        var outstanding = view.Required - view.Delivered;
        if (view.Held >= outstanding && outstanding > 0)
            return $"You are carrying {view.Held}. Take {outstanding} to the Uplink and this is done.";

        // "You have no any raw ore" is what naming a group label like an item
        // produces, so a group is phrased as a group.
        var wanted = Named(view.Tech.RequiresItem ?? "");
        var group = accepts.Count > 1;
        var short_ = view.Held == 0
            ? group ? $"Nothing you are carrying counts as {wanted}" : $"You have no {wanted}"
            : $"You have {view.Held} of the {outstanding} {wanted} still wanted";

        // What makes any of the accepted items, and whether the player can
        // build it today.
        foreach (var item in accepts)
        {
            var makers = MadeBy.GetValueOrDefault(item) ?? new List<RecipeDef>();
            var open = makers.FirstOrDefault(r => research.IsUnlocked(r.Id));
            if (open is null) continue;

            var where = MachineNames.GetValueOrDefault(open.Machine, open.Machine);
            var inputs = open.Inputs.Count == 0
                ? "nothing but time"
                : string.Join(" + ", open.Inputs.Select(i => $"{i.Count}x {Named(i.Item)}"));
            return $"{short_}. Make {Named(item)} in the {where}: {inputs}.";
        }

        // Raw ore has no recipe: it is dug, not made.
        var raw = accepts.FirstOrDefault(a => Data.Items.Any(i => i.Id == a && i.Raw));
        if (raw is not null)
            return $"{short_}. Dig it out of the ground -- the survey device marks a patch you can use.";

        var blocked = accepts.SelectMany(a => MadeBy.GetValueOrDefault(a) ?? new List<RecipeDef>())
                             .FirstOrDefault();
        return blocked is null
            ? $"{short_}, and no recipe in the game makes one."
            : $"{short_}, and nothing you can build makes one yet. " +
              $"{NameOfTech(blocked.UnlockedBy)} opens the recipe that does.";
    }

    private void WriteDetail(NodeView view)
    {
        var status = view.State switch
        {
            NodeState.Done => "[color=#7fd08a]RESEARCHED[/color]",
            NodeState.Ready => "[color=#f2c14e]READY TO DELIVER[/color]",
            NodeState.Open => "[color=#e0e4ea]OPEN[/color]",
            _ => "[color=#8a8f98]LOCKED[/color]",
        };

        _detailTitle.Text = view.Tech.Name;
        _detail.Text =
            $"{status}   {TierNames.GetValueOrDefault(view.Tech.Tier, view.Tech.Tier)} tier, " +
            $"{view.Tech.Line} line\n\n" +
            $"[b]Wants[/b]\n{view.WantsLine}\n\n" +
            $"[b]Gives[/b]\n{view.GivesLine}\n\n" +
            $"[b]Why[/b]\n{view.WhyLine}\n\n" +
            $"[b]Leads to[/b]\n{LeadsTo(view.Tech.Id)}";
    }

    private static string LeadsTo(string techId)
    {
        var next = Data.Techs.Where(t => t.Requires.Contains(techId)).Select(t => t.Name).ToList();
        return next.Count == 0
            ? "The Von Neumann Seed. This is the end of its line."
            : Listed(next, 4) + ".";
    }

    private void WriteBodyText(Research research, NodeView selected)
    {
        var rows = new List<string> { _title.Text };

        foreach (var node in _nodes)
        {
            var state = node.State switch
            {
                NodeState.Done => "done  ",
                NodeState.Ready => "ready ",
                NodeState.Open => "open  ",
                _ => "locked",
            };

            rows.Add($"row {node.Row} {node.Tech.Line,-12} {state} {node.Tech.Name}" +
                     (node.Required > 0
                        ? $" -- {node.Delivered}/{node.Required} {Named(node.Tech.RequiresItem!)}" +
                          $" (holding {node.Held})"
                        : ""));
        }

        var seed = research.SeedObjectiveNow();
        rows.Add($"GOAL Von Neumann Seed -- {seed.Needs.Count(n => n.Met)}/{seed.Needs.Count} " +
                 "assemblies delivered");
        rows.Add($"selected {selected.Tech.Name}: {selected.WhyLine}");

        BodyText = string.Join("\n", rows);
    }

    // ---- helpers -----------------------------------------------------------

    private int Held(string itemId)
        => _world.Items.TryGetId(itemId, out var id) ? _world.PlayerInventory.Count(id) : 0;

    private static string Named(string itemId) => ItemNames.GetValueOrDefault(itemId, itemId);

    private static string NameOfTech(string techId)
        => Data.Techs.FirstOrDefault(t => t.Id == techId)?.Name ?? techId;

    private static string Listed(IReadOnlyList<string> names, int limit)
    {
        if (names.Count <= limit)
            return names.Count switch
            {
                0 => "nothing",
                1 => "the " + names[0],
                _ => "the " + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
            };

        return "the " + string.Join(", ", names.Take(limit)) + $" and {names.Count - limit} more";
    }

    private static Dictionary<string, List<RecipeDef>> BuildMadeBy()
    {
        var map = new Dictionary<string, List<RecipeDef>>();
        foreach (var recipe in Data.Recipes)
            foreach (var output in recipe.Outputs)
            {
                if (!map.TryGetValue(output.Item, out var list))
                    map[output.Item] = list = new List<RecipeDef>();
                list.Add(recipe);
            }

        return map;
    }

    // ---- the graph ---------------------------------------------------------

    /// The drawing surface. A `Control` with a `_Draw` rather than a grid of
    /// buttons: the edges are half the information, and 36 nodes plus their
    /// dependency lines would otherwise be ninety controls laid out by hand.
    ///
    /// The layout is measured from the control's own size, so the graph fits
    /// whatever the data turns out to be -- 32 techs in 8 rows or 36 in 11.
    public sealed partial class Graph : Control
    {
        public const float Gutter = 88f;
        public const float ColumnGap = 8f;
        public const float RowGap = 6f;
        public const float HeaderHeight = 18f;
        public const float SeedHeight = 26f;

        public string Selected = "";

        [Signal]
        public delegate void PickedEventHandler(string techId);

        private IReadOnlyList<NodeView> _nodes = System.Array.Empty<NodeView>();
        private IReadOnlyList<string> _lines = System.Array.Empty<string>();
        private IReadOnlyList<string> _rowTiers = System.Array.Empty<string>();
        private Rect2 _seedBox;
        private float _nodeWidth = 130f;
        private float _nodeHeight = 40f;

        public void Layout(IReadOnlyList<NodeView> nodes, IReadOnlyList<string> lines,
                           IReadOnlyList<string> rowTiers)
        {
            _nodes = nodes;
            _lines = lines;
            _rowTiers = rowTiers;
            if (nodes.Count == 0 || lines.Count == 0 || rowTiers.Count == 0)
                return;

            var area = Size;
            if (area.X < 100f || area.Y < 100f)
                area = new Vector2(760f, 540f);   // before the first layout pass

            _nodeWidth = (area.X - Gutter - (lines.Count - 1) * ColumnGap) / lines.Count;
            _nodeHeight = (area.Y - HeaderHeight - SeedHeight - 8f
                           - (rowTiers.Count - 1) * RowGap) / rowTiers.Count;

            foreach (var node in nodes)
                node.Box = new Rect2(
                    Gutter + node.Column * (_nodeWidth + ColumnGap),
                    HeaderHeight + node.Row * (_nodeHeight + RowGap),
                    _nodeWidth, _nodeHeight);

            _seedBox = new Rect2(Gutter,
                                 HeaderHeight + rowTiers.Count * (_nodeHeight + RowGap) + 2f,
                                 lines.Count * _nodeWidth + (lines.Count - 1) * ColumnGap,
                                 SeedHeight);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationResized && _nodes.Count > 0)
            {
                Layout(_nodes, _lines, _rowTiers);
                QueueRedraw();
            }
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is not InputEventMouseButton
                { Pressed: true, ButtonIndex: MouseButton.Left } click)
                return;

            foreach (var node in _nodes)
                if (node.Box.HasPoint(click.Position))
                {
                    EmitSignal(SignalName.Picked, node.Tech.Id);
                    AcceptEvent();
                    return;
                }
        }

        public override void _Draw()
        {
            if (_nodes.Count == 0)
                return;

            var font = GetThemeDefaultFont();
            var byId = _nodes.ToDictionary(n => n.Tech.Id);

            for (var c = 0; c < _lines.Count; c++)
                DrawString(font, new Vector2(Gutter + c * (_nodeWidth + ColumnGap) + 4f, 13f),
                           _lines[c].ToUpperInvariant(), HorizontalAlignment.Left,
                           _nodeWidth - 8f, 10, new Color(0.62f, 0.66f, 0.74f));

            // Row labels: the tier this row of the ladder belongs to, in the
            // colour that tier's machines are drawn in, so the panel and the
            // world agree about what "Voltaic" looks like.
            for (var r = 0; r < _rowTiers.Count; r++)
            {
                var y = HeaderHeight + r * (_nodeHeight + RowGap);
                var colour = MeshKit.TierColor(TierIndexOf(_rowTiers[r]));
                DrawRect(new Rect2(0f, y + 3f, 4f, _nodeHeight - 6f), colour);
                DrawString(font, new Vector2(8f, y + _nodeHeight * 0.5f + 4f),
                           // Ellipsised rather than clipped, in case a tier is
                           // ever named something longer than "Singular".
                           Truncate(font, TierNames.GetValueOrDefault(_rowTiers[r], _rowTiers[r]),
                                    9, Gutter - 14f),
                           HorizontalAlignment.Left, Gutter - 10f, 9,
                           colour.Lerp(Colors.White, 0.35f));
            }

            // Edges under the nodes. An edge is drawn lit when its prerequisite
            // is researched: the lit part of the graph is the part of the
            // factory that exists.
            foreach (var node in _nodes)
                foreach (var required in node.Tech.Requires)
                {
                    if (!byId.TryGetValue(required, out var from)) continue;

                    var a = new Vector2(from.Box.Position.X + _nodeWidth * 0.5f, from.Box.End.Y);
                    var b = new Vector2(node.Box.Position.X + _nodeWidth * 0.5f, node.Box.Position.Y);
                    var lit = node.Tech.Id == Selected || from.Tech.Id == Selected;

                    var colour = lit
                        ? new Color(0.95f, 0.78f, 0.35f, 0.95f)
                        : from.State == NodeState.Done
                            ? new Color(0.52f, 0.82f, 0.60f, 0.95f)
                            : new Color(0.46f, 0.50f, 0.58f, 0.75f);

                    // Elbowed, not straight: a straight line between columns
                    // crosses the nodes in between and reads as a scribble.
                    var mid = (a.Y + b.Y) * 0.5f;
                    var w = lit ? 2.5f : 1.5f;
                    DrawLine(a, new Vector2(a.X, mid), colour, w);
                    DrawLine(new Vector2(a.X, mid), new Vector2(b.X, mid), colour, w);
                    DrawLine(new Vector2(b.X, mid), b, colour, w);
                }

            foreach (var node in _nodes)
                DrawNode(font, node);

            // The goal, wired to the last row: it is the reason for all of it,
            // and a tree that stops at the top row does not say so.
            foreach (var node in _nodes.Where(n => n.Row == _rowTiers.Count - 1))
            {
                var a = new Vector2(node.Box.Position.X + _nodeWidth * 0.5f, node.Box.End.Y);
                DrawLine(a, new Vector2(a.X, _seedBox.Position.Y),
                         new Color(0.55f, 0.50f, 0.70f, 0.7f), 1f);
            }

            DrawRect(_seedBox, new Color(0.16f, 0.15f, 0.22f));
            DrawRect(_seedBox, new Color(0.62f, 0.56f, 0.85f), filled: false, width: 1f);
            DrawString(font, _seedBox.Position + new Vector2(10f, SeedHeight * 0.5f + 4f),
                       "VON NEUMANN SEED  --  build it, and send it on",
                       HorizontalAlignment.Left, _seedBox.Size.X - 20f, 11,
                       new Color(0.80f, 0.76f, 0.95f));
        }

        private static int TierIndexOf(string tierId) => TierIndex.GetValueOrDefault(tierId, 0);

        private void DrawNode(Font font, NodeView node)
        {
            var (fill, border, text) = node.State switch
            {
                NodeState.Done => (new Color(0.13f, 0.24f, 0.16f),
                                   new Color(0.45f, 0.80f, 0.52f),
                                   new Color(0.80f, 0.93f, 0.82f)),
                NodeState.Ready => (new Color(0.28f, 0.22f, 0.08f),
                                    new Color(0.95f, 0.76f, 0.30f),
                                    new Color(1.00f, 0.92f, 0.72f)),
                NodeState.Open => (new Color(0.16f, 0.18f, 0.22f),
                                   new Color(0.72f, 0.76f, 0.84f),
                                   new Color(0.90f, 0.93f, 0.97f)),
                _ => (new Color(0.11f, 0.12f, 0.14f),
                      new Color(0.30f, 0.32f, 0.36f),
                      new Color(0.55f, 0.58f, 0.63f)),
            };

            DrawRect(node.Box, fill);
            var selected = node.Tech.Id == Selected;
            DrawRect(node.Box, selected ? Colors.White : border,
                     filled: false, width: selected ? 2f : 1f);

            // Tier stripe down the left edge, in the machine tier colour.
            DrawRect(new Rect2(node.Box.Position + new Vector2(1f, 1f),
                               new Vector2(3f, node.Box.Size.Y - 2f)),
                     MeshKit.TierColor(TierIndexOf(node.Tech.Tier)));

            // Names are clipped short of the delivery bar rather than drawn
            // under it, and shortened rather than cut mid-word: "Manual Ore
            // Processin" is what the first screenshot of this panel showed.
            var room = _nodeWidth - (node.Required > 0 ? 40f : 14f);
            DrawString(font, node.Box.Position + new Vector2(9f, 15f), Fit(font, node, room),
                       HorizontalAlignment.Left, room, 11, text);

            var second = node.State switch
            {
                NodeState.Done => "researched",
                NodeState.Ready => $"deliver {node.Delivered}/{node.Required} -- have {node.Held}",
                NodeState.Open => $"{node.Delivered}/{node.Required} {Short(node.ShortWant)}",
                _ => node.Blocker.Length > 0 ? $"needs {node.Blocker}" : "locked",
            };

            DrawString(font, node.Box.Position + new Vector2(9f, 29f),
                       Truncate(font, second, 9, _nodeWidth - 34f),
                       HorizontalAlignment.Left, _nodeWidth - 34f, 9,
                       text.Lerp(new Color(0.5f, 0.5f, 0.55f), 0.3f));

            // The delivery bar: filled by the fraction actually delivered, so a
            // node three ingots into six reads as half done from across the
            // panel without anyone reading a number.
            if (node.Required > 0)
            {
                var bar = new Rect2(node.Box.Position + new Vector2(_nodeWidth - 26f, 7f),
                                    new Vector2(18f, 5f));
                DrawRect(bar, new Color(0.08f, 0.09f, 0.11f));
                var fraction = Mathf.Clamp(node.Delivered / (float)node.Required, 0f, 1f);
                if (fraction > 0f)
                    DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * fraction, bar.Size.Y)),
                             new Color(0.45f, 0.80f, 0.52f));
                DrawRect(bar, border, filled: false, width: 1f);
            }
        }

        /// The longest form of a node's name that fits the box. The tier word
        /// goes first when it has to: the row label and the tier stripe already
        /// say "Voltaic", so "Voltaic Metallurgy" losing its first word costs
        /// nothing, while losing its last three letters costs the reader.
        private static string Fit(Font font, NodeView node, float room)
        {
            // Always dropped, not only when it does not fit: half the grid
            // keeping its tier word and half losing it read as two different
            // kinds of node, which is a distinction the data does not have.
            var name = node.Tech.Name;
            var tier = TierNames.GetValueOrDefault(node.Tech.Tier, "");
            if (tier.Length > 0 && name.StartsWith(tier + " "))
                name = name[(tier.Length + 1)..];

            if (font.GetStringSize(name, fontSize: 11).X <= room)
                return name;

            var space = name.IndexOf(' ');
            var shorter = space > 0 ? name[(space + 1)..] : name;
            if (font.GetStringSize(shorter, fontSize: 11).X <= room)
                return shorter;

            while (shorter.Length > 4
                   && font.GetStringSize(shorter + "..", fontSize: 11).X > room)
                shorter = shorter[..^1];

            return shorter + "..";
        }

        /// Cuts text to fit with an ellipsis rather than letting Godot clip it
        /// mid-glyph: "needs Steam Metallur" reads as a bug, "needs Steam
        /// Metall.." reads as an abbreviation.
        private static string Truncate(Font font, string text, int size, float room)
        {
            if (font.GetStringSize(text, fontSize: size).X <= room) return text;
            var cut = text;
            while (cut.Length > 4 && font.GetStringSize(cut + "..", fontSize: size).X > room)
                cut = cut[..^1];
            return cut + "..";
        }

        /// Names are long ("Voltaic Machine Hull") and a node is ~130px wide,
        /// so the tier word -- which the row label already carries -- is dropped
        /// rather than letting the name clip mid-word.
        private static string Short(string name)
        {
            var space = name.IndexOf(' ');
            return space > 0 && !char.IsDigit(name[0]) ? name[(space + 1)..] : name;
        }
    }
}
