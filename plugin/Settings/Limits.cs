namespace ApocalypterVehicleTuningLite.Settings
{
    /// <summary>
    /// Single source of truth for every tunable range. Used by the config file
    /// (AcceptableValueRange) and by the in-game sliders, so a hand-edited
    /// config value can never exceed what the panel can show.
    /// Widening a range never invalidates a stored value, so no migration is
    /// ever needed for range changes.
    /// </summary>
    public static class Limits
    {
        // Steering.
        public const float RateMin = 0.1f, RateMax = 5f;
        public const float SmoothMin = 0.1f, SmoothMax = 3f;
        public const float SlipMin = 0f, SlipMax = 25f;
        public const float OppLockMin = 1f, OppLockMax = 5f;
        public const float LinExpMin = 0.1f, LinExpMax = 5f;

        // Suspension.
        public const float SuspFactorMin = 0.1f, SuspFactorMax = 5f;

        // Assists (ABS/TCS).
        public const float SlipThrMin = 0.01f, SlipThrMax = 1f;
        public const float CutoffSpeedMin = 0f, CutoffSpeedMax = 20f;
        public const float CutMultMin = 0f, CutMultMax = 1f;

        // Panel.
        public const float PanelScaleMin = 0.3f, PanelScaleMax = 3f;
        public const float PanelWidthMin = 300f, PanelWidthMax = 1000f;
        public const float PanelWidthDefault = 460f;
        public const float PanelAlphaMin = 0.15f, PanelAlphaMax = 1f;
        public const int LastTabMin = 0, LastTabMax = 3;
    }
}
