using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
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
        // ALL INFO BELOW IS BASED ON MY FINDINGS AND I RESERVE THE RIGHT TO HAVE MISINTERPRETED SOMETHING, THANKS
        // The typical time range that passes for the client is never equal to ping, it always seems to be at least ping + server delay
        // The server delay is usually around 40-60 ms in the overworld, but falls to 30-40 ms inside of instances
        // Additionally, your FPS will add more time because one frame MUST pass for you to receive the new animation lock
        // Therefore, most players will never receive a response within 40 ms at any ping
        // Another interesting fact is that the delay from the server will spike if you send multiple packets at the same time
        // This seems to imply that the server will not process more than one packet from you per tick
        // You can see this if you sheathe your weapon before using an action, you will notice delays that are around 50 ms higher than usual
        // This explains the phenomenon where moving seems to make it harder to weave

        // For these reasons, I do not believe it is possible to triple weave on any ping without clipping even the slightest amount as that would require 25 ms response times for a 2.5 GCD triple

        // This module simulates around 10 ms ping inside instances

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
        private readonly int[] intervalPackets = new int[5]; // Record the last 50 ms of packets
        private bool saveConfig = false;
        private readonly Dictionary<ushort, float> appliedAnimationLocks = new();

        public bool IsDryRunEnabled => Config.EnableDryRun;

        private float AverageDelay(float currentDelay, float weight) =>
            delay > 0
                ? delay = delay * (1 - weight) + currentDelay * weight
                : delay = currentDelay; // Initial starting delay

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

        private unsafe void ReceiveActionEffect(int sourceActorID, nint sourceActor, nint vectorPosition, nint effectHeader, nint effectArray, nint effectTrail, float oldLock, float newLock)
        {
            try
            {
                if (oldLock == newLock || sourceActor != DalamudApi.ObjectTable.LocalPlayer?.Address) return;

                if (isCasting && !Config.EnableIgnoreCasting)
                {
                    isCasting = false;
                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");
                    return;
                }

                if (!Config.UsePercentage) {
                    var sequence = *(ushort*)(effectHeader + 0x18); // This is 0 for some special actions
                    var actionID = *(ushort*)(effectHeader + 0x1C);
                    var appliedLock = appliedAnimationLocks.GetValueOrDefault(sequence, 0.5f);

                    var lastRecordedLock = IsDryRunEnabled ? newLock : appliedLock - simulatedRTT;

                    // Get the difference between the recorded animation lock and the real one
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
                    var actionID = *(ushort*)(effectHeader + 0x1C);
                    // Calculate the percentage reduction
                    var reductionPercent = Config.AnimationLockPercent / 100f;
                    var adjustedAnimationLock = oldLock * (1f - reductionPercent);

                    // Apply the reduced animation lock
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

        private void NetworkMessage(nint dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
        {
            if (direction != NetworkMessageDirection.ZoneUp) return;
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
                ImGui.Columns(2, "", false);

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

        private void NetworkMessage(NetworkMessageDirection direction)
        {
            if (direction != NetworkMessageDirection.ZoneUp) return;
            intervalPackets[intervalPacketsIndex]++;
        }

        public override void Enable()
        {
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
            Game.OnReceiveActionEffect += ReceiveActionEffect;
            Game.OnUpdate += Update;
            Game.OnNetworkMessageDelegate += NetworkMessage;
        }

        public override void Disable()
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
