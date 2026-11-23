using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using DeathmatchAPI.Helpers;
using static DeathmatchAPI.Preferences;
using MenuPanel = Deathmatch.Services.MenuHud;

namespace Deathmatch;

/// <summary>
/// The plugin's side of the mouse menu (Services/MenuHud.cs): what it shows and what a click does.
///
///   !gun with no weapon name   the Weapons tab: the current mode's primaries and secondaries as tiles, the saved
///                              choice marked, the limits on each, the mode's rules. Was the allowed-weapons list in chat.
///   !dm, !deathmatch           the Settings tab: every preference in the menu (Sounds, Functions, API modules) with
///                              ON / OFF or option buttons. Was the "Deathmatch Menu" center menu and its sub-menus.
///   css_dm_editor              the Spawns tab (root admins): add CT / T spawns, remove the nearest, save. Was the
///                              "Spawns Editor" center menu.
///
/// <para>A tile goes through <see cref="PickWeapon"/>, the same code as !gun &lt;name&gt;: the same limits, the same
/// saved choice per mode, the same weapons in hand. A preference is written to the same place the old toggles wrote
/// (saved with the rest on disconnect). The answer shows in the footer, not in chat.</para>
///
/// <para>Off (the old center menus and the chat list exactly as before) with Hud.Enabled or Hud."Menu" false, or when
/// the layout failed to spawn.</para>
/// </summary>
public partial class Deathmatch
{
    /// <summary>Null on the instance the API capability hands out, which never ran Load.</summary>
    public MenuPanel? MenuHud { get; private set; }

    /// <summary>The menu option SetupDeathmatchMenus registers for the editor. The HUD has a tab for it instead.</summary>
    private const string SpawnsEditorOption = "Spawns Editor";

    /// <summary>Tile order: rifles, snipers, SMGs, heavy, then pistols. A weapon not listed keeps its config place, after these.</summary>
    private static readonly string[] WeaponOrder =
    [
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_famas", "weapon_galilar", "weapon_aug", "weapon_sg556",
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
        "weapon_mp9", "weapon_mac10", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff", "weapon_m249", "weapon_negev",
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer", "weapon_p250", "weapon_cz75a", "weapon_fiveseven",
        "weapon_tec9", "weapon_elite", "weapon_deagle", "weapon_revolver",
    ];

    private static readonly Regex SettingId = new(@"^dmm_c(\d)_r(\d+)_(?:o(\d+)|(go))$", RegexOptions.Compiled);
    private static readonly Regex Html = new("<[^>]*>", RegexOptions.Compiled);

    public enum WeaponPick { Primary, Secondary, AlreadySet, Restricted, NotInMode }

    /// <summary>What a settings row does when clicked, in the order drawn.</summary>
    private abstract record RowAction;
    private sealed record PrefAction(Preference Preference, List<string> Values) : RowAction;
    private sealed record GoAction(Menu Option) : RowAction;
    private sealed record NoAction : RowAction;

    private sealed class MenuViewer
    {
        public int Tab;

        /// <summary>Opened by the editor command: the Spawns tab shows even without "Display Spawns Editor In Menu".</summary>
        public bool Editor;

        public List<string> Primaries = [];
        public List<string> Secondaries = [];
        public List<RowAction>[] Rows = [[], []];

        public string Status = "";
        public string Tone = "";
    }

    private readonly Dictionary<int, MenuViewer> _menuViewers = [];

    /// <summary>Spawns added or removed since the last save.</summary>
    private bool _spawnsDirty;

