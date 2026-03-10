#nullable enable
using System;
using System.Collections.Generic;
using CoreSim.Events;
using CoreSim.Math;
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
        public bool EnableEarlyLockReplan { get; set; } = true;
        public float SolverTimeBudgetSeconds { get; set; } = 1.0f;
        public float ProcessOverheadBufferSeconds { get; set; } = 0.25f;
        public float SafetyMarginSeconds { get; set; } = 0.25f;

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
            _pendingReplan = false;
            _pendingReason = string.Empty;
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
            ObserveEarlyLockBoundary(simulation, now);

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

        private void ObserveEarlyLockBoundary(Simulation simulation, float now)
        {
            if (!EnableEarlyLockReplan)
                return;

            var state = simulation.State;
            if (state == null || state.Trucks.Count == 0)
                return;

            float minTimeToNextLock = float.PositiveInfinity;
            for (int i = 0; i < state.Trucks.Count; i++)
            {
                float t = EstimateTimeToNextLockBoundary(state, state.Trucks[i]);
                if (t < minTimeToNextLock)
                    minTimeToNextLock = t;
            }

            if (float.IsPositiveInfinity(minTimeToNextLock))
                return;

            float plannerLeadTime = System.Math.Max(0f, SolverTimeBudgetSeconds)
                                  + System.Math.Max(0f, ProcessOverheadBufferSeconds)
                                  + System.Math.Max(0f, SafetyMarginSeconds);

            if (minTimeToNextLock <= plannerLeadTime)
            {
                bool alreadyQueued = _pendingReason.Contains("EarlyLock", StringComparison.Ordinal);
                QueueReplan("EarlyLock");

                if (!alreadyQueued)
                {
                    Console.WriteLine(
                        $"[Replan] Early lock trigger at t={now:0.###}: min_time_to_next_lock={minTimeToNextLock:0.###}, planner_lead_time={plannerLeadTime:0.###}");
                }
            }
        }

        private int GetConservativeLockTargetCount(Truck truck)
        {
            int currentIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);
            int remaining = truck.Plan.Count - currentIndex;
            return remaining > 0 ? System.Math.Min(remaining, 1 + System.Math.Max(0, CommitmentLockK)) : 0;
        }

        private float EstimateTimeToNextLockBoundary(SimState state, Truck truck)
        {
            int lockTargetCount = GetConservativeLockTargetCount(truck);
            if (lockTargetCount <= 0)
                return float.PositiveInfinity;

            int currentIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);
            float total = 0f;
            int remainingLockedTargets = lockTargetCount;
            Vec2 currentPos = truck.Pos;

            if (truck.State == TruckState.Servicing)
            {
                total += System.Math.Max(0f, truck.ServiceRemaining);

                var servicingCustomer = state.GetCustomerById(truck.ServicingCustomerId);
                if (servicingCustomer != null)
                    currentPos = servicingCustomer.Pos;

                currentIndex += 1;
                remainingLockedTargets -= 1;
            }

            for (int i = currentIndex; i < truck.Plan.Count && remainingLockedTargets > 0; i++)
            {
                var target = truck.Plan[i];
                if (!TryResolveTargetPos(state, target, out Vec2 targetPos))
                    return 0f;

                float speed = System.Math.Max(0f, truck.Speed);
                if (speed <= 0f)
                    return 0f;

                float legDistance = Vec2.Distance(currentPos, targetPos);
                total += legDistance / speed;

                if (target.Type == TargetType.Customer)
                {
                    var customer = state.GetCustomerById(target.Id);
                    if (customer != null)
                        total += System.Math.Max(0f, customer.ServiceTime);
                }

                currentPos = targetPos;
                remainingLockedTargets -= 1;
            }

            if (remainingLockedTargets > 0)
                return float.PositiveInfinity;

            return total;
        }

        private static bool TryResolveTargetPos(SimState state, TargetRef target, out Vec2 pos)
        {
            if (target.Type == TargetType.Depot)
            {
                pos = state.Depot.Pos;
                return true;
            }

            if (target.Type == TargetType.Customer)
            {
                var customer = state.GetCustomerById(target.Id);
                if (customer != null)
                {
                    pos = customer.Pos;
                    return true;
                }

                pos = default;
                return false;
            }

            if (target.Type == TargetType.Station)
            {
                if (state.StationPositions.TryGetValue(target.Id, out var stationPos))
                {
                    pos = stationPos;
                    return true;
                }
            }

            pos = default;
            return false;
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
