namespace Sim;

/// A team: a name, and the progression that belongs to it.
///
/// Progression is team-owned rather than world-owned (ADR 0036). Several teams
/// can share one world, each racing the same tech ladder with its own unlocks,
/// and every placeable on the map records which team paid for it. There is no
/// trade between teams and no shared research: two teams in one world are two
/// factories that happen to be on the same map.
///
/// A `Team` is deliberately thin. Everything a team *owns* is stored where it
/// already lived -- research here, buildings by anchor in `World`, players in
/// the roster -- rather than being gathered into a per-team object graph. That
/// keeps a one-team world byte-for-byte the world it was before teams existed,
/// which is the property that made adding this to 293 call sites tractable.
public sealed class Team
{
    /// The id no team has. Used for a building placed outside `TryBuild`:
    /// headless throughput analysis and the demo world put machines on the map
    /// directly, and inventing an owner for those would let a played game's
    /// ownership rules be silently satisfied by something nobody built.
    public const int NoTeam = -1;

    /// Stable for the life of the world, and equal to the index in
    /// `World.Teams`. Stable because ownership is stored as this number on
    /// every building and in every save: a team id that shifted when a team
    /// was added would hand one team's factory to another.
    ///
    /// Teams are never removed for exactly that reason. An empty team costs a
    /// name and an unlock list.
    public int Id { get; }

    public string Name { get; set; }

    /// Which techs this team has unlocked and what its Uplinks are still
    /// waiting for. Nullable, and null means an ungated team: headless
    /// analysis worlds want the whole recipe graph, a played game always has
    /// one. Per team rather than per world, which is the whole point.
    public Research? Research { get; set; }

    private int _unattendedDeliveries;

    /// How many items have reached this team's research without passing
    /// through a player's hands -- the proof that its first automated line ran
    /// (docs/0030). Team-owned like the rest of progression: one team's belt
    /// feeding its own Uplink must not tick over the other team's milestone.
    public int UnattendedDeliveries => _unattendedDeliveries;

    /// Not a setter: restoring a count and scoring one are different
    /// operations, and a settable property invites a caller to fake a
    /// milestone.
    public void RestoreUnattendedDeliveries(int count) => _unattendedDeliveries = count;

    internal void ScoreUnattendedDelivery(int count) => _unattendedDeliveries += count;

    public Team(int id, string name, Research? research = null)
    {
        Id = id;
        Name = name;
        Research = research;
    }
}
