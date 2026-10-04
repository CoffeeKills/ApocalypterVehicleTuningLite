using System;

namespace ApocalypterVehicleTuningLite.Settings
{
    /// <summary>What the tuning applies to.</summary>
    public enum TargetMode
    {
        All,          // every tracked vehicle
        LastDriven,   // the player's vehicle (the driven-vehicle pick; the DEFAULT)
        Selected      // one tracked vehicle, by GameObject name
    }

    /// <summary>
    /// Runtime target selection, pushed from the config and read by the tuner on
    /// every apply pass. Selecting by NAME survives scene loads and respawns
    /// (a respawned vehicle has the same prefab name); a name with no tracked
    /// vehicle simply applies to nothing until one spawns.
    /// </summary>
    public static class TargetSettings
    {
        // The lite default: tune the player's vehicle, not every car in the world.
        public static TargetMode Mode = TargetMode.LastDriven;
        public static string SelectedName = "";

        public static string ModeName(TargetMode m)
        {
            switch (m)
            {
                case TargetMode.LastDriven: return "Last driven";
                case TargetMode.Selected: return "Selected vehicle";
                default: return "All vehicles";
            }
        }

        /// <summary>Name-only parse (case/space tolerant), anything else falls back to All.</summary>
        public static TargetMode Parse(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return TargetMode.All;
            }
            string t = s.Trim().ToLowerInvariant();
            if (t == "all" || t == "all vehicles")
            {
                return TargetMode.All;
            }
            if (t == "lastdriven" || t == "last driven")
            {
                return TargetMode.LastDriven;
            }
            if (t == "selected" || t == "selected vehicle")
            {
                return TargetMode.Selected;
            }
            return TargetMode.All;
        }
    }
}
