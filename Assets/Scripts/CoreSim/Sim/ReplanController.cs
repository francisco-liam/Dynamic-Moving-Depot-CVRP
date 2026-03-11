#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
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

        /// <summary>
        /// Sim-seconds that elapse per real second (i.e. the simulation speed multiplier).
        /// Used to convert the available sim-time window into a real-time solver budget.
        /// Set this to match the Unity <c>speedMultiplier</c> field.
        /// </summary>
        public float SimTimeScale { get; set; } = 1.0f;

        /// <summary>
        /// Sim-time of the next known external event (e.g. next batch insertion).
        /// When set, the solver budget is also capped so the solution is ready before that event.
        /// Leave at <c>float.PositiveInfinity</c> when no future event is known.
        /// </summary>
        public float NextScheduledInsertionSimTime { get; set; } = float.PositiveInfinity;

        /// <summary>Floor for the computed dynamic solver budget (real seconds).</summary>
        public float MinSolverTimeBudgetSeconds { get; set; } = 0.1f;

        /// <summary>
        /// How many sim-seconds before the next scheduled batch insertion the solver must
        /// finish. The budget window closes this many sim-seconds early so the plan is ready
        /// before new customers arrive.
        /// </summary>
        public float PreBatchBufferSimSeconds { get; set; } = 2f;

        public float LastReplanTime { get; private set; } = float.NegativeInfinity;
        public string LastSummary { get; private set; } = string.Empty;
        public IPlanner Planner => _planner;

        private readonly IPlanner _planner;

        private int _lastSeenEventCount;
        private float _nextPeriodicTime;
        private bool _pendingReplan;
        private string _pendingReason = string.Empty;

        // Background async planning state
        private Task<PlanResult>? _activePlanTask;
        private string _activePlanReason = string.Empty;
        private readonly Stopwatch _planStopwatch = new Stopwatch();

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
            _activePlanTask = null;   // abandon any in-flight task (background thread keeps its snapshot; no leak risk)
            _activePlanReason = string.Empty;

            float now = simulation?.State?.Time ?? 0f;
            _nextPeriodicTime = now + System.Math.Max(0f, PeriodicInterval);
            LastReplanTime = float.NegativeInfinity;
        }

        public bool Step(Simulation? simulation)
        {
            if (simulation == null || simulation.State == null)
                return false;

            float now = simulation.State.Time;

            // Apply any completed background plan before observing new events.
            bool applied = TryApplyCompletedPlan(simulation, now);

            ObserveEvents(simulation, now);
            ObservePeriodic(now);
            ObserveEarlyLockBoundary(simulation, now);

            if (!AutoReplanEnabled)
                return applied;

            bool started = TryStartReplan(simulation, now, force: false);
            return applied || started;
        }

        public bool ReplanNow(Simulation? simulation)
        {
            if (simulation == null || simulation.State == null)
                return false;

            _pendingReplan = true;
            _pendingReason = AppendReason(_pendingReason, "manual");
            float now = simulation.State.Time;
            TryApplyCompletedPlan(simulation, now);  // flush any just-finished task first
            return TryStartReplan(simulation, now, force: true);
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

            float minTimeToNextLock = ComputeMinTimeToNextLockBoundary(state);

            if (float.IsPositiveInfinity(minTimeToNextLock))
                return;

            // Lead-time estimate uses the static SolverTimeBudgetSeconds as a conservative bound;
            // the actual per-replan dynamic budget is computed in TryStartReplan.
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

        private float ComputeMinTimeToNextLockBoundary(SimState state)
        {
            float minTime = float.PositiveInfinity;
            for (int i = 0; i < state.Trucks.Count; i++)
            {
                float t = EstimateTimeToNextLockBoundary(state, state.Trucks[i]);
                if (t < minTime)
                    minTime = t;
            }
            return minTime;
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

        /// <summary>
        /// Checks if the background solver task has completed and, if so, applies its result
        /// to the live simulation state. Call this at the START of each Step so the plan is
        /// applied as soon as it is ready, before any new observations queue another replan.
        /// </summary>
        private bool TryApplyCompletedPlan(Simulation simulation, float now)
        {
            if (_activePlanTask == null || !_activePlanTask.IsCompleted)
                return false;

            PlanResult result;
            try
            {
                result = _activePlanTask.Result; // non-blocking: task is already complete
            }
            catch (Exception ex)
            {
                LastSummary = $"[{now:0.##}] Replan task faulted: {ex.Message}";
                Console.WriteLine($"[Replan] Background plan task threw: {ex}");
                _activePlanTask = null;
                _activePlanReason = string.Empty;
                return false;
            }

            _activePlanTask = null;
            double elapsedMs = _planStopwatch.Elapsed.TotalMilliseconds;
            int changedTrucks = ApplyPlan(simulation.State, result, CommitmentLockK);
            LastSummary = $"[{now:0.##}] Replan applied ({_activePlanReason}) in {elapsedMs:0}ms, changedTrucks={changedTrucks}. {result.DebugSummary}";
            Console.WriteLine($"[Replan] {LastSummary}");
            _activePlanReason = string.Empty;
            return true;
        }

        /// <summary>
        /// If a replan is pending and no solver is currently running, snapshots the current
        /// sim state and launches the solver on a background thread. Returns true if a task
        /// was started. The simulation continues stepping normally while the task runs.
        /// </summary>
        private bool TryStartReplan(Simulation simulation, float now, bool force)
        {
            if (!_pendingReplan)
                return false;

            // One solver at a time. The pending flag stays set so the next call after
            // the current task finishes will kick off a fresh replan.
            if (_activePlanTask != null && !_activePlanTask.IsCompleted)
            {
                Console.WriteLine(
                    $"[Replan] Replan queued at t={now:0.###} but solver still running; will retry on completion.");
                return false;
            }

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
                RespectReleaseTime = RespectReleaseTime,
                SolverTimeBudgetSeconds = ComputeDynamicBudget(simulation.State, now, out string budgetDetail),
            };

            // Take a deep-enough snapshot of the state so the background thread
            // has its own copy and cannot race with the main-thread simulation.
            SimState snapshot = SnapshotForPlanning(simulation.State);

            // Capture locals for the lambda — avoids capturing 'this'.
            IPlanner planner = _planner;

            _activePlanReason = _pendingReason;
            _planStopwatch.Restart();
            _activePlanTask = Task.Run(() => planner.ComputePlan(snapshot, ctx));

            LastReplanTime = now;
            LastSummary = $"[{now:0.##}] Replan started ({_activePlanReason}) budget={ctx.SolverTimeBudgetSeconds:0.###}s  {budgetDetail}";
            Console.WriteLine(
                $"[Replan] Launched background solver at t={now:0.###} reason={_activePlanReason}");

            _pendingReplan = false;
            _pendingReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Computes how many real seconds the solver should be given for the upcoming replan.
        /// Constraint 1: time until the nearest truck reaches its immediate next customer.
        /// Constraint 2: time until the next scheduled batch insertion, minus a pre-batch buffer.
        /// Both are converted from sim-time to real-time via <see cref="SimTimeScale"/>.
        /// Falls back to <see cref="SolverTimeBudgetSeconds"/> when no constraint is active.
        /// </summary>
        private float ComputeDynamicBudget(SimState state, float now, out string detail)
        {
            // Sim-seconds until the nearest truck reaches its immediate next customer.
            float timeToNextCustomer = ComputeMinTimeToNextCustomer(state);

            // Sim-seconds until the next batch, reduced by the pre-batch buffer so the
            // plan is ready before new customers appear.
            float rawTimeToInsert = NextScheduledInsertionSimTime - now;
            float timeToNextInsert = System.Math.Max(0f, rawTimeToInsert - System.Math.Max(0f, PreBatchBufferSimSeconds));

            float constraintSimSecs = System.Math.Min(timeToNextCustomer, timeToNextInsert);

            // No constraint → fall back to the configured maximum.
            if (float.IsPositiveInfinity(constraintSimSecs))
            {
                detail = "(no constraint, using max)";
                return SolverTimeBudgetSeconds;
            }

            // Convert sim-seconds → real-seconds.
            float scale = System.Math.Max(0.001f, SimTimeScale);
            float availableRealSecs = constraintSimSecs / scale;

            // Reserve time for process startup and result application.
            float budget = availableRealSecs
                         - System.Math.Max(0f, ProcessOverheadBufferSeconds)
                         - System.Math.Max(0f, SafetyMarginSeconds);

            // Floor: never give the solver less than the minimum.
            budget = System.Math.Max(MinSolverTimeBudgetSeconds, budget);

            detail = $"nextCustomerSim={timeToNextCustomer:0.##}  nextInsertSim={rawTimeToInsert:0.##}  " +
                     $"preBatchBuf={PreBatchBufferSimSeconds:0.##}  scale={scale:0.##}  avail={availableRealSecs:0.##}s";
            return budget;
        }

        /// <summary>
        /// Returns the minimum sim-time across all trucks until any truck arrives at its
        /// immediate next customer target (the first Customer entry at or after CurrentTargetIndex).
        /// Trucks currently servicing count their remaining service time.
        /// Returns <see cref="float.PositiveInfinity"/> when no truck has a customer target.
        /// </summary>
        private float ComputeMinTimeToNextCustomer(SimState state)
        {
            float minTime = float.PositiveInfinity;
            for (int i = 0; i < state.Trucks.Count; i++)
            {
                float t = EstimateTimeToNextCustomer(state, state.Trucks[i]);
                if (t < minTime)
                    minTime = t;
            }
            return minTime;
        }

        private float EstimateTimeToNextCustomer(SimState state, Truck truck)
        {
            float elapsed = 0f;
            Vec2 currentPos = truck.Pos;
            int startIndex = Clamp(truck.CurrentTargetIndex, 0, truck.Plan.Count);

            // If currently servicing, account for remaining service time and advance past this stop.
            if (truck.State == TruckState.Servicing)
            {
                elapsed += System.Math.Max(0f, truck.ServiceRemaining);
                var servCustomer = state.GetCustomerById(truck.ServicingCustomerId);
                if (servCustomer != null)
                    currentPos = servCustomer.Pos;
                startIndex += 1;
            }

            // Walk forward until we find a Customer target.
            for (int i = startIndex; i < truck.Plan.Count; i++)
            {
                var target = truck.Plan[i];
                if (!TryResolveTargetPos(state, target, out Vec2 targetPos))
                    return float.PositiveInfinity;

                float speed = System.Math.Max(0f, truck.Speed);
                if (speed <= 0f)
                    return float.PositiveInfinity;

                elapsed += Vec2.Distance(currentPos, targetPos) / speed;
                currentPos = targetPos;

                if (target.Type == TargetType.Customer)
                    return elapsed; // found the next customer
            }

            return float.PositiveInfinity; // no customer target in plan
        }

        /// <summary>
        /// Creates a planning snapshot of the live state: deep-copies all mutable data needed
        /// by the solver so the background thread can safely read it while the main thread
        /// continues stepping the simulation.
        /// </summary>
        private static SimState SnapshotForPlanning(SimState live)
        {
            var depotSnap = new DepotCarrier(live.Depot.Pos, live.Depot.Speed);
            var snap = new SimState(live.Capacity, depotSnap)
            {
                Time              = live.Time,
                Features          = live.Features,
                EnergyCapacity    = live.EnergyCapacity,
                EnergyConsumption = live.EnergyConsumption,
            };

            // Customers: copy each object — Status and AssignedTruckId are the mutable fields.
            for (int i = 0; i < live.Customers.Count; i++)
            {
                var c = live.Customers[i];
                var cc = new Customer(c.Id, c.Pos, c.Demand, c.ReleaseTime, c.ServiceTime)
                {
                    Status         = c.Status,
                    AssignedTruckId = c.AssignedTruckId,
                };
                snap.Customers.Add(cc);
            }

            // Trucks: copy each object with a deep-copied Plan list.
            // TargetRef is a struct so list element copies are already value copies.
            for (int i = 0; i < live.Trucks.Count; i++)
            {
                var t = live.Trucks[i];
                var tc = new Truck(t.Id, t.Pos, t.Capacity, t.Speed, t.BatteryCapacity, t.EnergyConsumption)
                {
                    Load                = t.Load,
                    State               = t.State,
                    CurrentTargetIndex  = t.CurrentTargetIndex,
                    LockedPrefixCount   = t.LockedPrefixCount,
                    ServiceRemaining    = t.ServiceRemaining,
                    ServicingCustomerId = t.ServicingCustomerId,
                    ActiveTarget        = t.ActiveTarget,
                    ArrivedOnActiveTarget = t.ArrivedOnActiveTarget,
                };
                for (int j = 0; j < t.Plan.Count; j++)
                    tc.Plan.Add(t.Plan[j]);
                snap.Trucks.Add(tc);
            }

            // StationPositions: Vec2 is a struct so a shallow dict copy is sufficient.
            foreach (var kvp in live.StationPositions)
                snap.StationPositions[kvp.Key] = kvp.Value;
            for (int i = 0; i < live.StationNodeIds.Count; i++)
                snap.StationNodeIds.Add(live.StationNodeIds[i]);

            return snap;
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