    private void InitMenuHud()
    {
        MenuHud = new MenuPanel(this);
        if (!Config.Hud.Enabled || !Config.Hud.Menu)
            return;

        MenuHud.Init();
        MenuHud.Clicked += OnMenuClicked;
        MenuHud.Restored += DrawMenu;
        MenuHud.Closed += slot => _menuViewers.Remove(slot);
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Opens the menu on <paramref name="tab"/>, or closes it when it is already open there (there is no Escape key).
    /// False when the HUD menu is off or could not open: the caller opens the old menu.
    /// </summary>
    private bool OpenMenu(CCSPlayerController player, int tab, bool editor = false)
    {
        if (MenuHud is not { Enabled: true })
            return false;

        if (player is not { IsValid: true, IsBot: false, IsHLTV: false })
            return true;

        if (MenuHud.IsOpenFor(player) && _menuViewers.TryGetValue(player.Slot, out var open))
        {
            open.Editor |= editor;
            if (open.Tab == tab)
                MenuHud.Close(player);
            else
                SwitchTab(player, open, tab);
            return true;
        }

        if (!MenuHud.Open(player))
            return false;

        _menuViewers[player.Slot] = new MenuViewer { Tab = tab, Editor = editor };
        if (tab == MenuPanel.TabSpawns)
            ShowSpawnMarkers();

        DrawMenu(player);
        return true;
    }

    private void SwitchTab(CCSPlayerController player, MenuViewer viewer, int tab)
    {
        if (tab is < 0 or >= MenuPanel.Tabs || (tab == MenuPanel.TabSpawns && !SeesSpawnsTab(player, viewer)))
            return;

        viewer.Tab = tab;
        viewer.Status = "";
        viewer.Tone = "";
        if (tab == MenuPanel.TabSpawns)
            ShowSpawnMarkers();

        DrawMenu(player);
    }

    /// <summary>!gun with no weapon name. False when the HUD menu is off: the chat list instead.</summary>
    private bool OpenWeaponsMenu(CCSPlayerController player) => OpenMenu(player, MenuPanel.TabWeapons);

    /// <summary>!dm. False when the HUD menu is off: the old center menu instead.</summary>
    private bool OpenSettingsMenu(CCSPlayerController player) => OpenMenu(player, MenuPanel.TabSettings);

    /// <summary>The spawns editor: the HUD's Spawns tab, or the old center menu when the HUD menu is off.</summary>
    private void OpenSpawnsEditor(CCSPlayerController player)
    {
        if (!OpenMenu(player, MenuPanel.TabSpawns, editor: true))
            OpenEditorMenu(player);
    }

    /// <summary>The menu is open for this player (the timers skip their center lines then, as for a center menu).</summary>
    private bool MenuOpenFor(CCSPlayerController player) => MenuHud?.IsOpenFor(player) == true;

    private void ForgetMenu(int slot)
    {
        _menuViewers.Remove(slot);
        MenuHud?.Forget(slot);
    }

    // ------------------------------------------------------------------ drawing

    private void DrawMenu(CCSPlayerController player)
    {
        if (MenuHud is not { Enabled: true } || !player.IsValid || !_menuViewers.TryGetValue(player.Slot, out var viewer))
            return;

        if (viewer.Tab == MenuPanel.TabSpawns && !SeesSpawnsTab(player, viewer))
            viewer.Tab = MenuPanel.TabWeapons;

        MenuHud.Draw(player, BuildMenuView(player, viewer));
    }

    /// <summary>Every open menu again: the mode changed, or a pick moved a weapon's count.</summary>
    private void RedrawMenus()
    {
        if (MenuHud is not { Enabled: true } || _menuViewers.Count == 0)
            return;

        foreach (var slot in _menuViewers.Keys.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            // Entries go on PanelAction.Close and on disconnect, never here: mid-rebuild a menu can read as closed
            // for a moment, and its Restored redraw needs the entry
            if (player is { IsValid: true } && MenuHud.IsOpenFor(player))
                DrawMenu(player);
        }
    }

    private Services.MenuView BuildMenuView(CCSPlayerController player, MenuViewer viewer)
    {
        playerData.TryGetValue(player.Slot, out var data);
        var isVip = AdminManager.PlayerHasPermissions(player, Config.PlayersSettings.VIPFlag);

        var view = new Services.MenuView
        {
            Title = MenuText("Hud.Menu.Title", "Deathmatch"),
            Tag = Config.Gameplay.IsFFA ? MenuText("Hud.Menu.Ffa", "Free for all") : MenuText("Hud.Menu.Teams", "Team deathmatch"),
            NowKey = MenuText("Hud.Menu.Mode", "Mode"),
            Now = Plain(ActiveMode.Name),
            TabNames =
            [
                MenuText("Hud.Menu.Tab.Weapons", "Weapons"),
                MenuText("Hud.Menu.Tab.Settings", "Settings"),
                MenuText("Hud.Menu.Tab.Spawns", "Spawns"),
            ],
            Tab = viewer.Tab,
            SpawnsTab = SeesSpawnsTab(player, viewer),
            Status = viewer.Status,
            StatusTone = viewer.Tone,
        };

        // The next mode and the time to it, as long as modes change and the server shows the countdown
        if (Config.Gameplay.IsCustomModes && Config.CustomModes.Count > 1 && !Config.General.HideModeRemainingTime
            && NextMode.ToString() != ActiveCustomMode && Config.CustomModes.TryGetValue(NextMode.ToString(), out var next))
        {
            var left = TimeSpan.FromSeconds(Math.Max(0, RemainingTime));
            view.NextKey = MenuText("Hud.Menu.Next", "Next");
            view.Next = $"{Plain(next.Name)} · {(int)left.TotalMinutes}:{left.Seconds:D2}";
        }

        var firstCmd = Config.CustomCommands.WeaponSelectCmds.Split(',').Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0) ?? "gun";
        view.Command = $"!{firstCmd} ak";
        view.CommandText = MenuText("Hud.Menu.ChatHint", "picks from chat");

        BuildWeapons(view, viewer, player, data, isVip);
        BuildSettings(view, viewer, player, data, isVip);
        BuildSpawns(view);
        return view;
    }

