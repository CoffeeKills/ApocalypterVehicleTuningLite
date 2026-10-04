using System;
using System.Collections.Generic;
using ApocalypterVehicleTuningLite.Persistence;
using ApocalypterVehicleTuningLite.Settings;
using NWH.Common.Vehicles;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.Powertrain.Wheel;
using UnityEngine;

namespace ApocalypterVehicleTuningLite.Runtime
{
    /// <summary>
    /// Applies the lite tuning categories (suspension, assists) to every live vehicle:
    ///   effective = the vehicle's captured stock value x the active preset factor.
    /// Stock values are captured the first time a vehicle is seen and restored when
    /// a category is switched off. Steering tuning is not a tuner category: the
    /// Harmony prefix in Patching/ reads SteeringSettings directly.
    /// Per-system logic lives in VehicleTuner.Systems.cs and VehicleTuner.Assists.cs.
    ///
    /// Cost model: a scene scan (FindObjectsOfType) runs every few seconds and on
    /// explicit request; dragging a slider only re-applies to already-known vehicles.
    /// </summary>
    public sealed partial class VehicleTuner : MonoBehaviour
    {
        private const float SCAN_INTERVAL = 2f;

        internal sealed class WheelData
        {
            public bool IsFront;
            public bool IsLeft;                                              // transform.localPosition.x < 0 at capture
            public float SpringForce, SpringLength, BumpRate, ReboundRate;   // suspension
        }

        internal sealed class GroupData
        {
            public bool IsFront;
            public float ArbForce;                    // suspension
        }

        internal sealed class AssistHandles
        {
            public Brakes.BrakeTorqueModifier Abs;
            public EngineComponent.PowerModifier Tcs;
            public bool AbsRegistered, TcsRegistered;
        }

        internal sealed class VehicleRecord
        {
            public VehicleController Vc;
            public Dictionary<WheelUAPI, WheelData> Wheels = new Dictionary<WheelUAPI, WheelData>();
            public Dictionary<WheelGroup, GroupData> Groups = new Dictionary<WheelGroup, GroupData>();
            public AssistHandles Assists;
            public AppliedCat Applied;      // categories THIS record currently has applied
            // Driven-vehicle pick liveness: the input sum at the last sample and when it
            // last changed. The game's FSM freezes a parked car's input at its exit values
            // (handbrake held, brakes last pressed); only recently-changed input is "live".
            public float InputPrev;
            public bool InputSeen;
            public float InputLastChange = -1f;
        }

        [Flags]
        internal enum AppliedCat
        {
            None = 0,
            Suspension = 1,
            Assists = 2
        }

        private readonly Dictionary<VehicleController, VehicleRecord> _records = new Dictionary<VehicleController, VehicleRecord>();
        // Insertion order of the records (the "reference vehicle" for per-wheel UI is the first one).
        private readonly List<VehicleRecord> _order = new List<VehicleRecord>();
        private readonly List<VehicleController> _dead = new List<VehicleController>();
        private float _nextScan;
        private bool _suspApplied, _assistsApplied;

        /// <summary>Number of vehicles currently tracked (shown in the panel).</summary>
        public int TrackedVehicles
        {
            get { return _records.Count; }
        }

        private void OnEnable()
        {
            ModConfig.SettingsChanged += ReapplyNow;
            _nextScan = 0f;   // re-apply on the next frame after a disable/enable cycle
        }

        private void OnDisable()
        {
            ModConfig.SettingsChanged -= ReapplyNow;
            // A disabled tuner stops applying, so it must hand the vehicles back now.
            // Otherwise a replacement runner would capture our tuned values as "stock"
            // (factors compound, and OFF would restore to tuned values). OnEnable's
            // next scan re-applies.
            RestoreAll();
        }

        private void OnDestroy()
        {
            RestoreAll();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
            {
                return;
            }
            _nextScan = Time.unscaledTime + SCAN_INTERVAL;
            ScanVehicles();
            ApplyLive();
        }

