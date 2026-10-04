namespace ApocalypterVehicleTuningLite.Settings
{
    /// <summary>
    /// Panel preferences. Pure UI state, never a vehicle change.
    /// Mirrored to [UI] by ModConfig.
    /// </summary>
    public static class UiSettings
    {
        public const float DefaultScale = 1f;
        public const float DefaultAlpha = 1f;

        /// <summary>Default false: the game keeps running while the panel is open.</summary>
        public static bool FreezeWhileOpen = false;
        public static float PanelScale = DefaultScale;
        public static float PanelWidth = Limits.PanelWidthDefault;
        public static float PanelAlpha = DefaultAlpha;
        public static int LastTab;

        public static void ResetPanel()
        {
            FreezeWhileOpen = false;
            PanelScale = DefaultScale;
            PanelWidth = Limits.PanelWidthDefault;
            PanelAlpha = DefaultAlpha;
        }

        /// <summary>A stored tab index, clamped into 0..3 (garbage = 0).</summary>
        public static int ClampTab(int tab)
        {
            return tab < Limits.LastTabMin || tab > Limits.LastTabMax ? 0 : tab;
        }
    }
}