    private void BuildWeapons(Services.MenuView view, MenuViewer viewer, CCSPlayerController player, DeathmatchPlayerData? data, bool isVip)
    {
        var mode = ActiveMode;
        var primaries = mode.PrimaryWeapons ?? [];
        var secondaries = mode.SecondaryWeapons ?? [];
        view.Random = mode.RandomWeapons;

        // The mode's rules
        if (mode.OnlyHS)
            view.Chips.Add((MenuText("Hud.Menu.Chip.Headshot", "Headshot only"), "warn"));
        if (mode.RandomWeapons)
            view.Chips.Add((MenuText("Hud.Menu.Chip.Random", "Random weapons"), "gold"));
        if (mode.Armor == 1)
            view.Chips.Add((MenuText("Hud.Menu.Chip.Kevlar", "Kevlar"), ""));
        else if (mode.Armor == 2)
            view.Chips.Add((MenuText("Hud.Menu.Chip.Helmet", "Kevlar + helmet"), ""));
        if (!mode.KnifeDamage)
            view.Chips.Add((MenuText("Hud.Menu.Chip.NoKnife", "No knife damage"), ""));

        view.AlsoKey = MenuText("Hud.Menu.Also", "Also");
        view.Utilities = (mode.Utilities ?? [])
            .Select(u => u.StartsWith("weapon_") ? u["weapon_".Length..] : u)
            .Where(MenuPanel.Utilities.Contains)
            .Distinct()
            .Take(MenuPanel.UtilitySlots)
            .ToList();

        var noPrimary = data != null && GetPrefsValue(data, "NoPrimary", Config.PlayersPreferences.NoPrimary.DefaultValue);
        if (mode.RandomWeapons)
            view.Note = MenuText("Hud.Menu.Note.Random", "This mode hands out random weapons. Your picks come back with the next mode.");
        else if (primaries.Count == 0 && secondaries.Count == 0)
            view.Note = MenuText("Hud.Menu.Note.Nothing", "No guns to pick in this mode.");
        else if (noPrimary && primaries.Count > 0 && secondaries.Count > 0)
            view.Note = MenuText("Hud.Menu.Note.NoPrimary", "No Primary is on: you spawn with your pistol only. It is in Settings.");

        viewer.Primaries = Ordered(primaries).Take(MenuPanel.PrimarySlots).ToList();
        viewer.Secondaries = Ordered(secondaries).Take(MenuPanel.SecondarySlots).ToList();

        view.Primary = Section(MenuText("Hud.Menu.Primary", "Primary"), viewer.Primaries, primaries, true, data, player.Team, isVip);
        view.Secondary = Section(MenuText("Hud.Menu.Secondary", "Secondary"), viewer.Secondaries, secondaries, false, data, player.Team, isVip);
    }

    private Services.MenuSection Section(string head, List<string> tiles, List<string> modeList, bool primary,
        DeathmatchPlayerData? data, CsTeam team, bool isVip)
    {
        var random = ActiveMode.RandomWeapons;
        var saved = data == null ? null : (primary ? data.PrimaryWeapon : data.SecondaryWeapon).GetValueOrDefault(ActiveCustomMode);

        var section = new Services.MenuSection
        {
            Visible = tiles.Count > 0,
            Head = head,
            SavedKey = MenuText("Hud.Menu.Saved", "Saved"),
            Saved = random ? MenuText("Hud.Menu.Random", "Random")
                : !string.IsNullOrEmpty(saved) ? WeaponName(saved)
                : MenuText("Hud.Menu.None", "None"),
        };

        foreach (var weapon in tiles)
        {
            var tile = new Services.MenuTile
            {
                Art = ArtOf(weapon),
                Name = WeaponName(weapon),
                Selected = !random && saved == weapon,
            };

            // The limits as the plugin applies them (CheckIsWeaponRestricted): only with a choice to make, only for
            // weapons in "Weapons Restrict" for this mode
            if (!random && modeList.Count > 1 && Config.RestrictedWeapons.Restrictions.ContainsKey(weapon))
            {
                var limit = GetWeaponRestrict(weapon, isVip, team, ActiveCustomMode);
                if (limit < 0)
                {
                    var (_, vipLimit) = GetRestrictData(weapon, team);
                    tile.Limit = !isVip && vipLimit >= 0 ? MenuText("Hud.Menu.Vip", "VIP") : MenuText("Hud.Menu.Off", "Off");
                }
                else if (limit > 0)
                {
                    tile.Limit = $"{WeaponUsers(weapon, team, primary)}/{limit}";
                }

                tile.Locked = !tile.Selected && CheckIsWeaponRestricted(weapon, isVip, team, modeList, ActiveCustomMode, primary);
            }

            section.Tiles.Add(tile);
        }

        return section;
    }

