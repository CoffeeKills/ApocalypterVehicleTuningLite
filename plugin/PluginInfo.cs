namespace ApocalypterVehicleTuningLite
{
    internal static class PluginInfo
    {
        // Own GUID (not the full mod's): this lite build has its own BepInEx config
        // file and must never share settings with — or collide with — the full mod.
        public const string PLUGIN_GUID = "dev.apocalypter.vehicletuninglite";
        public const string PLUGIN_NAME = "Vehicle Tuning Lite";
        // BepInEx 5 parses this with System.Version: numeric-only, no pre-release
        // tags. "-alpha" makes BepInEx skip the whole plugin at load.
        public const string PLUGIN_VERSION = "1.1.0";
    }
}