        /// <summary>Find new vehicles, then apply (or restore, when disabled). Use after toggles / on open.</summary>
        public void ReapplyNow()
        {
            _nextScan = Time.unscaledTime + SCAN_INTERVAL;
            ScanVehicles();
            ApplyLive();
        }

        /// <summary>
        /// Apply the current settings to vehicles already tracked. Cheap; safe to call every slider tick.
        /// A category that goes from not-applied to applied first re-reads its stock values
        /// (RefreshBaselines): while it was off the fields belonged to the game, so whatever
        /// the game set since the vehicle was first seen is the stock to scale and to restore.
        /// </summary>
        public void ApplyLive()
        {
            // Drop destroyed vehicles first. ApplyLive runs on every slider tick, i.e.
            // between scans; writing into a destroyed vehicle would throw inside NWH
            // and abort every later category.
            PurgeDead();

            if (SuspensionSettings.Enabled) { if (!_suspApplied) RefreshBaselines(Category.Suspension); ApplyAllSuspension(); _suspApplied = true; }
            else if (_suspApplied) { RestoreAllSuspension(); _suspApplied = false; }

            if (AssistsSettings.Enabled) { ApplyAllAssists(); _assistsApplied = true; }
            else if (_assistsApplied) { RestoreAllAssists(); _assistsApplied = false; }
        }

        /// <summary>Forget destroyed vehicles (allocation-free: reuses _dead).</summary>
        private void PurgeDead()
        {
            _dead.Clear();
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                if (kv.Key == null)
                {
                    _dead.Add(kv.Key);
                }
            }
            for (int i = 0; i < _dead.Count; i++)
            {
                VehicleRecord r;
                if (_records.TryGetValue(_dead[i], out r))
                {
                    _order.Remove(r);
                }
                _records.Remove(_dead[i]);
            }
            _dead.Clear();
            if (_captureFailures.Count > 0)
            {
                foreach (KeyValuePair<VehicleController, int> kv in _captureFailures)
                {
                    if (kv.Key == null)
                    {
                        _dead.Add(kv.Key);
                    }
                }
                for (int i = 0; i < _dead.Count; i++)
                {
                    _captureFailures.Remove(_dead[i]);
                }
                _dead.Clear();
            }
        }

        // ---- spawn-wave hardening -----------------------------------------------
        // The game loads a save by spawning hundreds of objects right after sceneLoaded. The
        // tuner's scan is read-only, but staying out of that wave entirely is cheap insurance.
        public const float SpawnQuietSeconds = 5f;   // no scans this long after a scene load
        public const int SpawnJump = 2;              // more new vehicles than this in one scan = still spawning
        public const int MaxSpawnDeferrals = 3;      // never defer capture for more than this many scans in a row
        private static float _quietUntil;            // static: a runner recreated on scene load sees it too
        private int _lastScanCount = -1;             // -1 = no scan yet: nothing to compare a jump against
        private int _spawnDeferrals;

        /// <summary>Called from Plugin.OnSceneLoaded: start the post-load quiet window.</summary>
        public static void NotifySceneLoaded()
        {
            _quietUntil = Time.unscaledTime + SpawnQuietSeconds;
        }

        /// <summary>
        /// Pure scan gate. Skip while inside the post-load quiet window; defer capture for one
        /// scan when the vehicle count jumped by more than SpawnJump since the last scan (the
        /// spawn wave is still running), but never more than MaxSpawnDeferrals scans in a row.
        /// </summary>
        public static bool ShouldSkipScan(float now, float quietUntil)
        {
            return now < quietUntil;
        }

        public static bool ShouldDeferCapture(int count, int lastCount, int deferralsSoFar)
        {
            return lastCount >= 0 && count > lastCount + SpawnJump && deferralsSoFar < MaxSpawnDeferrals;
        }

