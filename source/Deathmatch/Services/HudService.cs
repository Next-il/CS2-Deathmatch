using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

using PanoramaManager;

namespace Deathmatch.Services;

/// <summary>
/// The Deathmatch HUD: the mode-change banner at the top center, instead of the "New mode starts in
/// 10 seconds!" center hint, "A new mode has started!" and "Starting a new mode type ..." in chat. The
/// layout is hud/panorama/layout/custom_game/deathmatch_hud.xml, shipped in the workshop addon.
///
/// <para><b>One layout, opened once per player and left open.</b> Its root is an invisible full-screen
/// panel and the banner inside carries its own "show", so a banner is a class toggle, never an
/// Open/Close.</para>
///
/// <para><b>Everything is redrawn from state.</b> The library rebuilds a player's panel by itself after a
/// respawn, a round restart or a map change, and drops every per-viewer variable and class on the way
/// (PanelAction.Restored). The banner is the same for everyone, so it is one record here, and
/// <see cref="Redraw"/> writes it back. A banner whose time is up stays hidden.</para>
///
/// <para>Who sees it is the plugin's call (the "Hud Messages" preference and the API's hud switch), passed
/// in as <c>sees</c>. Off when Hud.Enabled is false or the layout failed to spawn: the plugin then keeps
/// its old lines.</para>
/// </summary>
public sealed class HudService : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/deathmatch_hud.vxml_c";

    /// <summary>How long the "New mode" banner stays up.</summary>
    private const float NewModeSeconds = 4.0f;

    /// <summary>The countdown leaves before the new mode comes in, like the review page (10.15 s -> 10.5 s).</summary>
    private const float SwapDelay = 0.35f;

    /// <summary>Every wpn-* class deathmatch_hud.css has. A weapon outside it gets no icon.</summary>
    private static readonly HashSet<string> Icons =
    [
        "ak47", "aug", "awp", "bizon", "cz75a", "deagle", "elite", "famas", "fiveseven", "g3sg1", "galilar",
        "glock", "hkp2000", "m249", "m4a1", "m4a1_silencer", "mac10", "mag7", "mp5sd", "mp7", "mp9", "negev",
        "nova", "p250", "p90", "revolver", "sawedoff", "scar20", "sg556", "ssg08", "tec9", "ump45",
        "usp_silencer", "xm1014", "knife", "knife_t", "taser", "hegrenade", "flashbang", "smokegrenade",
        "molotov", "incgrenade", "decoy", "healthshot",
    ];

    /// <summary>State "next" is the countdown, "new" the mode that just started. Icon is "" for none.</summary>
    private sealed record Banner(string State, string Icon, string Eyebrow, string Title, string Sub, string SideKey, int Seconds);

    private sealed class Viewer
    {
        /// <summary>The wpn-* class this viewer has on, so a redraw every second does not rewrite the palette.</summary>
        public string? Icon;
        public bool PopA;
    }

    private readonly Deathmatch _plugin;
    private readonly Dictionary<int, Viewer> _viewers = [];
    private PanelHandle? _panel;

    private Banner? _banner;
    private bool _bannerUp;
    private float _bannerUntil;
    private int _generation;
    private Func<CCSPlayerController, bool> _sees = _ => true;

    public HudService(Deathmatch plugin)
    {
        _plugin = plugin;
    }

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null)
        {
            return;
        }

        try
        {
            Panorama.Init(_plugin);
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by panel id
                RootPanelId = "DeathmatchRoot",
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnPanelEvent;
            _plugin.Logger.LogInformation("Deathmatch HUD ready");
        }
        catch (Exception ex)
        {
            // The plugin works without its HUD: every caller falls back to the old lines when Enabled is false
            _plugin.Logger.LogWarning("Deathmatch HUD failed to start, using the center and chat lines instead: {Message}", ex.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
        _banner = null;
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Restored && e.Player.IsValid)
        {
            // The rebuilt panel has none of this viewer's classes any more
            if (_viewers.TryGetValue(e.Player.Slot, out var viewer))
            {
                viewer.Icon = null;
            }

            Redraw(e.Player);
        }
    }

    // ------------------------------------------------------------------ viewers

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

    private static IEnumerable<CCSPlayerController> Humans() => Utilities.GetPlayers().Where(IsHuman);

    private Viewer ViewerOf(CCSPlayerController player)
    {
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[player.Slot] = viewer;
        }

        return viewer;
    }

    /// <summary>Opens the player's session if it is not open yet, which also draws what they should see.</summary>
    private bool EnsureOpen(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player))
        {
            return false;
        }

        if (!_panel.IsOpenFor(player))
        {
            _viewers.Remove(player.Slot);
            _panel.Open(player);
            Redraw(player);
        }

        return _panel.IsOpenFor(player);
    }

    public void OnPlayerDisconnect(int slot)
    {
        _viewers.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>Drops the banner at a map change, so a countdown cut short by the map end is not redrawn on the next map.</summary>
    public void Reset()
    {
        _generation++;
        _banner = null;
        _bannerUp = false;
    }

    /// <summary>Writes the banner back for one player, from state. Called on open and after the library rebuilt the panel.</summary>
    public void Redraw(CCSPlayerController player)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
        {
            return;
        }

        var up = _banner != null && _bannerUp && Server.CurrentTime < _bannerUntil && _sees(player);
        Apply(player, _banner, up, false);
    }

    // ------------------------------------------------------------------ the countdown

    /// <summary>
    /// The countdown to the next mode, once a second while it runs. The first call brings the banner in,
    /// the next ones change the number and pop it.
    /// </summary>
    public void Countdown(string? weapon, string modeName, int seconds, Func<CCSPlayerController, bool> sees)
    {
        if (_panel == null)
        {
            return;
        }

        _sees = sees;
        var running = _banner is { State: "next" } && _bannerUp && _banner.Title == modeName;
        var card = new Banner("next", IconOf(weapon),
            Text("Hud.Banner.NextMode", "Next mode"),
            modeName,
            Text("Hud.Banner.LoadoutChanges", "Your loadout changes when the timer ends."),
            Text("Hud.Banner.In", "IN"),
            seconds);

        if (!running)
        {
            _generation++;
        }

        _banner = card;
        _bannerUp = true;
        _bannerUntil = float.MaxValue;

        foreach (var player in Humans())
        {
            if (!sees(player))
            {
                Hide(player);
                continue;
            }

            if (!EnsureOpen(player))
            {
                continue;
            }

            if (running)
            {
                Apply(player, card, true, true);
                continue;
            }

            // Off first, so a banner that is still up (a new mode a moment ago) animates in again
            Apply(player, card, false, false);
            var slot = player.Slot;
            Server.NextFrame(() =>
            {
                var again = Utilities.GetPlayerFromSlot(slot);
                if (again != null && _banner == card && _bannerUp)
                {
                    Apply(again, card, true, false);
                }
            });
        }
    }

    /// <summary>Takes a countdown away that ended without a mode change (the timer was moved, the hud was switched off).</summary>
    public void EndCountdown()
    {
        if (_panel == null || _banner is not { State: "next" })
        {
            return;
        }

        _banner = null;
        _bannerUp = false;
        _generation++;

        foreach (var player in Humans())
        {
            Hide(player);
        }
    }

    // ------------------------------------------------------------------ the new mode

    /// <summary>
    /// The mode that just started, for 4 seconds, once the countdown has left. Returns the slots that get
    /// the banner: everyone else still needs the chat line.
    /// </summary>
    public HashSet<int> ModeStarted(string? weapon, string modeName, string sub, Func<CCSPlayerController, bool> sees)
    {
        var shown = new HashSet<int>();
        if (_panel == null)
        {
            return shown;
        }

        _sees = sees;
        var generation = ++_generation;
        var card = new Banner("new", IconOf(weapon), Text("Hud.Banner.NewMode", "New mode"), modeName, sub, "", 0);

        // The countdown leaves first
        foreach (var player in Humans())
        {
            Hide(player);
            if (sees(player))
            {
                shown.Add(player.Slot);
            }
        }

        _banner = card;
        _bannerUp = false;
        _bannerUntil = Server.CurrentTime + SwapDelay + NewModeSeconds;

        _plugin.AddTimer(SwapDelay, () =>
        {
            if (generation != _generation || _panel == null)
            {
                return;
            }

            _bannerUp = true;
            foreach (var player in Humans())
            {
                if (!sees(player))
                {
                    continue;
                }

                if (EnsureOpen(player))
                {
                    Apply(player, card, true, false);
                }
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        _plugin.AddTimer(SwapDelay + NewModeSeconds, () =>
        {
            if (generation != _generation || _panel == null)
            {
                return;
            }

            _bannerUp = false;
            foreach (var player in Humans())
            {
                Hide(player);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return shown;
    }

    // ------------------------------------------------------------------ drawing

    private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);

    private void Hide(CCSPlayerController player)
    {
        if (_panel != null && _panel.IsOpenFor(player))
        {
            Cls(player, "dm_rb", "show", false);
        }
    }

    private void Apply(CCSPlayerController player, Banner? card, bool show, bool pop)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
        {
            return;
        }

        if (card != null)
        {
            var viewer = ViewerOf(player);

            Set(player, "dm_rb_eyebrow", card.Eyebrow);
            Set(player, "dm_rb_title", card.Title);
            Set(player, "dm_rb_sub", card.Sub);
            Set(player, "dm_rb_side_k", card.SideKey);
            Set(player, "dm_rb_side_v", card.State == "next" ? card.Seconds.ToString() : "");
            Cls(player, "dm_rb_sub", "hidden", card.Sub.Length == 0);
            Cls(player, "dm_rb_side", "hidden", card.State != "next");
            Cls(player, "dm_rb", "no-wpn", card.Icon.Length == 0);

            if (viewer.Icon != card.Icon)
            {
                if (!string.IsNullOrEmpty(viewer.Icon))
                {
                    Cls(player, "dm_rb_art", "wpn-" + viewer.Icon, false);
                }

                if (card.Icon.Length > 0)
                {
                    Cls(player, "dm_rb_art", "wpn-" + card.Icon, true);
                }

                viewer.Icon = card.Icon;
            }

            if (pop)
            {
                // Ping-pong: the only restart a class diff can carry
                viewer.PopA = !viewer.PopA;
                Cls(player, "dm_rb_side_v", "pop-a", viewer.PopA);
                Cls(player, "dm_rb_side_v", "pop-b", !viewer.PopA);
            }
        }

        Cls(player, "dm_rb", "show", show && card != null);
    }

    /// <summary>"weapon_m4a1_silencer" -> "m4a1_silencer", any knife skin -> "knife", "" when the palette has no such icon.</summary>
    private static string IconOf(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon))
        {
            return "";
        }

        var name = weapon.Trim().ToLowerInvariant();
        if (name.StartsWith("weapon_"))
        {
            name = name["weapon_".Length..];
        }

        if (name != "knife_t" && (name.StartsWith("knife") || name == "bayonet"))
        {
            name = "knife";
        }

        return Icons.Contains(name) ? name : "";
    }

    // ------------------------------------------------------------------ text

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    public string Text(string key, string fallback, params object[] args)
    {
        var text = _plugin.Localizer[key, args];
        return text.ResourceNotFound ? string.Format(fallback, args) : text.Value;
    }
}