    /// <summary>How many players have this weapon saved for the current mode: the count CheckIsWeaponRestricted compares to the limit.</summary>
    private int WeaponUsers(string weapon, CsTeam team, bool primary)
    {
        var players = Utilities.GetPlayers().Where(p => playerData.ContainsKey(p.Slot));
        if (!Config.RestrictedWeapons.Global)
            players = players.Where(p => p.Team == team);

        return players.Count(p => primary
            ? playerData[p.Slot].PrimaryWeapon.TryGetValue(ActiveCustomMode, out var w) && w == weapon
            : playerData[p.Slot].SecondaryWeapon.TryGetValue(ActiveCustomMode, out var s) && s == weapon);
    }

    private void BuildSettings(Services.MenuView view, MenuViewer viewer, CCSPlayerController player, DeathmatchPlayerData? data, bool isVip)
    {
        view.SettingsEmpty = MenuText("Hud.Menu.NoSettings", "Nothing to set on this server.");

        // One group per category, then the menu's own options (another plugin's menu) under "More"
        var groups = new List<List<(Services.MenuRow Row, RowAction Action)>>();
        foreach (var category in Categorie.GetAllCategories())
        {
            var rows = new List<(Services.MenuRow, RowAction)>();
            foreach (var option in Menu.GetOptionsByCategory(category))
            {
                var row = SettingRow(player, option, data, isVip);
                if (row != null)
                    rows.Add(row.Value);
            }

            if (rows.Count > 0)
                groups.Add([(Heading(category.UseLocalizer ? Localizer[category.MenuTitle].Value : category.MenuTitle), new NoAction()), .. rows]);
        }

        var more = Menu.GetAllOptions()
            .Where(o => o.Category == null && o.OnChoose != null && !string.IsNullOrEmpty(o.Name) && o.Name != SpawnsEditorOption)
            .Select(o => SettingRow(player, o, data, isVip))
            .Where(r => r != null)
            .Select(r => r!.Value)
            .ToList();
        if (more.Count > 0)
            groups.Add([(Heading(MenuText("Hud.Menu.More", "More")), new NoAction()), .. more]);

        // Two columns, each group in whichever has fewer rows so far; a group that fits in neither is cut short
        var columns = new List<(Services.MenuRow Row, RowAction Action)>[] { [], [] };
        foreach (var group in groups)
        {
            var target = columns[0].Count <= columns[1].Count ? 0 : 1;
            if (columns[target].Count + group.Count > MenuPanel.RowSlots && columns[1 - target].Count + group.Count <= MenuPanel.RowSlots)
                target = 1 - target;

            var room = MenuPanel.RowSlots - columns[target].Count;
            if (room >= 2)
                columns[target].AddRange(group.Take(room));
        }

        for (var c = 0; c < MenuPanel.ColumnSlots; c++)
        {
            view.Columns[c] = columns[c].Select(r => r.Row).ToList();
            viewer.Rows[c] = columns[c].Select(r => r.Action).ToList();
        }
    }

    private Services.MenuRow Heading(string text) => new() { Kind = Services.MenuRowKind.Head, Label = Plain(text) };

