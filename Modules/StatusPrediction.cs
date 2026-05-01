using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

namespace NoClippyUnchained
{
    public partial class Configuration
    {
        public bool PredictStatusApplications = false;
        public bool PredictMudras = false;
        public bool PredictDualcast = false;
    }
}

namespace NoClippyUnchained.Modules
{
    public class StatusPrediction : Module
    {
        [StructLayout(LayoutKind.Explicit, Size = 0xC)]
        public struct Status
        {
            [FieldOffset(0x0)] public ushort StatusID;
            [FieldOffset(0x2)] public byte StackCount;
            [FieldOffset(0x3)] public byte Param;
            [FieldOffset(0x4)] public float RemainingTime;
            [FieldOffset(0x8)] public uint SourceID;
        }

        public override bool IsEnabled => false;

        public override int DrawOrder => 15;

        private class PredictedStatusList
        {
            private readonly List<PredictedStatus> statuses = new();

            public PredictedStatus Add(ushort statusID = 0, byte stacks = 0, byte param = 0, bool replace = false, float timer = 0.75f, Action endAction = null)
            {
                var statusList = DalamudApi.ObjectTable.LocalPlayer!.StatusList;

                int prev;

                if (!replace)
                {
                    if (statusList.Any(status => status.StatusId == statusID)) return null;
                    prev = statuses.FindIndex(s => !s.replace && s.status.StatusID == statusID);
                }
                else
                {
                    prev = statuses.FindIndex(s => s.replace && s.status.StatusID == statusID);
                }

                if (prev >= 0)
                {
                    statuses[prev].TryRemove(statusList);
                    endAction?.Invoke();
                    statuses.RemoveAt(prev);
                }

                var predicted = new PredictedStatus
                {
                    status = new Status
                    {
                        StatusID = statusID,
                        StackCount = stacks,
                        Param = param
                    },
                    timer = timer,
                    replace = replace,
                    endAction = endAction
                };

                statuses.Add(predicted);
                return predicted;
            }

            public void Update(float dt)
            {
                var statusList = DalamudApi.ObjectTable.LocalPlayer?.StatusList;
                var exists = statusList != null;

                for (int i = statuses.Count - 1; i >= 0; i--)
                {
                    var status = statuses[i];
                    if ((status.timer -= dt) > 0) continue;

                    if (exists)
                        status.TryRemove(statusList);

                    status.endAction?.Invoke();
                    statuses.RemoveAt(i);
                }
            }

            public unsafe void Apply(StatusList statusList)
            {
                if (statuses.Count == 0) return;

                var currentIndex = 0;
                for (short i = 0; i < statusList.Length; i++)
                {
                    var breakLoop = false;

                    while (statuses[currentIndex].replace)
                    {
                        ++currentIndex;
                        if (breakLoop = statuses.Count == currentIndex)
                            break;
                    }

                    if (breakLoop)
                        break;

                    var statusPtr = (Status*)statusList.GetStatusAddress(i);
                    if (!replaceableStatusIDs.Contains(statusPtr->StatusID) || IsStatusValid(statusPtr)) continue;

                    var status = statuses[currentIndex];
                    status.Apply(statusPtr, i);
                    ++currentIndex;
                    if (statuses.Count == currentIndex) break;
                }

                for (int i = 0; i < statuses.Count; i++)
                {
                    var status = statuses[i];
                    if (status.replace)
                        status.Replace(statusList);
                    else if (i >= currentIndex)
                        status.currentSlot = -1;
                }
            }

            public void CheckNewStatus(StatusList statusList, short slot, ushort statusID)
            {
                var reapply = false;
                for (int i = 0; i < statuses.Count; i++)
                {
                    var status = statuses[i];
                    var replaced = slot == status.currentSlot;
                    reapply = reapply || replaced;
                    if (status.replace || statusID != status.status.StatusID) continue;

                    if (!replaced)
                        status.TryRemove(statusList);

                    status.endAction?.Invoke();
                    statuses.RemoveAt(i);
                    break;
                }

                if (reapply)
                    Apply(DalamudApi.ObjectTable.LocalPlayer!.StatusList);
            }

            public bool Remove(PredictedStatus status)
            {
                var removed = statuses.Remove(status);

                if (removed && DalamudApi.ObjectTable.LocalPlayer?.StatusList is { } statusList)
                {
                    status.TryRemove(statusList);
                    status.endAction?.Invoke();
                }

                return removed;
            }

            public bool Contains(PredictedStatus status) => statuses.Contains(status);
        }

        private unsafe class PredictedStatus
        {
            public Status status = new();
            public float timer = 0;
            public bool replace = false;
            public short currentSlot = -1;
            public Action endAction = null;

            public void Apply(Status* statusPtr, short slot)
            {
                statusPtr->StatusID = status.StatusID;
                statusPtr->StackCount = status.StackCount;
                statusPtr->Param = status.Param;
                currentSlot = slot;
            }

            public void Replace(StatusList statusList)
            {
                for (short i = 0; i < statusList.Length; i++)
                {
                    var statusPtr = (Status*)statusList.GetStatusAddress(i);
                    if (statusPtr->StatusID != status.StatusID) continue;
                    Apply(statusPtr, i);
                    return;
                }
            }

            public void TryRemove(StatusList statusList)
            {
                if (currentSlot < 0 || replace) return;
                var statusPtr = (Status*)statusList.GetStatusAddress(currentSlot);
                if (statusPtr->StatusID != status.StatusID || statusPtr->Param != status.Param || statusPtr->StackCount != status.StackCount) return;
                statusPtr->StatusID = 0;
                statusPtr->StackCount = 0;
                statusPtr->Param = 0;
                currentSlot = -1;
            }
        }

