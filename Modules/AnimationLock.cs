using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Dalamud.Bindings.ImGui;
using static NoClippyUnchained.NoClippyUnchained;

namespace NoClippyUnchained
{
    public partial class Configuration
    {
        public bool EnableAnimLockComp = true;
        public bool EnableLogging = false;
        public bool EnableDryRun = false;
        public Dictionary<uint, float> AnimationLocks = new();
        public ulong TotalActionsReduced = 0ul;
        public double TotalAnimationLockReduction = 0d;
        public float AnimationLockPercent = 75f;
        public bool EnableIgnoreCasting = false;
        public bool UsePercentage = false;
    }
}

namespace NoClippyUnchained.Modules
{
    public class AnimationLock : Module
    {
        public override bool IsEnabled
        {
            get => Config.EnableAnimLockComp;
            set => Config.EnableAnimLockComp = value;
        }

        public override int DrawOrder => 1;

        private const float simulatedRTT = 0.001f;
        private float delay = -1;
        private int packetsSent = 0;
        private bool isCasting = false;
        private float intervalPacketsTimer = 0;
        private int intervalPacketsIndex = 0;
        private readonly int[] intervalPackets = new int[5];
        private bool saveConfig = false;
        private readonly Dictionary<ushort, float> appliedAnimationLocks = new();

        public bool IsDryRunEnabled => Config.EnableDryRun;

        private float AverageDelay(float currentDelay, float weight) =>
            delay > 0
                ? delay = delay * (1 - weight) + currentDelay * weight
                : delay = currentDelay;

        private static float GetAnimationLock(uint actionID) => (!Config.AnimationLocks.TryGetValue(actionID, out var animationLock) || animationLock < 0.5f
                ? Game.DefaultClientAnimationLock
                : animationLock)
            + simulatedRTT;

        private void UpdateDatabase(uint actionID, float animationLock)
        {
            if (Config.AnimationLocks.TryGetValue(actionID, out var oldLock) && oldLock == animationLock) return;
            Config.AnimationLocks[actionID] = animationLock;
            saveConfig = true;
            DalamudApi.LogDebug($"Recorded new animation lock value of {F2MS(animationLock)} ms for {actionID}");
        }

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            packetsSent = intervalPackets.Sum();

            if (Game.actionManager->animationLock != Game.DefaultClientAnimationLock) return;

            var id = ActionManager.GetSpellIdForAction((ActionType)actionType, actionID);
            var animationLock = GetAnimationLock(id);
            if (!IsDryRunEnabled)
            {
                Game.actionManager->animationLock = animationLock;
                appliedAnimationLocks[Game.actionManager->currentSequence] = animationLock;
            }

            DalamudApi.LogDebug($"Applying {F2MS(animationLock)} ms animation lock for {actionType} {actionID} ({id})");
        }