    /// <summary>One option of the menu as a row, or null when this player does not get it (VIP only, a permission).</summary>
    private (Services.MenuRow Row, RowAction Action)? SettingRow(CCSPlayerController player, Menu option, DeathmatchPlayerData? data, bool isVip)
    {
        if (option.Permission != null && !AdminManager.PlayerHasPermissions(player, option.Permission))
            return null;

        var pref = option.Preference;
        if (pref != null)
        {
            if (pref.VipOnly && !isVip)
                return null;

            if (pref.BooleanData != null)
            {
                var on = data != null ? GetPrefsValue(data, pref.Name, pref.BooleanData.DefaultValue) : pref.BooleanData.DefaultValue;
                return (new Services.MenuRow
                {
                    Kind = Services.MenuRowKind.Options,
                    Label = PrefLabel(pref.Name),
                    Shortcut = Shortcut(pref.BooleanData.CommandShortcuts),
                    Options = [(MenuText("Hud.Menu.On", "On"), on), (MenuText("Hud.Menu.Off", "Off"), !on)],
                }, new PrefAction(pref, ["on", "off"]));
            }

            if (pref.Data != null)
            {
                var current = data != null ? GetPrefsValue(data, pref.Name, pref.Data.DefaultValue) : pref.Data.DefaultValue;
                var values = pref.Data.Options.Take(MenuPanel.OptionSlots).ToList();
                return (new Services.MenuRow
                {
                    Kind = Services.MenuRowKind.Options,
                    Label = PrefLabel(pref.Name),
                    Shortcut = Shortcut(pref.Data.CommandShortcuts),
                    Options = values.Select(v => (Plain(v), v == current)).ToList(),
                }, new PrefAction(pref, values));
            }

            return null;
        }

        if (option.OnChoose != null && !string.IsNullOrEmpty(option.Name))
        {
            return (new Services.MenuRow
            {
                Kind = Services.MenuRowKind.Go,
                Label = Plain(Phrase(option.Name, option.Name)),
                Go = MenuText("Hud.Menu.Open", "Open") + " ›",
            }, new GoAction(option));
        }

        return null;
    }

    private void BuildSpawns(Services.MenuView view)
    {
        // The counts as the old editor showed them: 0 until the map's own spawns are switched off
        var ct = DefaultMapSpawnDisabled ? spawnPoints.Count(x => x.Team == CsTeam.CounterTerrorist) : 0;
        var t = DefaultMapSpawnDisabled ? spawnPoints.Count(x => x.Team == CsTeam.Terrorist) : 0;

        view.CtKey = MenuText("Hud.Menu.Spawns.CtCount", "CT spawns");
        view.Ct = ct.ToString();
        view.TKey = MenuText("Hud.Menu.Spawns.TCount", "T spawns");
        view.T = t.ToString();
        view.Dirty = _spawnsDirty ? MenuText("Hud.Menu.Spawns.Unsaved", "Not saved") : "";
        view.AddCt = MenuText("Hud.Menu.Spawns.AddCt", "Add CT spawn");
        view.AddT = MenuText("Hud.Menu.Spawns.AddT", "Add T spawn");
        view.Remove = MenuText("Hud.Menu.Spawns.Remove", "Remove nearest");
        view.Save = MenuText("Hud.Menu.Spawns.Save", "Save spawns");
        view.SpawnsHint = MenuText("Hud.Menu.Spawns.Hint",
            "A spawn goes where you stand, facing where you look. The markers show every spawn until you save. Close the menu to move, !dm_editor brings it back.");
    }

    // ------------------------------------------------------------------ clicks

    private void OnMenuClicked(CCSPlayerController player, string id)
    {
        if (!_menuViewers.TryGetValue(player.Slot, out var viewer))
            return;

        if (id.StartsWith("dmm_tab") && int.TryParse(id.AsSpan(7), out var tab))
        {
            SwitchTab(player, viewer, tab);
            return;
        }

        if (id.StartsWith("dmm_sp_"))
        {
            if (viewer.Tab == MenuPanel.TabSpawns)
                ClickSpawns(player, viewer, id);
            return;
        }

        if (id.StartsWith("dmm_p") && int.TryParse(id.AsSpan(5), out var p))
        {
            if (viewer.Tab == MenuPanel.TabWeapons)
                ClickWeapon(player, viewer, viewer.Primaries, p, true);
            return;
        }

        if (id.StartsWith("dmm_s") && int.TryParse(id.AsSpan(5), out var s))
        {
            if (viewer.Tab == MenuPanel.TabWeapons)
                ClickWeapon(player, viewer, viewer.Secondaries, s, false);
            return;
        }

        var match = SettingId.Match(id);
        if (match.Success && viewer.Tab == MenuPanel.TabSettings)
        {
            var column = int.Parse(match.Groups[1].Value);
            var row = int.Parse(match.Groups[2].Value);
            if (column < viewer.Rows.Length && row < viewer.Rows[column].Count)
                ClickSetting(player, viewer, viewer.Rows[column][row], match.Groups[4].Success ? -1 : int.Parse(match.Groups[3].Value));
        }
    }