        private void ScanVehicles()
        {
            // Forget destroyed vehicles only. Vehicles that are merely inactive keep their
            // record: re-capturing one later would read our own tuned values as "stock".
            PurgeDead();
            if (ShouldSkipScan(Time.unscaledTime, _quietUntil))
            {
                return;
            }

            VehicleController[] vehicles = UnityEngine.Object.FindObjectsOfType<VehicleController>();
            int count = vehicles.Length;
            if (ShouldDeferCapture(count, _lastScanCount, _spawnDeferrals))
            {
                _lastScanCount = count;
                _spawnDeferrals++;
                return;
            }
            _lastScanCount = count;
            _spawnDeferrals = 0;
            for (int i = 0; i < vehicles.Length; i++)
            {
                VehicleController vc = vehicles[i];
                if (vc == null || _records.ContainsKey(vc))
                {
                    continue;
                }
                VehicleRecord record = TryCapture(vc);
                if (record != null)
                {
                    _records[vc] = record;
                    _order.Add(record);
                }
            }
        }

        // ---- per-category exception guards --------------------------------------
        public const int MaxCaptureAttempts = 3;     // retries for a vehicle whose capture threw somewhere
        private readonly Dictionary<VehicleController, int> _captureFailures = new Dictionary<VehicleController, int>();
        private static readonly HashSet<string> LoggedFaults = new HashSet<string>();

        /// <summary>Log a fault once per key (exception path only; allocates there, never on the hot path).</summary>
        internal static void LogFault(string category, VehicleController vc, Exception ex)
        {
            string key = category + "|" + VehicleName(vc) + "|" + (ex != null ? ex.GetType().Name : "");
            if (Plugin.Log == null || LoggedFaults.Count > 256 || !LoggedFaults.Add(key))
            {
                return;
            }
            Plugin.Log.LogWarning(category + " on '" + VehicleName(vc) + "' failed and was skipped (other vehicles/categories unaffected): " + ex);
        }

        /// <summary>
        /// Capture with retries: a vehicle captured mid-spawn may throw in one category (half
        /// initialised). Such a capture is dropped and retried on the next scans; after
        /// MaxCaptureAttempts the vehicle is kept with the categories that did capture (the
        /// failed ones stay null and every apply/restore skips them).
        /// </summary>
        private VehicleRecord TryCapture(VehicleController vc)
        {
            bool incomplete;
            VehicleRecord record;
            try
            {
                record = Capture(vc, out incomplete);
            }
            catch (Exception ex)
            {
                // The wheel/axle pass itself threw: nothing usable, always retry later.
                LogFault("Capture", vc, ex);
                return null;
            }
            if (record == null)
            {
                return null;
            }
            if (incomplete)
            {
                int failures;
                _captureFailures.TryGetValue(vc, out failures);
                failures++;
                if (failures < MaxCaptureAttempts)
                {
                    _captureFailures[vc] = failures;
                    return null;
                }
            }
            _captureFailures.Remove(vc);
            return record;
        }

