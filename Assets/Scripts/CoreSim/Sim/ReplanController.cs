#nullable enable
using System;
using System.Collections.Generic;
using CoreSim.Events;
using CoreSim.Model;
using CoreSim.Planning;

namespace CoreSim.Sim
{
    public sealed class ReplanController
    {
        public bool AutoReplanEnabled { get; set; } = true;
        public float MinTimeBetweenReplans { get; set; } = 1.0f;
        public float PeriodicInterval { get; set; } = 2.0f;
        public int CommitmentLockK { get; set; } = 1;
        public bool RespectCapacity { get; set; } = true;
        public bool RespectReleaseTime { get; set; } = true;

        public float LastReplanTime { get; private set; } = float.NegativeInfinity;
        public string LastSummary { get; private set; } = string.Empty;

        private readonly IPlanner _planner;

        private int _lastSeenEventCount;
        private float _nextPeriodicTime;
        private bool _pendingReplan;
        private string _pendingReason = string.Empty;

        public ReplanController(IPlanner? planner = null)
        {
            _planner = planner ?? new BaselinePlanner();
        }

        public void Reset(Simulation? simulation)
        {
            _lastSeenEventCount = 0;
            // Queue an initial replan so the first Step() call computes a plan even if
            // no CustomerReleased events fire in that tick (e.g. all release times > 0).
            _pendingReplan = true;
            _pendingReason = "Initialize";
            LastSummary = string.Empty;

            float now = simulation?.State?.Time ?? 0f;
            _nextPeriodicTime = now + System.Math.Max(0f, PeriodicInterval);
            LastReplanTime = float.NegativeInfinity;
        }

        public bool Step(Simulation? simulation)
        {
            if (simulation == null || simulation.State == null)
                return false;

            float now = simulation.State.Time;

            ObserveEvents(simulation, now);
            ObservePeriodic(now);

            if (!AutoReplanEnabled)
                return false;

            return TryReplan(simulation, now, force: false);
        }

        public bool ReplanNow(Simulation? simulation)
        {
            if (simulation == null || simulation.State == null)
                return false;

            _pendingReplan = true;
            _pendingReason = AppendReason(_pendingReason, "manual");
            return TryReplan(simulation, simulation.State.Time, force: true);
        }

        private void ObserveEvents(Simulation simulation, float now)
        {
            var events = simulation.RecentEvents;
            if (events.Count < _lastSeenEventCount)
                _lastSeenEventCount = 0;

            var insertedIds = new HashSet<int>();
            var releasedIds = new HashSet<int>();

            for (int i = _lastSeenEventCount; i < events.Count; i++)
            {
                var e = events[i];
                if (e.Type == SimEventType.CustomerReleased)
                    releasedIds.Add(e.A);
                else if (e.Type == SimEventType.CustomerInserted)
                    insertedIds.Add(e.A);
            }

            _lastSeenEventCount = events.Count;

            bool sawRelease = releasedIds.Count > 0;
            bool sawInsertedWithoutImmediateRelease = false;

            if (insertedIds.Count > 0)
            {
                foreach (int id in insertedIds)
                {
                    if (!releasedIds.Contains(id))
                    {
                        sawInsertedWithoutImmediateRelease = true;
                        break;
                    }
                }
            }

            if (sawRelease)
                QueueReplan("CustomerReleased");

            if (sawInsertedWithoutImmediateRelease)
                QueueReplan("CustomerInserted");
        }

        private void ObservePeriodic(float now)
        {
            if (PeriodicInterval <= 0f)
                return;

            if (now < _nextPeriodicTime)
                return;

            QueueReplan("Periodic");

            while (_nextPeriodicTime <= now)
                _nextPeriodicTime += PeriodicInterval;
        }

        private void QueueReplan(string reason)
        {
            _pendingReplan = true;
            _pendingReason = AppendReason(_pendingReason, reason);
        }

        private bool TryReplan(Simulation simulation, float now, bool force)
        {
            if (!_pendingReplan)
                return false;

            if (!force)
            {
                float minGap = System.Math.Max(0f, MinTimeBetweenReplans);
                if (!float.IsNegativeInfinity(LastReplanTime) && now - LastReplanTime < minGap)
                    return false;
            }

            var ctx = new PlanningContext
            {
                Now = now,
                CommitmentLockK = System.Math.Max(0, CommitmentLockK),
                RespectCapacity = RespectCapacity,
                RespectReleaseTime = RespectReleaseTime
            };

            var result = _planner.ComputePlan(simulation.State, ctx);
            int changedTrucks = ApplyPlan(simulation.State, result, ctx.CommitmentLockK);

            LastReplanTime = now;
            LastSummary = $"[{now:0.##}] Replan ({_pendingReason}) changedTrucks={changedTrucks}. {result.DebugSummary}";
            _pendingReplan = false;
            _pendingReason = string.Empty;
            return true;
        }

        private static int ApplyPlan(SimState state, PlanResult result, int lockK)
        {
            int changedCount = 0;

            for (int i = 0; i < state.Trucks.Count; i++)
            {
                var truck = state.Trucks[i];
                if (truck.State == TruckState.Servicing)
                    continue;

                if (!result.TruckPlans.TryGetValue(truck.Id, out var plannedFull))
                    continue;

                int currentIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);
                int remaining = truck.Plan.Count - currentIndex;
                int preserveCount = remaining > 0 ? System.Math.Min(remaining, 1 + System.Math.Max(0, lockK)) : 0;

                var newPlan = new List<TargetRef>(truck.Plan.Count + plannedFull.Count + 4);

                for (int p = 0; p < currentIndex; p++)
                    newPlan.Add(truck.Plan[p]);

                for (int p = 0; p < preserveCount; p++)
                    newPlan.Add(truck.Plan[currentIndex + p]);

                int plannedFutureStart = Clamp(currentIndex, 0, plannedFull.Count);
                int plannedUnlockedStart = Clamp(plannedFutureStart + preserveCount, 0, plannedFull.Count);

                for (int p = plannedUnlockedStart; p < plannedFull.Count; p++)
                    newPlan.Add(plannedFull[p]);

                bool planChanged = !TargetSequenceEqual(truck.Plan, newPlan);
                if (!planChanged)
                {
                    truck.LockedPrefixCount = preserveCount;
                    continue;
                }

                bool futureChanged = FutureSequenceChanged(truck.Plan, newPlan, currentIndex + 1);

                truck.Plan.Clear();
                truck.Plan.AddRange(newPlan);
                truck.LockedPrefixCount = preserveCount;

                if (futureChanged)
                {
                    truck.ActiveTarget = null;
                    truck.ArrivedOnActiveTarget = false;
                }

                changedCount += 1;
            }

            return changedCount;
        }

        private static bool TargetSequenceEqual(List<TargetRef> a, List<TargetRef> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        private static bool FutureSequenceChanged(List<TargetRef> before, List<TargetRef> after, int startIndex)
        {
            int startA = Clamp(startIndex, 0, before.Count);
            int startB = Clamp(startIndex, 0, after.Count);

            int lenA = before.Count - startA;
            int lenB = after.Count - startB;
            if (lenA != lenB)
                return true;

            for (int i = 0; i < lenA; i++)
            {
                if (!before[startA + i].Equals(after[startB + i]))
                    return true;
            }

            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string AppendReason(string existing, string reason)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return reason;

            if (existing.Contains(reason, StringComparison.Ordinal))
                return existing;

            return existing + "+" + reason;
        }
    }
}
