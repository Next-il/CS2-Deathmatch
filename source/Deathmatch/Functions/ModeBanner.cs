using CounterStrikeSharp.API.Core;
using DeathmatchAPI.Helpers;

namespace Deathmatch;

/// <summary>
/// The plugin's side of the mode-change banner (Services/HudService.cs): when it runs, who sees it, and
/// what it says. The banner replaces the "New mode starts in N seconds!" and "A new mode has started!"
/// center lines and the "Starting a new mode type ..." chat line; with Hud.Enabled false they come back.
/// </summary>
public partial class Deathmatch
{
    /// <summary>Null on the instance the API capability hands out, which never ran Load.</summary>
    public Services.HudService? Hud { get; private set; }

    /// <summary>
    /// Once a second, after RemainingTime moved: the countdown while the next mode is NewModeCountdown
    /// seconds away or less, nothing otherwise. Same conditions as the old center hint, plus one: no
    /// countdown to the mode that is already running.
    /// </summary>
    private void UpdateModeBanner()
    {
        if (Hud is not { Enabled: true })
            return;

        var countdown = Config.Gameplay.NewModeCountdown;
        // A "next mode" that is the mode already running (one mode only) changes nothing, so it gets no countdown
        if (countdown > 0 && RemainingTime > 0 && RemainingTime <= countdown && !Config.General.HideModeRemainingTime
            && NextMode.ToString() != ActiveCustomMode && Config.CustomModes.TryGetValue(NextMode.ToString(), out var next))
        {
            Hud.Countdown(ModeIcon(NextMode.ToString(), next), next.Name, RemainingTime, SeesModeBanner);
        }
        else
        {
            Hud.EndCountdown();
        }
    }

    /// <summary>The same players the old center lines reached: hud messages on (the player's preference and the API switch).</summary>
    private bool SeesModeBanner(CCSPlayerController player)
    {
        if (!VisibleHud || !playerData.TryGetValue(player.Slot, out var data))
            return false;

        var pref = Config.PlayersPreferences.HudMessages;
        return !pref.Enabled || GetPrefsValue(data, "HudMessages", pref.DefaultValue);
    }

    /// <summary>The weapon on a mode's banner: Hud."Mode Icons" first, else the AK-47 if the mode has it, its first primary, its first secondary, the knife.</summary>
    private string ModeIcon(string modeId, ModeData mode)
    {
        if (Config.Hud.ModeIcons.TryGetValue(modeId, out var forced) && !string.IsNullOrWhiteSpace(forced))
            return forced;

        var primaries = mode.PrimaryWeapons ?? new List<string>();
        if (primaries.Count > 0)
            return primaries.Contains("weapon_ak47") ? "weapon_ak47" : primaries[0];

        var secondaries = mode.SecondaryWeapons ?? new List<string>();
        if (secondaries.Count > 0)
            return secondaries[0];

        return "weapon_knife";
    }

    /// <summary>"Until the next change in 5 minutes." from the mode's own interval, or nothing when modes never change.</summary>
    private string NextChangeText(ModeData mode)
    {
        if (Hud == null || !Config.Gameplay.IsCustomModes || Config.CustomModes.Count <= 1 || mode.Interval <= 0)
            return "";

        var seconds = mode.Interval;
        if (seconds == 60)
            return Hud.Text("Hud.Banner.UntilOneMinute", "Until the next change in 1 minute.");
        if (seconds % 60 == 0)
            return Hud.Text("Hud.Banner.UntilMinutes", "Until the next change in {0} minutes.", seconds / 60);

        return Hud.Text("Hud.Banner.UntilTime", "Until the next change in {0}.", $"{seconds / 60}:{seconds % 60:D2}");
    }
}
