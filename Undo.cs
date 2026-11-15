using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2FunUploader;

/// <summary>
/// Undo buttons for single-player combat, like Monster Train 2's: undo the last
/// card / potion, undo the current turn, or restart the combat.
///
/// Restart is what Save &amp; Quit + Continue already does: reload the save made
/// on entering the room. Combat RNG is restored from the save, so the fight
/// replays identically. Undo action / turn restart the same way and then
/// re-run the game's own combat recording (CombatReplayWriter, the input
/// log its multiplayer replays use) up to just before the undone input, with
/// animations skipped. So an undo reaches only states that Save &amp; Quit and
/// replaying the same moves would reach. Every use is logged with the run
/// (Recorder.Rewind) and counts in the game's own reload counter.
/// </summary>
static class Undo
{
    const string Tag = "[sts2fun]";
    /// While true the single-player net service reports itself as a replay, so
    /// the game takes hook actions and card choices from the recording instead
    /// of generating them or asking the player.
    public static bool Simulating;
    static bool _busy;

    static RunManager Rm => RunManager.Instance;

    static CombatReplay? CurrentReplay() =>
        Traverse.Create(Rm.CombatReplayWriter).Field("_replay").GetValue<CombatReplay>();

    static bool IsInput(CombatReplayEvent e) =>
        e.eventType == CombatReplayEventType.GameAction &&
        e.action is NetPlayCardAction or NetUsePotionAction or NetDiscardPotionGameAction;

    static bool IsTurnEnd(CombatReplayEvent e) =>
        e.eventType == CombatReplayEventType.GameAction && e.action is NetReadyToBeginEnemyTurnAction;

    /// Index of the event to cut at (events before it are kept), or -1.
    static int CutIndex(CombatReplay replay, string kind)
    {
        var ev = replay.events;
        int turnStart = ev.FindLastIndex(IsTurnEnd) + 1;
        if (kind == "undo_action")
        {
            int i = ev.FindLastIndex(IsInput);
            return i >= turnStart ? i : -1;
        }
        if (kind == "undo_turn")
            return ev.FindIndex(turnStart, IsInput);
        return 0;
    }

    /// Why a button can't be used right now, or null.
    public static string? Blocked(string kind)
    {
        if (_busy) return "busy";
        if (Rm.NetService?.Type != NetGameType.Singleplayer) return "single-player only";
        var cm = CombatManager.Instance;
        if (!cm.IsInProgress) return "not in combat";
        if (cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo || cm.DebugOnlyGetState().CurrentSide != CombatSide.Player)
            return "wait for your turn";
        if (!Rm.ActionQueueSet.IsEmpty || Rm.ActionExecutor.IsRunning) return "wait for actions to finish";
        var rs = Rm.DebugOnlyGetState();
        // a fight started from an event reloads into the event; only restart works there
        if (rs.CurrentRoomCount != 1 && kind != "restart_combat") return "not in event fights";
        if (kind == "restart_combat") return null;
        var replay = CurrentReplay();
        if (replay == null || CutIndex(replay, kind) < 0) return "nothing to undo this turn";
        return null;
    }

    public static void Request(string kind)
    {
        string? why = Blocked(kind);
        if (why != null) { GD.Print($"{Tag} {kind}: {why}"); return; }
        _busy = true;
        TaskHelper.RunSafely(Run(kind));
    }