        private void CastBegin(ulong objectID, nint packetData) => isCasting = true;
        private void CastInterrupt(nint actionManager) => isCasting = false;

        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds, float oldLock, float newLock)
        {
            try
            {
                if (oldLock == newLock || (nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address) return;

                if (isCasting && !Config.EnableIgnoreCasting)
                {
                    isCasting = false;
                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");
                    return;
                }

                if (!Config.UsePercentage)
                {
                    var sequence = header->SourceSequence;
                    var actionID = header->SpellId;
                    var appliedLock = appliedAnimationLocks.GetValueOrDefault(sequence, 0.5f);

                    var lastRecordedLock = IsDryRunEnabled ? newLock : appliedLock - simulatedRTT;

                    var correction = newLock - lastRecordedLock;
                    var rtt = appliedLock - oldLock;

                    if (rtt <= simulatedRTT)
                    {
                        if (Config.EnableLogging)
                            PrintLog($"RTT ({F2MS(rtt)} ms) was lower than {F2MS(simulatedRTT)} ms, no adjustments were made");
                        return;
                    }

                    var prevAverage = delay;
                    var newAverage = AverageDelay(rtt, packetsSent > 1 ? 0.1f : 1f);
                    var average = Math.Max(prevAverage > 0 ? prevAverage : newAverage, 0.001f);

                    var variationMultiplier = Math.Max(rtt / average, 1) - 1;
                    var networkVariation = simulatedRTT * variationMultiplier;

                    var adjustedAnimationLock = Math.Max(oldLock + correction + networkVariation, 0);

                    if (!IsDryRunEnabled && float.IsFinite(adjustedAnimationLock) && adjustedAnimationLock < 20)
                    {
                        Game.actionManager->animationLock = adjustedAnimationLock;

                        Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                        Config.TotalActionsReduced++;

                        if (!saveConfig && DalamudApi.Condition[ConditionFlag.InCombat])
                            saveConfig = true;
                    }

                    if (!Config.EnableLogging) return;

                    var sb = new StringBuilder(IsDryRunEnabled ? "[DRY] " : string.Empty)
                            .Append($"Action: {actionID} ")
                            .Append(lastRecordedLock != newLock ? $"({F2MS(lastRecordedLock)} > {F2MS(newLock)} ms)" : $"({F2MS(newLock)} ms)")
                            .Append($" || RTT: {F2MS(rtt)} (+{variationMultiplier:P0}) ms");

                    if (!IsDryRunEnabled)
                        sb.Append($" || Lock: {F2MS(oldLock)} > {F2MS(adjustedAnimationLock)} ({F2MS(correction + networkVariation):+0;-#}) ms");

                    sb.Append($" || Packets: {packetsSent}");

                    PrintLog(sb.ToString());
                }
                else
                {
                    var actionID = header->SpellId;
                    var reductionPercent = Config.AnimationLockPercent / 100f;
                    var adjustedAnimationLock = oldLock * (1f - reductionPercent);

                    if (!IsDryRunEnabled && float.IsFinite(adjustedAnimationLock) && adjustedAnimationLock < 20)
                    {
                        Game.actionManager->animationLock = adjustedAnimationLock;

                        Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                        Config.TotalActionsReduced++;

                        if (!saveConfig && DalamudApi.Condition[ConditionFlag.InCombat])
                            saveConfig = true;
                    }

                    if (!Config.EnableLogging) return;

                    var sb = new StringBuilder(IsDryRunEnabled ? "[DRY] " : string.Empty)
                            .Append($"Action: {actionID} ")
                            .Append($"({F2MS(oldLock)} ms)")
                            .Append($" || Reduction: {reductionPercent:P0}");

                    if (!IsDryRunEnabled)
                        sb.Append($" || Lock: {F2MS(oldLock)} > {F2MS(adjustedAnimationLock)} ({F2MS(oldLock - adjustedAnimationLock):+0;-#}) ms");

                    PrintLog(sb.ToString());
                }
            }
            catch { PrintError("Error in AnimationLock Module"); }
        }

        private void NetworkMessage()
        {
            intervalPackets[intervalPacketsIndex]++;
        }

        private void Update()
        {
            if (saveConfig && DalamudApi.Condition[ConditionFlag.BetweenAreas])
            {
                Config.Save();
                saveConfig = false;
            }

            intervalPacketsTimer += (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            while (intervalPacketsTimer >= 0.01f)
            {
                intervalPacketsTimer -= 0.01f;
                intervalPacketsIndex = (intervalPacketsIndex + 1) % intervalPackets.Length;
                intervalPackets[intervalPacketsIndex] = 0;
            }
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Animation Lock Reduction", ref Config.EnableAnimLockComp))
                Config.Save();

            if (ImGui.Checkbox("Use Percentage Reduction Instead Of Fixed", ref Config.UsePercentage))
                Config.Save();

            if (Config.UsePercentage)
            {
                if (ImGui.SliderFloat("% Reduction", ref Config.AnimationLockPercent, 1f, 100f, "%.0f"))
                {
                    Config.AnimationLockPercent = MathF.Round(Config.AnimationLockPercent);
                    Config.Save();
                }
            }

            if (ImGui.Checkbox("Allow Cast & Limit Break Animation To Be Reduced", ref Config.EnableIgnoreCasting))
                Config.Save();

            if (Config.EnableAnimLockComp)
            {
                ImGui.Columns(2, "AnimlockColumns", false);

                if (ImGui.Checkbox("Enable Logging", ref Config.EnableLogging))
                    Config.Save();

                ImGui.NextColumn();

                var _ = IsDryRunEnabled;
                if (ImGui.Checkbox("Dry Run", ref _))
                {
                    Config.EnableDryRun = _;
                    Config.Save();
                }
                PluginUI.SetItemTooltip("The plugin will still log and perform calculations, but no in-game values will be overwritten.");
            }

            ImGui.Columns(1);

            ImGui.TextUnformatted($"Reduced a total time of {TimeSpan.FromSeconds(Config.TotalAnimationLockReduction):d\\:hh\\:mm\\:ss} from {Config.TotalActionsReduced} actions");
        }

        public override unsafe void Enable()
        {
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
            Game.OnReceiveActionEffect += ReceiveActionEffect;
            Game.OnUpdate += Update;
            Game.OnNetworkMessageDelegate += NetworkMessage;
        }

        public override unsafe void Disable()
        {
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
            Game.OnReceiveActionEffect -= ReceiveActionEffect;
            Game.OnUpdate -= Update;
            Game.OnNetworkMessageDelegate -= NetworkMessage;
        }
    }
}