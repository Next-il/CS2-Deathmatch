using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Deathmatch;

/// <summary>
/// The plugin's side of the death card (Services/DamageHud.cs): when it shows, who sees it, and what it
/// says. On death the victim gets "Killed by kobi [deagle] [hs]" and "You dealt 54 in 2 hits. kobi has 46 HP
/// left." instead of the "You dealt X damage to player Y in N hits" / "You didn't deal any damage to player
/// Y" chat line, with the same numbers and the same "Damage Info" preference. It stays until the respawn.
///
/// <para>The killer's own "You dealt X damage to player Y" line stays in chat: the card is a death card.</para>
///
/// <para>Off (the chat lines exactly as before) with Hud.Enabled or Hud."Damage Card" false, when the
/// layout failed to spawn, and for a player who does not see hud messages (the "Hud Messages" preference
/// and the API's hud switch, the same as the mode banner).</para>
/// </summary>
public partial class Deathmatch
{
    /// <summary>Null on the instance the API capability hands out, which never ran Load.</summary>
    public Services.DamageHud? DamageHud { get; private set; }

    /// <summary>
    /// The victim's card, from player_death (Pre, so the killer's HP is still the HP they had when the kill
    /// landed, before the kill bonus). Returns false when the victim gets the chat line instead.
    /// </summary>
    private bool ShowDamageCard(CCSPlayerController victim, DeathmatchPlayerData data, CCSPlayerController killer, EventPlayerDeath @event)
    {
        if (DamageHud is not { Enabled: true } || !SeesModeBanner(victim))
            return false;

        try
        {
            return DamageHud.Show(victim, BuildDamageCard(data, killer, @event));
        }
        catch (Exception ex)
        {
            // Never in the way of the rest of player_death (the respawn is scheduled after this): chat instead
            Logger.LogWarning("Deathmatch damage card failed, using the chat line: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>"Killed by kobi [deagle] [hs]" and "You dealt 54 in 2 hits. kobi has 46 HP left."</summary>
    private Services.DamageCard BuildDamageCard(DeathmatchPlayerData data, CCSPlayerController killer, EventPlayerDeath @event)
    {
        data.DamageInfo.TryGetValue(killer.Slot, out var given);
        int dealt = given?.Damage ?? 0;
        int hits = given?.Hits ?? 0;
        int health = HealthOf(killer);

        object[] args = [dealt, HitsText(hits), killer.PlayerName, health];

        var line = (dealt > 0, health > 0) switch
        {
            (true, true) => Line("Hud.Damage.Dealt", "You dealt *{0}* in *{1}*. {2} has *{3} HP* left.", args),
            (true, false) => Line("Hud.Damage.DealtDead", "You dealt *{0}* in *{1}*. {2} is dead too.", args),
            (false, true) => Line("Hud.Damage.None", "You dealt no damage. {2} has *{3} HP* left.", args),
            (false, false) => Line("Hud.Damage.NoneDead", "You dealt no damage. {2} is dead too.", args),
        };

        return new Services.DamageCard
        {
            Eyebrow = Phrase("Hud.Damage.KilledBy", "Killed by"),
            Title = killer.PlayerName,
            Weapon = WeaponArt(@event.Weapon),
            Headshot = @event.Headshot,
            Line = line,
        };
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeamDamageCard(EventPlayerTeam @event, GameEventInfo info)
    {
        // Gone to spectators: there is no respawn coming to take the card away
        if (@event.Userid is { IsValid: true } player && @event.Team <= (int)CsTeam.Spectator)
            DamageHud?.Hide(player);

        return HookResult.Continue;
    }

    /// <summary>The killer's HP right now, 0 when they are dead or have no pawn.</summary>
    private static int HealthOf(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        return pawn is { IsValid: true } && player.PawnIsAlive ? Math.Max(0, pawn.Health) : 0;
    }

    /// <summary>player_death's "weapon" as one of damage_hud's wpn- classes, or null for no art.</summary>
    private static string? WeaponArt(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon))
            return null;

        var name = weapon.Trim().ToLowerInvariant();
        if (name.StartsWith("weapon_"))
            name = name["weapon_".Length..];

        if (Services.DamageHud.Weapons.Contains(name))
            return name;

        // A knife the list does not have yet still gets a knife
        return name.StartsWith("knife") || name.Contains("bayonet") ? "knife" : null;
    }

    // ------------------------------------------------------------------ text

    private string HitsText(int hits) =>
        hits == 1 ? string.Format(Phrase("Hud.Damage.Hit", "{0} hit"), hits) : string.Format(Phrase("Hud.Damage.Hits", "{0} hits"), hits);

    /// <summary>A phrase from lang/ as written (not formatted), or the English fallback when the server's lang files don't have it yet.</summary>
    private string Phrase(string key, string fallback)
    {
        var text = Localizer[key];
        return text.ResourceNotFound || string.IsNullOrEmpty(text.Value) || text.Value == key ? fallback : text.Value;
    }

    /// <summary>
    /// A phrase with *bold* parts as the card line's segments: plain, bold, plain, ... The phrase is split
    /// before the names go in, so a '*' in a player's name stays text.
    /// </summary>
    private List<string> Line(string key, string fallback, object[] args)
    {
        List<string> Split(string template)
        {
            var parts = template.Split('*').Select(part => string.Format(part, args)).ToList();
            if (parts.Count > Services.DamageHud.LineSegments)
            {
                // More bold runs than the layout has labels: the rest goes into the last one, plain
                var rest = string.Concat(parts.Skip(Services.DamageHud.LineSegments - 1));
                parts = [.. parts.Take(Services.DamageHud.LineSegments - 1), rest];
            }

            return parts;
        }

        try
        {
            return Split(Phrase(key, fallback));
        }
        catch (FormatException)
        {
            return Split(fallback);
        }
    }
}
