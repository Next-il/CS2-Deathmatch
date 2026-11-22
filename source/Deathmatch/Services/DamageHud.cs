using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

using PanoramaManager;

namespace Deathmatch.Services;

/// <summary>What one viewer's death card says. Built by the plugin (Functions/DamageCard.cs), drawn by <see cref="DamageHud"/>.</summary>
public sealed class DamageCard
{
    public string Eyebrow = "";
    public string Title = "";

    /// <summary>A name from <see cref="DamageHud.Weapons"/>, or null for no art.</summary>
    public string? Weapon;
    public bool Headshot;

    /// <summary>The one line under the head: plain, bold, plain, bold, ... (at most 7 segments).</summary>
    public List<string> Line = [];
}

/// <summary>
/// The death card: "Killed by kobi [deagle] [hs]" and one line, "You dealt 54 in 2 hits. kobi has 46 HP
/// left.", instead of the "You dealt X damage to player Y in N hits" / "You didn't deal any damage" chat
/// lines. The layout is K4-DamageInfo's damage_hud (panorama/layout/custom_game/damage_hud.xml in the
/// workshop addon), used as is in its compact (deathmatch) form: this is K4-DamageInfo's
/// src/Hud/DamageHud.cs with the same root id, variables and classes, minus the table and the bars.
///
/// <para><b>One layout, opened once per player and left open.</b> Its root is an invisible full-screen
/// panel; the card inside carries its own "show". The card is "hidden" (collapsed) while not in use and
/// un-collapsed a moment before it shows, so it always has a resting place to slide in from.</para>
///
/// <para><b>Everything is drawn from state.</b> PanoramaManager rebuilds a player's panel by itself after a
/// respawn, a round restart or a map change, and drops every per-viewer variable and class on the way
/// (PanelAction.Restored). Each viewer's card is a plain record here and <see cref="Restore"/> writes it
/// back. Every timed step (the entry, the exit) is a time in that record, run by <see cref="Tick"/>, so
/// nothing is left behind in a timer closure.</para>
///
/// <para><b>One damage card per server.</b> Dialog variables are keyed by the root id, so K4-DamageInfo's
/// own card ("DamageRoot" too) on the same server would share this one's text and draw a second card.</para>
/// </summary>
public sealed class DamageHud : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/damage_hud.vxml_c";
    private const string RootId = "DamageRoot";
    private const string CardId = "dmg_card";
    private const string WeaponId = "dmg_wpn";

    /// <summary>dmg_s0..dmg_s6 in the layout: plain, bold, plain, bold, ...</summary>
    public const int LineSegments = 7;

    /// <summary>Un-collapse first, then "show": a transition needs the client to have seen the card at its
    /// resting offset (ToastService found one server frame is not enough).</summary>
    private const float EntrySeconds = 0.06f;

    /// <summary>The exit transition, before the card collapses or comes back with new content.</summary>
    private const float ExitSeconds = 0.35f;

    /// <summary>The wpn- classes damage_hud.css ships: the names CS2 puts in player_death's "weapon".</summary>
    public static readonly HashSet<string> Weapons =
    [
        "ak47", "aug", "awp", "bizon", "cz75a", "deagle", "elite", "famas", "fiveseven", "g3sg1", "galilar", "glock",
        "hkp2000", "m249", "m4a1", "m4a1_silencer", "mac10", "mag7", "mp5sd", "mp7", "mp9", "negev", "nova", "p250",
        "p90", "revolver", "sawedoff", "scar20", "sg556", "ssg08", "taser", "tec9", "ump45", "usp_silencer", "xm1014",
        "hegrenade", "inferno", "molotov", "incgrenade", "flashbang", "smokegrenade", "decoy", "planted_c4", "knife",
        "knife_t", "bayonet", "knife_bowie", "knife_butterfly", "knife_canis", "knife_cord", "knife_css",
        "knife_falchion", "knife_flip", "knife_gut", "knife_gypsy_jackknife", "knife_karambit", "knife_kukri",
        "knife_m9_bayonet", "knife_outdoor", "knife_push", "knife_skeleton", "knife_stiletto", "knife_survival_bowie",
        "knife_tactical", "knife_twinblade", "knife_ursus", "knife_widowmaker", "knifegg", "world", "trigger_hurt",
        "prop_exploding_barrel",
    ];

    private sealed class Viewer
    {
        /// <summary>What the viewer should see; null when nothing.</summary>
        public DamageCard? Card;

        /// <summary>"show" is on.</summary>
        public bool Shown;

        /// <summary>"hidden" is on (the layout authors it so).</summary>
        public bool Collapsed = true;

        /// <summary>When to write the card and turn "show" on; -1 when nothing is pending.</summary>
        public float ShowAt = -1;

        /// <summary>When to collapse a card that has finished leaving; -1 when nothing is pending.</summary>
        public float CollapseAt = -1;

        /// <summary>The wpn- class on dmg_wpn as last written.</summary>
        public string? WeaponClass;

        /// <summary>Forgets every class the client may hold: after a rebuild or a fresh open they are gone.</summary>
        public void ResetClientState()
        {
            Shown = false;
            Collapsed = true;
            ShowAt = -1;
            CollapseAt = -1;
            WeaponClass = null;
        }
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Viewer> _viewers = [];
    private PanelHandle? _panel;

    public DamageHud(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null)
            return;

        try
        {
            Panorama.Init(_plugin);
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by panel id
                RootPanelId = RootId,
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
                SpectatingMessage = null,
            });
            // Bottom center, above the spectator bar: K4-DamageInfo's default placement
            _panel.SetVariant("pos", "bottom");
            _panel.OnEvent += OnPanelEvent;
            _plugin.Logger.LogInformation("Deathmatch damage card ready");
        }
        catch (Exception ex)
        {
            // The plugin works without it: the caller prints the chat lines when Enabled is false
            _plugin.Logger.LogWarning("Deathmatch damage card failed to start, using the chat lines instead: {Message}", ex.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Restored && e.Player.IsValid)
            Restore(e.Player);
    }

    // ------------------------------------------------------------------ viewers

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

    private Viewer ViewerOf(CCSPlayerController player)
    {
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[player.Slot] = viewer;
        }

        return viewer;
    }

    /// <summary>Opens the player's session if it is not open yet. A fresh open starts from the layout as authored.</summary>
    private bool EnsureOpen(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player))
            return false;

        if (!_panel.IsOpenFor(player))
        {
            _panel.Open(player);
            if (!_panel.IsOpenFor(player))
                return false;

            ViewerOf(player).ResetClientState();
        }

        return true;
    }

    public void Forget(int slot)
    {
        _viewers.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>Map end: the clock restarts on the next map, so every stored time is meaningless now.</summary>
    public void Clear()
    {
        foreach (var viewer in _viewers.Values)
        {
            viewer.Card = null;
            viewer.ResetClientState();
        }
    }

    /// <summary>
    /// Writes the card back for one player, from state. Called after the library rebuilt the panel: every
    /// class it may have lost is written again.
    /// </summary>
    public void Restore(CCSPlayerController player)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
            return;

        var viewer = ViewerOf(player);

        viewer.ResetClientState();
        Cls(player, CardId, "show", false);

        if (viewer.Card == null)
        {
            Cls(player, CardId, "hidden", true);
            return;
        }

        Cls(player, CardId, "hidden", false);
        viewer.Collapsed = false;
        viewer.ShowAt = Server.CurrentTime + EntrySeconds;
    }

    // ------------------------------------------------------------------ showing and hiding

    /// <summary>
    /// Shows <paramref name="card"/> to the player until <see cref="Hide"/> (the respawn). A card already on
    /// screen leaves first and comes back with the new content. Returns false when the card cannot be shown
    /// to this player, so the caller prints its chat line.
    /// </summary>
    public bool Show(CCSPlayerController player, DamageCard card)
    {
        if (!EnsureOpen(player))
            return false;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        viewer.Card = card;

        if (viewer.Shown)
        {
            Cls(player, CardId, "show", false);
            viewer.Shown = false;
            viewer.ShowAt = now + ExitSeconds;
        }
        else if (viewer.Collapsed)
        {
            Cls(player, CardId, "hidden", false);
            viewer.Collapsed = false;
            viewer.ShowAt = now + EntrySeconds;
        }
        else
        {
            // Still leaving, or about to enter: come in when that is done
            viewer.ShowAt = viewer.CollapseAt >= 0 ? viewer.CollapseAt : viewer.ShowAt >= 0 ? viewer.ShowAt : now + EntrySeconds;
        }

        viewer.CollapseAt = -1;
        return true;
    }

    /// <summary>Takes the player's card away (they respawned, they went to spectators).</summary>
    public void Hide(CCSPlayerController player)
    {
        if (_viewers.TryGetValue(player.Slot, out var viewer))
            Hide(player, viewer, Server.CurrentTime);
    }

    private void Hide(CCSPlayerController player, Viewer viewer, float now)
    {
        viewer.Card = null;
        viewer.ShowAt = -1;

        if (_panel == null || !_panel.IsOpenFor(player))
        {
            viewer.Shown = false;
            return;
        }

        if (viewer.Shown)
        {
            // The content stays as it is, so the card fades out showing what it showed
            Cls(player, CardId, "show", false);
            viewer.Shown = false;
            viewer.CollapseAt = now + ExitSeconds;
        }
        else if (!viewer.Collapsed && viewer.CollapseAt < 0)
        {
            // Between a leave and a come-back: let the fade finish, then collapse
            viewer.CollapseAt = now + ExitSeconds;
        }
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>Runs every timed step. The plugin calls this every server tick.</summary>
    public void Tick()
    {
        if (_panel == null || _viewers.Count == 0)
            return;

        var now = Server.CurrentTime;

        foreach (var (slot, viewer) in _viewers)
        {
            if (viewer.Card == null && viewer.CollapseAt < 0)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || !_panel.IsOpenFor(player))
                continue;

            if (viewer.CollapseAt >= 0 && now >= viewer.CollapseAt)
            {
                viewer.CollapseAt = -1;
                if (viewer.Card == null)
                {
                    Cls(player, CardId, "hidden", true);
                    viewer.Collapsed = true;
                }
            }

            var card = viewer.Card;
            if (card == null)
                continue;

            if (viewer.ShowAt >= 0 && now >= viewer.ShowAt)
            {
                viewer.ShowAt = -1;
                Draw(player, viewer, card);
                Cls(player, CardId, "show", true);
                viewer.Shown = true;
            }
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Writes the card's text and state: the killed head and the compact line, never the table.</summary>
    private void Draw(CCSPlayerController player, Viewer viewer, DamageCard card)
    {
        Cls(player, CardId, "st-killed", true);
        Cls(player, CardId, "compact", true);
        Cls(player, CardId, "hs", card.Headshot);

        Set(player, "dmg_eyebrow", card.Eyebrow);
        Set(player, "dmg_title", card.Title);

        var weapon = card.Weapon is { } w && Weapons.Contains(w) ? $"wpn-{w}" : null;
        if (viewer.WeaponClass != weapon)
        {
            if (viewer.WeaponClass != null)
                Cls(player, WeaponId, viewer.WeaponClass, false);
            if (weapon != null)
                Cls(player, WeaponId, weapon, true);
            viewer.WeaponClass = weapon;
        }
        Cls(player, WeaponId, "on", weapon != null);

        DrawLine(player, card.Line);
    }

    /// <summary>
    /// The compact line, one segment per label. A label next to a label has no word space, so every segment
    /// after a visible one gets "sp" unless it starts with punctuation (". kobi has" after "2 hits").
    /// </summary>
    private void DrawLine(CCSPlayerController player, List<string> line)
    {
        var anyBefore = false;

        for (var i = 0; i < LineSegments; i++)
        {
            var text = i < line.Count ? line[i].Trim() : "";
            var id = $"dmg_s{i}";

            Set(player, id, text);
            Cls(player, id, "hidden", text.Length == 0);
            Cls(player, id, "sp", anyBefore && text.Length > 0 && !char.IsPunctuation(text[0]));

            if (text.Length > 0)
                anyBefore = true;
        }
    }

    private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);
}