    static async Task Run(string kind)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        CombatReplay? replay = CurrentReplay();
        int cut = replay == null ? 0 : CutIndex(replay, kind);
        var events = replay?.events.Take(cut).ToList() ?? new List<CombatReplayEvent>();
        try
        {
            Recorder.Safe(() => Recorder.Rewind(kind));
            if (SaveManager.Instance.CurrentRunSaveTask is { } t) await t;
            var read = SaveManager.Instance.LoadRunSave();
            if (!read.Success || read.SaveData == null) { GD.PrintErr($"{Tag} {kind}: no run save"); return; }
            SerializableRun save = read.SaveData;
            if (save.PreFinishedRoom != null) { GD.PrintErr($"{Tag} {kind}: save is after this combat"); return; }

            await NGame.Instance.Transition.FadeOut(0.25f);
            Rm.CleanUp();
            RunState state = RunState.FromSerializable(save);
            await Rm.SetUpSavedSingleplayer(state, save);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await PreloadManager.LoadRunAssets(state.Players.Select((Player p) => p.Character));
            await PreloadManager.LoadActAssets(state.Act);
            Rm.Launch();
            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(state));
            await Rm.GenerateMap();
            if (replay != null)
            {   // same id counters as the original fight, so recorded ids line up
                Rm.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
                Rm.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
                Rm.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
                Rm.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
                Traverse.Create(Rm.ChecksumTracker).Property("NextId").SetValue(replay.nextChecksumId);
            }
            if (events.Count > 0)
            {   // as NMultiplayerTest.RunReplay: no peer sync, waits skipped
                Simulating = true;
                Rm.CombatStateSynchronizer.IsDisabled = true;
                NonInteractiveMode.AutoSlayerCheck = () => Simulating;
            }
            await Rm.LoadIntoLatestMapCoord(null);
            while (Rm.ActionExecutor.IsPaused) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (events.Count > 0 && !await Replay(events, state, tree, replay!))
            {
                GD.PrintErr($"{Tag} {kind}: replay diverged; restarting the combat instead");
                Simulating = false;
                _busy = false;
                await Run("restart_combat");
                return;
            }
        }
        catch (Exception e) { GD.PrintErr($"{Tag} {kind} failed: {e}"); }
        finally
        {
            Simulating = false;
            try { Rm.CombatStateSynchronizer.IsDisabled = false; } catch { }
            NonInteractiveMode.AutoSlayerCheck = () => false;
            _busy = false;
        }
        try { await NGame.Instance.Transition.FadeIn(0.25f); } catch { }
    }

    /// Feeds recorded events like NMultiplayerTest.RunReplay. False if the
    /// recomputed state stops matching the recorded checksums.
    static async Task<bool> Replay(List<CombatReplayEvent> events, RunState state, SceneTree tree, CombatReplay original)
    {
        var expected = original.checksumData.ToDictionary(c => c.checksumData.id, c => Rm.ChecksumTracker.GenerateChecksum(c.fullState));
        bool ok = true;
        void Check(NetChecksumData d, string _, NetFullCombatState __)
        {
            if (expected.TryGetValue(d.id, out uint want) && want != d.checksum) ok = false;
        }
        Rm.ChecksumTracker.ChecksumGenerated += Check;
        try
        {
            async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            foreach (var e in events)
            {
                if (!ok) return false;
                switch (e.eventType)
                {
                    case CombatReplayEventType.GameAction:
                    {
                        while (CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo) await Frame();
                        GameAction action = e.action!.ToGameAction(state.GetPlayer(e.playerId!.Value));
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                            while (CombatManager.Instance.DebugOnlyGetState().CurrentSide == CombatSide.Enemy) await Frame();
                        Rm.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                        if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction)
                            await Rm.ActionExecutor.FinishedExecutingActions();
                        break;
                    }
                    case CombatReplayEventType.HookAction:
                        Rm.ActionQueueSet.EnqueueWithoutSynchronizing(
                            Rm.ActionQueueSynchronizer.GetHookActionForId(e.hookId!.Value, e.playerId!.Value, e.gameActionType!.Value));
                        break;
                    case CombatReplayEventType.ResumeAction:
                        Rm.ActionQueueSet.ResumeActionWithoutSynchronizing(e.actionId!.Value);
                        break;
                    case CombatReplayEventType.PlayerChoice:
                        Rm.PlayerChoiceSynchronizer.ReceiveReplayChoice(state.GetPlayer(e.playerId!.Value), e.choiceId!.Value, e.playerChoiceResult!.Value);
                        break;
                }
            }
            // let the kept actions finish before handing control back
            for (int i = 0; i < 600 && !(Rm.ActionQueueSet.IsEmpty && !Rm.ActionExecutor.IsRunning); i++) await Frame();
            await Rm.ActionExecutor.FinishedExecutingActions();
            return ok && Rm.ActionQueueSet.IsEmpty;
        }
        finally { Rm.ChecksumTracker.ChecksumGenerated -= Check; }
    }

    // ---- buttons ----
    public static void AddButtons(NCombatUi ui)
    {
        if (!Uploader.UndoEnabled || Rm.NetService?.Type != NetGameType.Singleplayer) return;
        var box = new HBoxContainer { Name = "Sts2FunUndo" };
        box.AddThemeConstantOverride("separation", 6);
        foreach (var (label, kind, tip) in new[]
        {
            ("Undo card", "undo_action", "Undo the last card or potion"),
            ("Undo turn", "undo_turn", "Back to the start of this turn"),
            ("Restart", "restart_combat", "Restart the combat (same as Save & Quit)"),
        })
        {
            var b = new Button { Text = label, TooltipText = tip, FocusMode = Control.FocusModeEnum.None };
            b.AddThemeFontSizeOverride("font_size", 18);
            b.Pressed += () => Request(kind);
            box.AddChild(b);
        }
        ui.AddChild(box);
        // above the end turn button
        Callable.From(() =>
        {
            var end = ui.EndTurnButton;
            box.GlobalPosition = end.GlobalPosition + new Vector2(end.Size.X - box.Size.X, -box.Size.Y - 12);
        }).CallDeferred();
    }
}

[HarmonyPatch(typeof(NetSingleplayerGameService), nameof(NetSingleplayerGameService.Type), MethodType.Getter)]
static class Patch_SingleplayerType
{
    static void Postfix(ref NetGameType __result) { if (Undo.Simulating) __result = NetGameType.Replay; }
}

[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi._Ready))]
static class Patch_NCombatUi_Ready
{
    static void Postfix(NCombatUi __instance) => Recorder.Safe(() => Undo.AddButtons(__instance));
}