        /// <summary>
        /// Snapshot stock values. Returns null while the vehicle has no initialised wheels
        /// yet, so it is retried on the next scan instead of being recorded as empty.
        /// </summary>
        private static VehicleRecord Capture(VehicleController vc, out bool incomplete)
        {
            incomplete = false;
            if (vc.powertrain == null || vc.powertrain.wheelGroups == null)
            {
                return null;
            }

            // Front/rear is decided relative to the mean wheel position, not the transform
            // origin: many prefabs put the pivot at an axle, which would call every wheel "front".
            float sumZ = 0f;
            int count = 0;
            foreach (WheelGroup group in vc.powertrain.wheelGroups)
            {
                if (group == null)
                {
                    continue;
                }
                foreach (WheelComponent wc in group.Wheels)
                {
                    if (wc != null && wc.wheelUAPI != null)
                    {
                        sumZ += vc.transform.InverseTransformPoint(wc.wheelUAPI.transform.position).z;
                        count++;
                    }
                }
            }
            if (count == 0)
            {
                return null;
            }
            float meanZ = sumZ / count;

            var record = new VehicleRecord { Vc = vc };
            foreach (WheelGroup group in vc.powertrain.wheelGroups)
            {
                if (group == null)
                {
                    continue;
                }
                bool groupFront = false;
                bool groupFrontKnown = false;
                foreach (WheelComponent wc in group.Wheels)
                {
                    WheelUAPI uapi = wc != null ? wc.wheelUAPI : null;
                    if (uapi == null)
                    {
                        continue;
                    }
                    bool front = vc.transform.InverseTransformPoint(uapi.transform.position).z > meanZ;
                    if (!groupFrontKnown)
                    {
                        groupFront = front;
                        groupFrontKnown = true;
                    }
                    record.Wheels[uapi] = new WheelData
                    {
                        IsFront = front,
                        IsLeft = uapi.transform.localPosition.x < 0f,
                        SpringForce = uapi.SpringMaxForce,
                        SpringLength = uapi.SpringMaxLength,
                        BumpRate = uapi.DamperBumpRate,
                        ReboundRate = uapi.DamperReboundRate
                    };
                }
                record.Groups[group] = new GroupData
                {
                    IsFront = groupFront,
                    ArbForce = group.antiRollBarForce
                };
            }

            // Each category on its own: one that throws (a half-initialised vehicle mid-spawn)
            // is left null and logged; the others still capture. TryCapture decides on retries.
            try { record.Assists = CreateAssistHandles(vc); }
            catch (Exception ex) { record.Assists = null; incomplete = true; LogFault("Assists capture", vc, ex); }
            return record;
        }

        internal enum Category
        {
            Suspension
        }