        private readonly PredictedStatusList predictedStatusList = new();
        private bool predictDualcast = false;
        private bool inPVP = false;
        private PredictedStatus dualCast = null;

        private class StatusInfo
        {
            public ushort id = 0;
            public byte stacks = 0;
            public byte param = 0;
            public float timer = 0.75f;
            public bool replace = false;
            public Action beginAction = null;
            public Action endAction = null;
        }

        private static unsafe void SwapMudras(byte b)
        {
            var jobGaugeManager = (byte*)JobGaugeManager.Instance();
            if (jobGaugeManager == null) return;
            *(jobGaugeManager + 0x8 + 0xE) = b;
        }

        private static unsafe void SwapEukrasia(byte b)
        {
            var jobGaugeManager = JobGaugeManager.Instance();
            if (jobGaugeManager == null) return;
            jobGaugeManager->Sage.Eukrasia = b;
        }

        private const ushort MudraStatusID = 496;
        private readonly Dictionary<uint, List<StatusInfo>> predictedStatuses = new()
        {
            [7561] = new() { new() { id = 167 } },
            [7421] = new() { new() { id = 1211, stacks = 3 } },
            [7518] = new() { new() { id = 1238 } },
            [24290] = new() { new() { id = 2606, timer = 1.15f, beginAction = () => SwapEukrasia(1), endAction = () => SwapEukrasia(0) } },
            [2264] = new() { new() { id = 497, beginAction = () => SwapMudras(1), endAction = () => SwapMudras(0) } },
        };

        private static readonly HashSet<ushort> replaceableStatusIDs = new() { 0, 167, 496, 497, 1211, 1238, 1249, 1393, 2606 };

        private static unsafe bool IsStatusValid(Status* statusPtr) => statusPtr->StatusID != 0 && (statusPtr->RemainingTime > 0 || statusPtr->SourceID is not (0 or 0xE0000000));

        private void UseActionLocation(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            if (actionType != 1 || ret == 0) return;

            if (NoClippyUnchained.Config.PredictStatusApplications)
                PredictStatuses(actionID);

            if (NoClippyUnchained.Config.PredictMudras)
                PredictMudras(actionID);
        }

        private void PredictStatuses(uint actionID)
        {
            if (!predictedStatuses.TryGetValue(actionID, out var statuses)) return;

            foreach (var status in statuses)
            {
                if (predictedStatusList.Add(status.id, status.stacks, status.param, status.replace, status.timer, status.endAction) != null)
                    status.beginAction?.Invoke();
            }

            predictedStatusList.Apply(DalamudApi.ObjectTable.LocalPlayer!.StatusList);
        }

        private void PredictMudras(uint actionID)
        {
            switch (actionID)
            {
                case 2259:
                case 18805:
                    UpdateMudraStatus(1);
                    break;
                case 2261:
                case 18806:
                    UpdateMudraStatus(2);
                    break;
                case 2263:
                case 18807:
                    UpdateMudraStatus(3);
                    break;
            }
        }

        private unsafe void UpdateMudraStatus(byte bit)
        {
            var mudraStacks = 0;

            var statusList = DalamudApi.ObjectTable.LocalPlayer!.StatusList;
            for (int i = 0; i < statusList.Length; i++)
            {
                var statusPtr = (Status*)statusList.GetStatusAddress(i);
                if (statusPtr->StatusID != MudraStatusID) continue;
                mudraStacks = statusPtr->StackCount;
                break;
            }

            switch (mudraStacks)
            {
                case > 63:
                    return;
                case > 15:
                    mudraStacks = 0xFF;
                    break;
                case > 3:
                    mudraStacks += bit << 4;
                    break;
                case > 0:
                    mudraStacks += bit << 2;
                    break;
                default:
                    predictedStatusList.Add(MudraStatusID, bit, 0, false, 1f);
                    predictedStatusList.Apply(statusList);
                    return;
            }

            if (mudraStacks > byte.MaxValue) return;

            predictedStatusList.Add(MudraStatusID, (byte)mudraStacks, 0, true, 1f);
            predictedStatusList.Apply(statusList);
        }

        private unsafe void UpdateDualcast()
        {
            var statusList = DalamudApi.ObjectTable.LocalPlayer?.StatusList;
            if (statusList == null)
            {
                predictDualcast = false;
                return;
            }

            if (Game.actionManager->isCasting) return;
            dualCast = predictedStatusList.Add((ushort)(!inPVP ? 1249 : 1393));
            if (dualCast != null)
                predictedStatusList.Apply(statusList);
            predictDualcast = false;
        }

        private unsafe void CastBegin(ulong objectID, nint packetData)
        {
            if (!NoClippyUnchained.Config.PredictDualcast || DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId != 35 || *(byte*)(packetData + 2) != 1) return;

            var actionID = *(ushort*)packetData;
            if (actionID < 9) return;

            dualCast = null;
            predictDualcast = true;
        }

        private void CastInterrupt(nint actionManager)
        {
            if (!predictDualcast) return;
            predictedStatusList.Remove(dualCast);
            dualCast = null;
            predictDualcast = false;
        }

        private void Update()
        {
            predictedStatusList.Update((float)DalamudApi.Framework.UpdateDelta.TotalSeconds);

            if (predictDualcast)
                UpdateDualcast();
        }

        public override void DrawConfig()
        {
        }

        public override void Enable()
        {
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnUpdate += Update;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
        }

        public override void Disable()
        {
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnUpdate -= Update;
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
        }
    }
}