    private void ClickWeapon(CCSPlayerController player, MenuViewer viewer, List<string> tiles, int index, bool primary)
    {
        if (index < 0 || index >= tiles.Count || !playerData.TryGetValue(player.Slot, out var data))
            return;

        var weapon = tiles[index];
        var name = WeaponName(weapon);

        if (ActiveMode.RandomWeapons)
        {
            PlayCantEquip(player);
            SetStatus(viewer, MenuText("Hud.Menu.Status.Random", "Weapons are random in this mode."), "warn");
            DrawMenu(player);
            return;
        }

        // The tile that is already the choice: nothing to do, and no "already set" buzz for a click on it
        var saved = (primary ? data.PrimaryWeapon : data.SecondaryWeapon).GetValueOrDefault(ActiveCustomMode);
        if (saved == weapon)
            return;

        switch (PickWeapon(player, data, weapon))
        {
            case WeaponPick.Primary:
                SetStatus(viewer, MenuText("Hud.Menu.Status.Primary", "{0} is your primary now.", name), "ok");
                break;
            case WeaponPick.Secondary:
                SetStatus(viewer, MenuText("Hud.Menu.Status.Secondary", "{0} is your secondary now.", name), "ok");
                break;
            case WeaponPick.AlreadySet:
                SetStatus(viewer, MenuText("Hud.Menu.Status.Already", "{0} is already set.", name), "");
                break;
            case WeaponPick.Restricted:
                var (nonVip, vip) = GetRestrictData(weapon, player.Team);
                SetStatus(viewer, MenuText("Hud.Menu.Status.Restricted", "{0} is taken: the limit is {1} (VIP {2}).",
                    name, RestrictText(nonVip), RestrictText(vip)), "warn");
                break;
            default:
                SetStatus(viewer, MenuText("Hud.Menu.Status.NotInMode", "{0} is not in this mode.", name), "warn");
                break;
        }

        // Every open menu: the pick moved this weapon's count
        RedrawMenus();
    }

    private void ClickSetting(CCSPlayerController player, MenuViewer viewer, RowAction action, int option)
    {
        var isVip = AdminManager.PlayerHasPermissions(player, Config.PlayersSettings.VIPFlag);

        switch (action)
        {
            case PrefAction pref when option >= 0 && option < pref.Values.Count:
            {
                var preference = pref.Preference;

                // Still registered (an API module may have gone) and still this player's (VIP)
                if (!Preference.GetAllPreferences().Contains(preference) || (preference.VipOnly && !isVip)
                    || !playerData.TryGetValue(player.Slot, out var data))
                {
                    DrawMenu(player);
                    return;
                }

                var label = PrefLabel(preference.Name);
                if (preference.BooleanData != null)
                {
                    var wanted = pref.Values[option] == "on";
                    if (GetPrefsValue(data, preference.Name, preference.BooleanData.DefaultValue) == wanted)
                        return;

                    // Where SwitchBooleanPrefsValue writes it, without its chat line: the row shows the state
                    data.Preferences[preference.Name] = wanted;
                    SetStatus(viewer, MenuText("Hud.Menu.Status.Pref", "{0}: {1}.", label,
                        wanted ? MenuText("Hud.Menu.On", "On") : MenuText("Hud.Menu.Off", "Off")), "ok");
                }
                else if (preference.Data != null)
                {
                    var value = pref.Values[option];
                    if (!preference.Data.Options.Contains(value) || GetPrefsValue(data, preference.Name, preference.Data.DefaultValue) == value)
                        return;

                    data.Preferences[preference.Name] = value;
                    SetStatus(viewer, MenuText("Hud.Menu.Status.Pref", "{0}: {1}.", label, Plain(value)), "ok");
                }

                DrawMenu(player);
                return;
            }

            case GoAction go when option < 0:
            {
                var choose = go.Option;
                if (choose.OnChoose == null || !Menu.GetAllOptions().Contains(choose)
                    || (choose.Permission != null && !AdminManager.PlayerHasPermissions(player, choose.Permission)))
                {
                    DrawMenu(player);
                    return;
                }

                // Another plugin's menu: let go of the mouse first, then open it next frame
                MenuHud?.Close(player);
                var slot = player.Slot;
                var steamId = player.SteamID;
                Server.NextFrame(() =>
                {
                    var again = Utilities.GetPlayerFromSlot(slot);
                    if (again is { IsValid: true } && again.SteamID == steamId)
                        choose.OnChoose(again, choose);
                });
                return;
            }
        }
    }

