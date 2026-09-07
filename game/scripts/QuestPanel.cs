using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;

namespace Game;

/// What research wants next, and how far the Seed is.
///
/// The quest list is not authored: it is `data/techs.json` read through
/// `Research` (ADR 0023), so this panel cannot drift out of step with what the
/// game actually gates. It shows the open objectives -- the techs whose
/// prerequisites are met -- rather than all 32, because a list of everything
/// that is not yet possible is not a list of things to do.
///
/// Owns no state but the premise flag. Everything shown is read from the world
/// each time it opens, so holding it open cannot change the simulation.
public sealed partial class QuestPanel : PanelContainer
{
    private Label _title = null!;
    private Label _premise = null!;
    private RichTextLabel _body = null!;
    private Label _hint = null!;

    private World _world = null!;

    /// Item id -> the name a player reads. The objectives come out of the data
    /// as ids ("stm_machine_hull"), and a quest list written in ids reads as a
    /// debug dump rather than as something to go and do.
    private static readonly Dictionary<string, string> ItemNames =
        Sim.Data.Catalogue.Instance.Data.Items.ToDictionary(i => i.Id, i => i.Name);

    private static string Named(string itemId) => ItemNames.GetValueOrDefault(itemId, itemId);

    /// The three sentences, shown once on a new game and never again. Kept
    /// here rather than in a file because it is three sentences and a data
    /// file for it would be a place for it to go missing.
    private const string Premise =
        "Your probe came apart on entry. The fabricator survived; nothing else did.\n" +
        "A Von Neumann probe exists to make another Von Neumann probe. Yours cannot, yet -- " +
        "the machine that builds Seeds is itself a Seed's worth of industry. So you start with " +
        "what a lander carries: a survey device, your hands, and enough stone to make a bench.\n" +
        "Build the industry. Build the Seed. Send it on.";

    public bool IsShowing => Visible;

    public override void _Ready()
    {
        _title = GetNode<Label>("Margin/Rows/Title");
        _premise = GetNode<Label>("Margin/Rows/Premise");
        _body = GetNode<RichTextLabel>("Margin/Rows/Body");
        _hint = GetNode<Label>("Margin/Rows/Hint");
        GetNode<Button>("Margin/Rows/Buttons/Close").Pressed += Close;
        Visible = false;
    }

    public void Bind(World world) => _world = world;

    /// Opens with the premise above the objectives. Called once, on a new game.
    public void OpenWithPremise()
    {
        _premise.Text = Premise;
        _premise.Visible = true;
        Open();
    }

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        Visible = false;
        // The premise is a one-shot: it belongs to the moment the game starts,
        // and re-reading it every time the objective list is opened would turn
        // three sentences into wallpaper.
        _premise.Visible = false;
    }

    /// The text the panel is showing, so the headless smoke run can assert it
    /// says something -- a panel that renders an empty box photographs the same
    /// as one that renders correctly.
    public string BodyText => _body.Text;

    public void Refresh()
    {
        var research = _world.Research;

        if (research is null)
        {
            _title.Text = "Objectives";
            _body.Text = "This world has no research.";
            _hint.Text = "";
            return;
        }

        var done = research.AllTechs.Count(t => research.IsTechUnlocked(t.Id));
        _title.Text = $"Objectives  --  {done}/{research.AllTechs.Count} researched";

        if (research.SeedDelivered)
        {
            _body.Text = "The Seed is built and away.\n\n" +
                         "It will find somewhere to land, come apart on entry, and start again.";
            _hint.Text = "";
            return;
        }

        var lines = new List<string>();

        foreach (var objective in research.Objectives)
        {
            var needs = string.Join(", ", objective.Needs.Select(
                n => $"{n.Delivered}/{n.Required} {Named(n.Item)}"));

            // What it opens is the reason to do it, so it is on the same line
            // as the cost. A count rather than a list: 121 recipe names is not
            // a reward, it is a wall of text.
            var reward = objective.Unlocks.Count == 1
                ? "1 recipe"
                : $"{objective.Unlocks.Count} recipes";

            lines.Add(objective.Id == Research.SeedObjective
                ? $"[b]{objective.Name}[/b] -- {needs}"
                : $"[b]{objective.Name}[/b] -- deliver {needs}  ->  unlocks {reward}");
        }

        if (lines.Count == 0)
            lines.Add("Nothing is open. Every tech is researched.");

        // The Seed is always named, even while it is far off, because it is the
        // only reason any of the rest is being done.
        var seed = research.SeedObjectiveNow();
        var seedLeft = seed.Needs.Count(n => !n.Met);
        lines.Add("");
        lines.Add(seed.Needs.Count == 0
            ? "[b]Von Neumann Seed[/b] -- no goal in this data"
            : $"[b]Von Neumann Seed[/b] -- {seed.Needs.Count - seedLeft}/{seed.Needs.Count} " +
              "assemblies delivered: " +
              string.Join(", ", seed.Needs.Select(n => $"{n.Delivered}/{n.Required} {Named(n.Item)}")));

        _body.Text = string.Join("\n", lines);
        _hint.Text = "Deliver into the Uplink -- by hand, by inserter or by belt.";
    }

    public override void _Process(double delta)
    {
        if (Visible) Refresh();
    }
}
