using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;

namespace Game;

/// The survey device, finally given a face.
///
/// `Prospector` has existed since worldgen landed and nothing in the game ever
/// showed it: the player carried a device whose whole job is answering "what is
/// near me and which way is it" and had no way to ask. The opening was therefore
/// walking in a direction until something turned up, which is the wandering the
/// prospector was written to remove.
///
/// Two things are on every row and both matter. **Distance and heading** make a
/// walk a decision rather than a gamble. **Whether the resource is usable now**
/// is the half that F2 was actually about (ADR 0026): before it, the top of the
/// list was halite on 18 of 20 seeds, and the list said nothing to distinguish
/// halite from the copper the player was looking for.
///
/// Unusable hits are shown, not hidden. Knowing there is bauxite 60 tiles east
/// long before anything can smelt it is exactly the kind of thing a player plans
/// around, and a list that quietly dropped it would be lying by omission.
public sealed partial class SurveyPanel : PanelContainer
{
    private RichTextLabel _body = null!;
    private Label _hint = null!;

    private World _world = null!;

    /// Where the survey is taken from. The camera, not the origin: the device is
    /// carried, so what it reports has to change as the player moves.
    private Vector2I _from;

    private static readonly Dictionary<string, string> ItemNames =
        Sim.Data.Catalogue.Instance.Data.Items.ToDictionary(i => i.Id, i => i.Name);

    public bool IsShowing => Visible;

    /// The rendered list, so the headless smoke run can report it without a
    /// display. Every visual defect this project has had was found by looking,
    /// and the ones a screenshot cannot show are found by printing.
    public string BodyText => _body.Text;

    /// Counted from the last refresh, for the same reason: a headless run has
    /// no panel to look at, and "3 rows, 2 usable" is the fact a screenshot
    /// would be checked for anyway.
    public int RowCount { get; private set; }
    public int UsableCount { get; private set; }

    public override void _Ready()
    {
        _body = GetNode<RichTextLabel>("Margin/Rows/Body");
        _hint = GetNode<Label>("Margin/Rows/Hint");
        GetNode<Button>("Margin/Rows/Buttons/Close").Pressed += Close;
        Visible = false;
    }

    public void Bind(World world) => _world = world;

    public void Open(int fromX, int fromY)
    {
        _from = new Vector2I(fromX, fromY);
        Visible = true;
        Refresh();
    }

    public void Close() => Visible = false;

    public void Refresh()
    {
        var usable = _world.Research?.ConsumableNow(_world.Items);
        var hits = new Prospector().Scan(_world.Ground.Gen, _from.X, _from.Y, usable);

        RowCount = hits.Count;
        UsableCount = hits.Count(h => h.Usable);

        var rows = new List<string>();
        foreach (var hit in hits)
        {
            var name = ItemNames.GetValueOrDefault(_world.Items.GetName(hit.Item),
                                                   _world.Items.GetName(hit.Item));
            var heading = Prospector.HeadingTo(_from.X, _from.Y, hit);

            // Colour carries the same fact as the word, never only the colour:
            // "usable" has to survive a player who cannot tell the two greens
            // apart, and it has to survive a screenshot read at a glance.
            rows.Add(hit.Usable
                ? $"[color=#8fdc7a]{name}[/color] -- {hit.Distance} tiles, {Compass(heading)} " +
                  $"({heading} deg), {hit.Amount} units -- [b]usable now[/b]"
                : $"[color=#9aa2ad]{name}[/color] -- {hit.Distance} tiles, {Compass(heading)} " +
                  $"({heading} deg), {hit.Amount} units -- nothing you can build uses it yet");
        }

        _body.Text = rows.Count > 0
            ? string.Join("\n", rows)
            : "Nothing within range. Walk, and survey again.";

        var reach = hits.Count == 0 ? 0 : hits.Count(h => h.Usable);
        _hint.Text = $"Surveyed from {_from.X},{_from.Y} -- {hits.Count} within " +
                     $"{new Prospector().Radius} tiles, {reach} usable now.";
    }

    /// Eight points, because "037 degrees" is a bearing and "NE" is a direction
    /// you can walk in. Both are shown: the degrees are what a second survey
    /// from further along is compared against.
    private static string Compass(int degrees)
    {
        string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return points[(int)((degrees + 22.5) / 45) % 8];
    }
}
