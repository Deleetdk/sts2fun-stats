using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using GD = Godot.GD;

namespace Sts2FunUploader;

/// <summary>
/// Records what the .run file doesn't contain, per run (keyed by the run's
/// seed, which the .run file also stores): full shop stock with prices and
/// what was bought, every event page with all options shown (and which were
/// locked / chosen), and every potion gained, used or discarded, including
/// ones created and used within a single combat. Saved to
/// <user data>/sts2fun_stats/runs/<seed>.json and uploaded with the run.
/// Read-only observation: nothing here changes game state.
/// </summary>
static class Recorder
{
    const int Schema = 1;
    static string _dir = "";
    static readonly Dictionary<string, RunExtras> Cache = new();
    // shop entries whose purchase is in progress (the game empties the entry
    // before its purchase hook fires, so note the contents first)
    static readonly ConditionalWeakTable<MerchantEntry, ShopItem> Pending = new();
    static EventModel? _lastEvent;   // options don't reference their event; pages are shown in order

    public static void Init(string stateDir) => _dir = Path.Combine(stateDir, "runs");

    public static RunExtras? Load(string seed)
    {
        try
        {
            string p = Path.Combine(_dir, SafeName(seed) + ".json");
            return File.Exists(p) ? JsonSerializer.Deserialize<RunExtras>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    /// Runs a hook body so that nothing it throws reaches the game: an API that
    /// differs between the stable and beta branches only drops that one record.
    /// (Calls go through a lambda so a missing member fails inside the try.)
    public static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { GD.PrintErr($"[sts2fun] record: {e.GetType().Name}: {e.Message}"); }
    }

    static string SafeName(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

    static void Update(IRunState? rs, Action<RunExtras, int> change)
    {
        try
        {
            if (rs == null) return;
            string seed = rs.Rng.StringSeed;
            if (string.IsNullOrEmpty(seed)) return;
            if (!Cache.TryGetValue(seed, out var x)) Cache[seed] = x = Load(seed) ?? new RunExtras { Seed = seed, Schema = Schema };
            change(x, rs.TotalFloor);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, SafeName(seed) + ".json"), JsonSerializer.Serialize(x));
        }
        catch (Exception e) { GD.PrintErr($"[sts2fun] record: {e.Message}"); }
    }

    static ShopItem? Describe(MerchantEntry e)
    {
        switch (e)
        {
            case MerchantCardEntry c when c.CreationResult?.Card is { } card:
                return new ShopItem { Kind = "card", Id = card.Id.Entry, Upgraded = card.CurrentUpgradeLevel, Cost = c.Cost, OnSale = c.IsOnSale };
            case MerchantRelicEntry r when r.Model != null:
                return new ShopItem { Kind = "relic", Id = r.Model.Id.Entry, Cost = r.Cost };
            case MerchantPotionEntry p when p.Model != null:
                return new ShopItem { Kind = "potion", Id = p.Model.Id.Entry, Cost = p.Cost };
            case MerchantCardRemovalEntry rm:
                return new ShopItem { Kind = "removal", Id = "CARD_REMOVAL", Cost = rm.Cost };
            default:
                return null;
        }
    }

    // ---- shops ----
    // Stable (<= v0.112): MerchantRoom.GetLocalInventory(). Beta v0.113+:
    // RunManager.Instance.MerchantSynchronizer.GetLocalInventory(). Looked up by
    // reflection so one build runs on both branches.
    static MerchantInventory? LocalInventory(MerchantRoom room)
    {
        var m = AccessTools.Method(room.GetType(), "GetLocalInventory");
        if (m != null) return m.Invoke(room, null) as MerchantInventory;
        var sync = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")?.GetValue(RunManager.Instance);
        return sync == null ? null : AccessTools.Method(sync.GetType(), "GetLocalInventory")?.Invoke(sync, null) as MerchantInventory;
    }

    public static void ShopEntered(MerchantRoom room, IRunState rs) => Update(rs, (x, floor) =>
    {
        var inv = LocalInventory(room);
        if (inv == null) return;
        var visit = new ShopVisit { Floor = floor };
        foreach (var e in inv.AllEntries)
            if (Describe(e) is { } item) visit.Stock.Add(item);
        // purchases are logged during the visit; keep them when replacing the stock view
        foreach (var old in x.Shops.Where(s => s.Floor == floor)) visit.Bought.AddRange(old.Bought);
        x.Shops.RemoveAll(s => s.Floor == floor);
        x.Shops.Add(visit);
    });

    public static void PurchaseStarting(MerchantEntry e)
    {
        try { if (Describe(e) is { } item) { Pending.Remove(e); Pending.Add(e, item); } }
        catch (Exception ex) { GD.PrintErr($"[sts2fun] record: {ex.Message}"); }
    }

