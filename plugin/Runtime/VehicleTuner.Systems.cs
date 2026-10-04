using System.Collections.Generic;
using ApocalypterVehicleTuningLite.Settings;
using NWH.Common.Vehicles;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.Powertrain.Wheel;
using UnityEngine;

namespace ApocalypterVehicleTuningLite.Runtime
{
    /// <summary>Per-system apply/restore. All hot paths are allocation-free.</summary>
    public sealed partial class VehicleTuner
    {
        // ---------------------------------------------------------------- suspension

        private void ApplyAllSuspension()
        {
            TargetPass(AppliedCat.Suspension, ApplySuspension, RestoreSuspension);
        }

        private void RestoreAllSuspension()
        {
            RestorePass(AppliedCat.Suspension, RestoreSuspension);
        }

        private static void ApplySuspension(VehicleRecord r)
        {
            foreach (KeyValuePair<WheelUAPI, WheelData> wk in r.Wheels)
            {
                WheelUAPI u = wk.Key;
                if (u == null)
                {
                    continue;
                }
                WheelData b = wk.Value;
                u.SpringMaxForce = b.SpringForce * SuspensionSettings.Spring(b.IsFront);
                u.SpringMaxLength = b.SpringLength * SuspensionSettings.RideHeight(b.IsFront);
                u.DamperBumpRate = b.BumpRate * SuspensionSettings.Bump(b.IsFront);
                u.DamperReboundRate = b.ReboundRate * SuspensionSettings.Rebound(b.IsFront);
            }
            foreach (KeyValuePair<WheelGroup, GroupData> gk in r.Groups)
            {
                WheelGroup g = gk.Key;
                if (g == null)
                {
                    continue;
                }
                g.antiRollBarForce = gk.Value.ArbForce * SuspensionSettings.Arb(gk.Value.IsFront);
            }
        }

        private static void RestoreSuspension(VehicleRecord r)
        {
            foreach (KeyValuePair<WheelUAPI, WheelData> wk in r.Wheels)
            {
                WheelUAPI u = wk.Key;
                if (u == null)
                {
                    continue;
                }
                u.SpringMaxForce = wk.Value.SpringForce;
                u.SpringMaxLength = wk.Value.SpringLength;
                u.DamperBumpRate = wk.Value.BumpRate;
                u.DamperReboundRate = wk.Value.ReboundRate;
            }
            foreach (KeyValuePair<WheelGroup, GroupData> gk in r.Groups)
            {
                WheelGroup g = gk.Key;
                if (g != null)
                {
                    g.antiRollBarForce = gk.Value.ArbForce;
                }
            }
        }

    }
}