    private void ClickSpawns(CCSPlayerController player, MenuViewer viewer, string id)
    {
        if (!CanEditSpawns(player))
        {
            DrawMenu(player);
            return;
        }

        var pawn = player.PlayerPawn.Value;
        var alive = player.PawnIsAlive && pawn is { IsValid: true } && pawn.AbsOrigin != null && pawn.AbsRotation != null;

        switch (id)
        {
            case "dmm_sp_addct":
            case "dmm_sp_addt":
            {
                if (!alive)
                {
                    SetStatus(viewer, MenuText("Hud.Menu.Spawns.Alive", "You have to be alive to place a spawn."), "warn");
                    break;
                }

                var team = id == "dmm_sp_addct" ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                AddNewSpawnPoint(pawn!.AbsOrigin!, pawn.AbsRotation!, team);
                DisableDefaultSpawns();
                ShowSpawnMarkers();
                _spawnsDirty = true;

                var total = spawnPoints.Count(x => x.Team == team);
                SetStatus(viewer, team == CsTeam.CounterTerrorist
                    ? MenuText("Hud.Menu.Spawns.AddedCt", "CT spawn added ({0} in all).", total)
                    : MenuText("Hud.Menu.Spawns.AddedT", "T spawn added ({0} in all).", total), "ok");
                break;
            }

            case "dmm_sp_rm":
            {
                if (!alive)
                {
                    SetStatus(viewer, MenuText("Hud.Menu.Spawns.Alive", "You have to be alive to place a spawn."), "warn");
                    break;
                }

                RemoveNearestSpawnPoint(pawn!.AbsOrigin);
                DisableDefaultSpawns();
                ShowSpawnMarkers();
                _spawnsDirty = true;
                SetStatus(viewer, MenuText("Hud.Menu.Spawns.Removed", "The nearest spawn is gone."), "ok");
                break;
            }

            case "dmm_sp_save":
                SaveSpawnsFile();
                RemoveSpawnModels();
                _spawnsDirty = false;
                SetStatus(viewer, MenuText("Hud.Menu.Spawns.Saved", "Spawns saved. The markers are off until the next edit."), "ok");
                break;

            default:
                return;
        }

        // Other admins in the editor see the new counts too
        RedrawMenus();
    }

    // ------------------------------------------------------------------ the weapon pick, shared with !gun

    /// <summary>
    /// The weapon choice itself, for !gun &lt;name&gt; and the menu's tiles: the limits, the saved choice per mode,
    /// the weapon in hand. Plays the can't-equip sound on a refusal; the caller says why.
    /// </summary>
    public WeaponPick PickWeapon(CCSPlayerController player, DeathmatchPlayerData data, string weaponName)
    {
        bool IsVIP = AdminManager.PlayerHasPermissions(player, Config.PlayersSettings.VIPFlag);
        var pawn = player.PlayerPawn.Value;

        if (ActiveMode.PrimaryWeapons.Contains(weaponName))
        {
            if (data.PrimaryWeapon.TryGetValue(ActiveCustomMode, out var weapon) && weaponName == weapon)
            {
                PlayCantEquip(player);
                return WeaponPick.AlreadySet;
            }
            if (CheckIsWeaponRestricted(weaponName, IsVIP, player.Team, ActiveMode.PrimaryWeapons, ActiveCustomMode, true))
            {
                PlayCantEquip(player);
                return WeaponPick.Restricted;
            }

            data.PrimaryWeapon[ActiveCustomMode] = weaponName;

            var primaryWeapon = pawn?.GetWeaponFromSlot(gear_slot_t.GEAR_SLOT_RIFLE);
            if (player.PawnIsAlive)
            {
                if (primaryWeapon == null)
                {
                    player.GiveNamedItem(weaponName);
                }
                else if (Config.Gameplay.SwitchWeapons)
                {
                    var secondaryWeapon = pawn?.GetWeaponFromSlot(gear_slot_t.GEAR_SLOT_PISTOL);
                    player.RemoveWeapons();
                    player.GiveNamedItem("weapon_knife");
                    player.GiveNamedItem(weaponName);
                    if (secondaryWeapon != null)
                        player.GiveNamedItem(secondaryWeapon.DesignerName);

                    if (ActiveMode.Armor != 0)
                    {
                        var armor = ActiveMode.Armor == 1 ? "item_kevlar" : "item_assaultsuit";
                        player.GiveNamedItem(armor);
                    }
                }
            }
            return WeaponPick.Primary;
        }

        if (ActiveMode.SecondaryWeapons.Contains(weaponName))
        {
            if (data.SecondaryWeapon.TryGetValue(ActiveCustomMode, out var weapon) && weaponName == weapon)
            {
                PlayCantEquip(player);
                return WeaponPick.AlreadySet;
            }
            if (CheckIsWeaponRestricted(weaponName, IsVIP, player.Team, ActiveMode.SecondaryWeapons, ActiveCustomMode, false))
            {
                PlayCantEquip(player);
                return WeaponPick.Restricted;
            }

            data.SecondaryWeapon[ActiveCustomMode] = weaponName;

            var secondaryWeapon = pawn?.GetWeaponFromSlot(gear_slot_t.GEAR_SLOT_PISTOL);
            if (player.PawnIsAlive)
            {
                if (secondaryWeapon == null)
                {
                    player.GiveNamedItem(weaponName);
                }
                else if (Config.Gameplay.SwitchWeapons)
                {
                    var primaryWeapon = pawn?.GetWeaponFromSlot(gear_slot_t.GEAR_SLOT_RIFLE);
                    player.RemoveWeapons();
                    player.GiveNamedItem("weapon_knife");
                    player.GiveNamedItem(weaponName);
                    if (primaryWeapon != null)
                        player.GiveNamedItem(primaryWeapon.DesignerName);

                    if (ActiveMode.Armor != 0)
                    {
                        var armor = ActiveMode.Armor == 1 ? "item_kevlar" : "item_assaultsuit";
                        player.GiveNamedItem(armor);
                    }
                }
            }
            return WeaponPick.Secondary;
        }

        PlayCantEquip(player);
        return WeaponPick.NotInMode;
    }