    public static void Purchased(IRunState rs, Player player, MerchantEntry e, int goldSpent) => Update(rs, (x, floor) =>
    {
        if (!Pending.TryGetValue(e, out var item)) item = Describe(e) ?? new ShopItem { Kind = e.GetType().Name };
        Pending.Remove(e);
        var visit = x.Shops.LastOrDefault(s => s.Floor == floor);
        if (visit == null) { visit = new ShopVisit { Floor = floor }; x.Shops.Add(visit); }
        visit.Bought.Add(new ShopItem { Kind = item.Kind, Id = item.Id, Upgraded = item.Upgraded, Cost = goldSpent, OnSale = item.OnSale });
    });

    // ---- events ----
    public static void EventPage(EventModel ev) => Update((_lastEvent = ev).Owner?.RunState, (x, floor) =>
    {
        if (!ev.CurrentOptions.Any()) return;   // final page of an event: nothing to choose
        x.Events.Add(new EventPage
        {
            Floor = floor, Event = ev.Id.Entry,
            Options = ev.CurrentOptions.Select(o => new EventOptionShown { Key = o.TextKey, Locked = o.IsLocked }).ToList(),
        });
    });

    public static void OptionChosen(EventOption o) => Update(_lastEvent?.Owner?.RunState, (x, floor) =>
    {
        var page = x.Events.LastOrDefault(p => p.Floor == floor && p.Options.Any(op => op.Key == o.TextKey));
        if (page != null) page.Chosen = o.TextKey;
    });

    // ---- potions ----
    public static void Potion(string what, IRunState rs, ICombatState? combat, PotionModel potion) => Update(rs, (x, floor) =>
        x.Potions.Add(new PotionEvent { Floor = floor, What = what, Id = potion.Id.Entry, InCombat = combat != null }));
}

sealed class RunExtras
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("seed")] public string Seed { get; set; } = "";
    [JsonPropertyName("shops")] public List<ShopVisit> Shops { get; set; } = new();
    [JsonPropertyName("events")] public List<EventPage> Events { get; set; } = new();
    [JsonPropertyName("potions")] public List<PotionEvent> Potions { get; set; } = new();
}
sealed class ShopVisit
{
    [JsonPropertyName("floor")] public int Floor { get; set; }
    [JsonPropertyName("stock")] public List<ShopItem> Stock { get; set; } = new();
    [JsonPropertyName("bought")] public List<ShopItem> Bought { get; set; } = new();
}
sealed class ShopItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("upgraded")] public int Upgraded { get; set; }
    [JsonPropertyName("cost")] public int Cost { get; set; }
    [JsonPropertyName("on_sale")] public bool OnSale { get; set; }
}
sealed class EventPage
{
    [JsonPropertyName("floor")] public int Floor { get; set; }
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("options")] public List<EventOptionShown> Options { get; set; } = new();
    [JsonPropertyName("chosen")] public string? Chosen { get; set; }
}
sealed class EventOptionShown
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("locked")] public bool Locked { get; set; }
}
sealed class PotionEvent
{
    [JsonPropertyName("floor")] public int Floor { get; set; }
    [JsonPropertyName("what")] public string What { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("in_combat")] public bool InCombat { get; set; }
}

// ---- Harmony hooks (all observe only) ----

[HarmonyPatch(typeof(MerchantRoom), nameof(MerchantRoom.Exit))]
static class Patch_MerchantRoom_Exit
{   // at exit the stock is final; bought slots are empty, purchases come from Purchased()
    static void Prefix(MerchantRoom __instance, IRunState? runState) { if (runState != null) Recorder.Safe(() => Recorder.ShopEntered(__instance, runState)); }
}

[HarmonyPatch]
static class Patch_MerchantEntry_Purchase
{
    // OnTryPurchaseWrapper until v0.112, OnTryPurchaseUnsynchronized from v0.113
    static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized") ?? AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseWrapper");

    static void Prefix(MerchantEntry __instance) => Recorder.Safe(() => Recorder.PurchaseStarting(__instance));
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterItemPurchased))]
static class Patch_Hook_AfterItemPurchased
{
    static void Prefix(IRunState runState, Player player, MerchantEntry itemPurchased, int goldSpent)
        => Recorder.Safe(() => Recorder.Purchased(runState, player, itemPurchased, goldSpent));
}

[HarmonyPatch(typeof(EventModel), "SetEventState")]
static class Patch_EventModel_SetEventState
{
    static void Postfix(EventModel __instance) => Recorder.Safe(() => Recorder.EventPage(__instance));
}

[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
static class Patch_EventOption_Chosen
{
    static void Prefix(EventOption __instance) => Recorder.Safe(() => Recorder.OptionChosen(__instance));
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionProcured))]
static class Patch_Hook_PotionProcured
{
    static void Prefix(IRunState runState, ICombatState? combatState, PotionModel potion) => Recorder.Safe(() => Recorder.Potion("gained", runState, combatState, potion));
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
static class Patch_Hook_PotionUsed
{
    static void Prefix(IRunState runState, ICombatState? combatState, PotionModel potion) => Recorder.Safe(() => Recorder.Potion("used", runState, combatState, potion));
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionDiscarded))]
static class Patch_Hook_PotionDiscarded
{
    static void Prefix(IRunState runState, ICombatState? combatState, PotionModel potion) => Recorder.Safe(() => Recorder.Potion("discarded", runState, combatState, potion));
}