        /// <summary>
        /// Re-read a category's stock values on every tracked vehicle. Only called while
        /// that category is NOT applied (its fields hold the game's values, never ours), so
        /// this can never capture tuned values. Without it the baseline was frozen at first
        /// sight: a value the game changed later (while the category was off) was scaled from
        /// the stale number on enable and overwritten with it on disable. Runs on a toggle,
        /// not per tick.
        /// </summary>
        private void RefreshBaselines(Category category)
        {
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleRecord r = kv.Value;
                if (kv.Key == null || r.Vc == null)
                {
                    continue;
                }
                try
                {
                    RefreshBaseline(r, category);
                }
                catch (Exception ex)
                {
                    LogFault(category + " baseline refresh", kv.Key, ex);
                }
            }
        }

        private static void RefreshBaseline(VehicleRecord r, Category category)
        {
            switch (category)
            {
                case Category.Suspension:
                    foreach (KeyValuePair<WheelUAPI, WheelData> wk in r.Wheels)
                    {
                        WheelUAPI u = wk.Key;
                        if (u == null)
                        {
                            continue;
                        }
                        wk.Value.SpringForce = u.SpringMaxForce;
                        wk.Value.SpringLength = u.SpringMaxLength;
                        wk.Value.BumpRate = u.DamperBumpRate;
                        wk.Value.ReboundRate = u.DamperReboundRate;
                    }
                    foreach (KeyValuePair<WheelGroup, GroupData> gk in r.Groups)
                    {
                        if (gk.Key != null)
                        {
                            gk.Value.ArbForce = gk.Key.antiRollBarForce;
                        }
                    }
                    break;
            }
        }

        // --------------------------------------------------------------- readouts

        public enum Readout
        {
            SpringForce,
            RideHeight,
            BumpRate,
            ReboundRate,
            ArbForce
        }

        /// <summary>Mean stock baseline over tracked vehicles for one axle (0 when none tracked).</summary>
        public float MeanBaseline(Readout kind, bool front)
        {
            float sum = 0f;
            int count = 0;
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleRecord r = kv.Value;
                if (kind == Readout.ArbForce)
                {
                    foreach (KeyValuePair<WheelGroup, GroupData> gk in r.Groups)
                    {
                        if (gk.Key != null && gk.Value.IsFront == front)
                        {
                            sum += gk.Value.ArbForce;
                            count++;
                        }
                    }
                    continue;
                }
                foreach (KeyValuePair<WheelUAPI, WheelData> wk in r.Wheels)
                {
                    if (wk.Key == null || wk.Value.IsFront != front)
                    {
                        continue;
                    }
                    switch (kind)
                    {
                        case Readout.SpringForce: sum += wk.Value.SpringForce; break;
                        case Readout.RideHeight: sum += wk.Value.SpringLength; break;
                        case Readout.BumpRate: sum += wk.Value.BumpRate; break;
                        case Readout.ReboundRate: sum += wk.Value.ReboundRate; break;
                    }
                    count++;
                }
            }
            return count == 0 ? 0f : sum / count;
        }

        // --------------------------------------------------------------- apply passes

        /// <summary>The stable identity a target selection stores (the GameObject name).</summary>
        public static string VehicleName(VehicleController vc)
        {
            return vc != null && vc.gameObject != null ? vc.gameObject.name : "";
        }

        // ----------------------------------------------------- driven-vehicle pick

        /// <summary>
        /// The vehicle the player is driving: most live input above the dead zone, else
        /// the last vehicle that had input (a stalled/off engine keeps the pick), else a
        /// running engine, else the fastest, else the first tracked. Allocation-free.
        ///
        /// "Live" is input that changed recently: the game's FSM freezes a parked car's
        /// input at its exit values (handbrake held, brakes last pressed, …), so raw
        /// input alone lets a parked car steal the pick from a hands-off player. A frozen
        /// value stays live only for the 2 s hold window after its last change.
        /// </summary>
        private VehicleController _lastDriven;

        public const float InputDeadZone = 0.05f;    // input below this is noise / hands-off
        public const float InputHoldSeconds = 2f;    // unchanged input counts as live this long after its last change
        public const float InputChangeEpsilon = 0.02f;

        /// <summary>
        /// Samples one vehicle's input for the pick: records the value and reports whether
        /// it counts as live driving right now. Pure (only the caller's state fields).
        /// </summary>
        public static bool UpdateInputLiveness(float input, float now, ref float prev, ref bool seen, ref float lastChange, out bool live)
        {
            bool changed = !seen || Mathf.Abs(input - prev) > InputChangeEpsilon;
            seen = true;
            prev = input;
            if (changed && input > InputDeadZone)
            {
                lastChange = now;
            }
            live = input > InputDeadZone && (changed || now - lastChange <= InputHoldSeconds);
            return changed;
        }

        public VehicleController FindDrivenVehicle()
        {
            float now = Time.unscaledTime;
            VehicleController driven = null;    // most live input above the dead zone
            float bestInput = 0f;
            VehicleController running = null;   // first tracked vehicle with a running engine
            VehicleController fastest = null;
            float bestSpeed = 0f;
            VehicleController first = null;
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleController vc = kv.Key;
                VehicleRecord r = kv.Value;
                if (vc == null || r == null || r.Vc == null)
                {
                    continue;
                }
                if (first == null)
                {
                    first = vc;
                }
                float input = Mathf.Abs(vc.input.Steering) + vc.input.Throttle + vc.input.Brakes + vc.input.Handbrake;
                bool live;
                UpdateInputLiveness(input, now, ref r.InputPrev, ref r.InputSeen, ref r.InputLastChange, out live);
                if (live && input > bestInput)
                {
                    bestInput = input;
                    driven = vc;
                }
                if (running == null && vc.powertrain != null && vc.powertrain.engine != null && vc.powertrain.engine.OutputRPM > 10f)
                {
                    running = vc;
                }
                if (vc.Speed > bestSpeed)
                {
                    bestSpeed = vc.Speed;
                    fastest = vc;
                }
            }
            if (driven != null)
            {
                _lastDriven = driven;
                return driven;
            }
            // No live input anywhere: stay on the last car the player drove (its engine
            // may have stalled, or the panel is open and hands are on the mouse).
            if (_lastDriven != null)
            {
                VehicleRecord r;
                if (_records.TryGetValue(_lastDriven, out r) && r != null && r.Vc != null)
                {
                    return _lastDriven;
                }
                _lastDriven = null;
            }
            if (running != null)
            {
                return running;
            }
            return bestSpeed > 0.01f ? fastest : first;
        }

        /// <summary>Does the current target selection cover this vehicle?</summary>
        public bool IsTarget(VehicleController vc)
        {
            switch (TargetSettings.Mode)
            {
                case TargetMode.LastDriven:
                    return vc != null && vc == FindDrivenVehicle();
                case TargetMode.Selected:
                    return vc != null && string.Equals(TargetSettings.SelectedName, VehicleName(vc), StringComparison.Ordinal);
                default:
                    return true;
            }
        }

        /// <summary>Names of every tracked vehicle, in first-seen order (panel list; UI path).</summary>
        public List<string> TrackedNames()
        {
            List<string> names = new List<string>(_order.Count);
            for (int i = 0; i < _order.Count; i++)
            {
                VehicleRecord r = _order[i];
                if (r != null && r.Vc != null && !names.Contains(VehicleName(r.Vc)))
                {
                    names.Add(VehicleName(r.Vc));
                }
            }
            return names;
        }

        /// <summary>Vehicles the current target selection covers (the panel status lines).</summary>
        public int TargetedCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
                {
                    if (kv.Key != null && kv.Value.Vc != null && IsTarget(kv.Key))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// One category's per-record pass under the current target: targets get the
        /// category applied (idempotently) and their bit set; records that were
        /// applied but are no longer targets get restored and their bit cleared.
        /// Flag first: a pass that throws half-way has still written some fields,
        /// and the flag is what makes OFF restore them.
        /// </summary>
        private void TargetPass<T>(AppliedCat cat, Action<VehicleRecord, T> apply, T preset, Action<VehicleRecord> restore)
        {
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleRecord r = kv.Value;
                if (kv.Key == null || r == null || r.Vc == null)
                {
                    continue;
                }
                if (IsTarget(kv.Key))
                {
                    r.Applied |= cat;
                    try { apply(r, preset); }
                    catch (Exception ex) { LogFault(cat + " apply", kv.Key, ex); }
                }
                else if ((r.Applied & cat) != 0)
                {
                    r.Applied &= ~cat;
                    try { restore(r); }
                    catch (Exception ex) { LogFault(cat + " restore", kv.Key, ex); }
                }
            }
        }

        private void TargetPass(AppliedCat cat, Action<VehicleRecord> apply, Action<VehicleRecord> restore)
        {
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleRecord r = kv.Value;
                if (kv.Key == null || r == null || r.Vc == null)
                {
                    continue;
                }
                if (IsTarget(kv.Key))
                {
                    r.Applied |= cat;
                    try { apply(r); }
                    catch (Exception ex) { LogFault(cat + " apply", kv.Key, ex); }
                }
                else if ((r.Applied & cat) != 0)
                {
                    r.Applied &= ~cat;
                    try { restore(r); }
                    catch (Exception ex) { LogFault(cat + " restore", kv.Key, ex); }
                }
            }
        }

        /// <summary>Restore one category on every record that has it applied.</summary>
        private void RestorePass(AppliedCat cat, Action<VehicleRecord> restore)
        {
            foreach (KeyValuePair<VehicleController, VehicleRecord> kv in _records)
            {
                VehicleRecord r = kv.Value;
                if (r != null && (r.Applied & cat) != 0)
                {
                    r.Applied &= ~cat;
                    try { restore(r); }
                    catch (Exception ex) { LogFault(cat + " restore", kv.Key, ex); }
                }
            }
        }

        /// <summary>
        /// Restore only the categories this tuner actually applied. Writing captured
        /// values for a category that was never on would clobber anything the game
        /// changed on those fields since capture.
        /// </summary>
        private void RestoreAll()
        {
            PurgeDead();
            if (_assistsApplied) { RestoreAllAssists(); }
            if (_suspApplied) { RestoreAllSuspension(); }
            _suspApplied = _assistsApplied = false;
        }
    }
}