    private void PlayCantEquip(CCSPlayerController player)
    {
        if (!string.IsNullOrEmpty(Config.SoundSettings.CantEquipSound))
            player.ExecuteClientCommand("play " + Config.SoundSettings.CantEquipSound);
    }

    // ------------------------------------------------------------------ the spawns editor

    private bool CanEditSpawns(CCSPlayerController player) =>
        Config.SpawnSystem.SpawnsMethod == 0 && AdminManager.PlayerHasPermissions(player, "@css/root");

    /// <summary>The tab is there for root admins when the menu lists the editor, or when the editor command opened it.</summary>
    private bool SeesSpawnsTab(CCSPlayerController player, MenuViewer viewer) =>
        CanEditSpawns(player) && (Config.General.DisplayZonesEditorInMenu || viewer.Editor);

    /// <summary>Every spawn as a model, as the old editor did each time it opened.</summary>
    private void ShowSpawnMarkers()
    {
        RemoveSpawnModels();
        ShowAllSpawnPoints();
    }

    // ------------------------------------------------------------------ text

    private static void SetStatus(MenuViewer viewer, string text, string tone)
    {
        viewer.Status = text;
        viewer.Tone = tone;
    }

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    private string MenuText(string key, string fallback, params object[] args)
    {
        var text = Localizer[key, args];
        return text.ResourceNotFound || string.IsNullOrEmpty(text.Value) || text.Value == key ? string.Format(fallback, args) : text.Value;
    }

    private string WeaponName(string weapon)
    {
        var text = Localizer[weapon];
        return text.ResourceNotFound || string.IsNullOrEmpty(text.Value) || text.Value == weapon
            ? (weapon.StartsWith("weapon_") ? weapon["weapon_".Length..] : weapon).ToUpperInvariant()
            : Plain(text.Value);
    }

    private string PrefLabel(string name) => Plain(Phrase($"Prefs.{name}", name));

    private string RestrictText(int limit) => limit switch
    {
        -1 => MenuText("Hud.Menu.Off", "Off"),
        0 => MenuText("Hud.Menu.Unlimited", "no limit"),
        _ => limit.ToString(),
    };

    /// <summary>"!hs" from a preference's command shortcuts, or "" for none.</summary>
    private static string Shortcut(List<string>? shortcuts)
    {
        var first = shortcuts?.Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
        if (first == null)
            return "";

        return "!" + (first.StartsWith("css_") ? first["css_".Length..] : first);
    }

    /// <summary>Text from a config or an API module without the center-menu HTML it may carry.</summary>
    private static string Plain(string text) => Html.Replace(text, "").Trim();

    /// <summary>"weapon_m4a1_silencer" -> "m4a1_silencer" when the stylesheet has its art, else "".</summary>
    private static string ArtOf(string weapon)
    {
        var name = weapon.StartsWith("weapon_") ? weapon["weapon_".Length..] : weapon;
        return MenuPanel.Weapons.Contains(name) ? name : "";
    }

    private static IEnumerable<string> Ordered(IEnumerable<string> weapons) =>
        weapons.Distinct().Select((w, i) => (w, i))
            .OrderBy(x => Array.IndexOf(WeaponOrder, x.w) is var at && at >= 0 ? at : WeaponOrder.Length + x.i)
            .Select(x => x.w);
}
