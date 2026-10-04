using System;
using ApocalypterVehicleTuningLite.Settings;
using BepInEx.Configuration;

namespace ApocalypterVehicleTuningLite.Persistence
{
    /// <summary>
    /// The BepInEx config bridge: every Steering / Suspension / Assists / UI key lives
    /// here, bound with ranges, wired to the runtime and mirrored back on Save. No
    /// migration machinery: this mod's config file is its own (fresh GUID), so there
    /// are no legacy files to migrate from.
    /// </summary>
    public static class ModConfig
    {
        public static event Action SettingsChanged;

        private static ConfigFile _config;
        private static bool _syncing;

        // Steering
        private static ConfigEntry<bool> _steerEnabled;
        private static ConfigEntry<string> _steerPreset;
        private static ConfigEntry<bool> _matchGameSteeringSpeed;
        private static ConfigEntry<string> _steerBasedOn;
        private static ConfigEntry<float> _steerRate, _steerSmoothing, _steerSlip, _steerOppLock, _steerLinExp;
        private static ConfigEntry<bool> _steerUseVehicleCurve;
        private static ConfigEntry<string> _steerLockCurve, _steerReturnCurve;
        private static ConfigEntry<bool> _steerTraction, _steerLinearityOverride;

        // Suspension
        private static ConfigEntry<bool> _suspEnabled;
        private static ConfigEntry<string> _suspPreset;
        private static ConfigEntry<bool> _suspSplit;
        private static ConfigEntry<string> _suspBasedOn;
        private static ConfigEntry<float> _suspSpringF, _suspSpringR, _suspHeightF, _suspHeightR,
            _suspBumpF, _suspBumpR, _suspReboundF, _suspReboundR, _suspArbF, _suspArbR;

        // Assists
        private static ConfigEntry<bool> _assistsEnabled;
        private static ConfigEntry<string> _assistsPreset, _assistsBasedOn;
        private static ConfigEntry<bool> _assistsAbsEnabled, _assistsTcsEnabled;
        private static ConfigEntry<float> _assistsAbsThr, _assistsAbsCut, _assistsAbsMult,
            _assistsTcsThr, _assistsTcsCut, _assistsTcsMult;

        // UI + general
        private static ConfigEntry<bool> _uiFreeze;
        private static ConfigEntry<float> _uiScale, _uiWidth, _uiAlpha;
        private static ConfigEntry<int> _uiLastTab;
        private static ConfigEntry<string> _toggleKey;
        // Read by Apocasetter via Chainloader (not wired to OnSettingChanged — we never read it).
        private static ConfigEntry<bool> _apocasetter;

        public static string ToggleKeyString
        {
            get { return _toggleKey != null ? _toggleKey.Value : "F7"; }
        }

        /// <summary>Write the panel hotkey (the Settings tab rebinder calls this) and save.</summary>
        public static void SetToggleKey(string keyName)
        {
            if (_toggleKey == null || string.IsNullOrEmpty(keyName))
            {
                return;
            }
            _toggleKey.Value = keyName;
            _config.Save();
        }

        private static ConfigEntry<float> BindRange(string section, string key, float def, float min, float max, string description)
        {
            return _config.Bind(section, key, def,
                new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
        }

        private static ConfigEntry<int> BindIntRange(string section, string key, int def, int min, int max, string description)
        {
            return _config.Bind(section, key, def,
                new ConfigDescription(description, new AcceptableValueRange<int>(min, max)));
        }

        public static void Load(ConfigFile config)
        {
            _config = config;

            // One file write at the end instead of one per Bind.
            bool autoSave = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;

            BindSteering();
            BindSuspension();
            BindAssists();
            _toggleKey = config.Bind("UI", "ToggleKey", "F7",
                "Hotkey that opens/closes the tuning panel (a Unity KeyCode name, e.g. F7, F8, Home). Rebind in the Settings tab; applies immediately.");
            BindUi();
            WireAll();
            _apocasetter = config.Bind("General", "Apocasetter", true,
                "Show this mod in the Apocasetter Mods menu (requires Apocasetter installed).");

            PushAllToRuntime();

            config.SaveOnConfigSet = autoSave;
            config.Save();
        }

        // ---------------------------------------------------------------- binding

        private static void BindSteering()
        {
            _steerEnabled = _config.Bind("Steering", "Enabled", false,
                "Master switch for the mod's steering (opt-in).");
            _steerPreset = _config.Bind("Steering", "Preset", "Custom",
                "Active steering preset: Vanilla, GTA-style Keyboard, Euro Truck, Sim/Race, Drift, Custom.");
            _matchGameSteeringSpeed = _config.Bind("Steering", "MatchGameSteeringSpeed", true,
                "Scale the steering rate with the game's own steering speed setting.");
            _steerBasedOn = _config.Bind("Steering.Custom", "BasedOn", "",
                "Built-in preset the Custom tuning was copied from (restores that preset's curves). Leave empty for none.");
            _steerRate = BindRange("Steering.Custom", "RateMultiplier", 1f, Limits.RateMin, Limits.RateMax,
                "Multiplier on the vehicle's configured deg/s steering rate.");
            _steerSmoothing = BindRange("Steering.Custom", "SmoothingScale", 1f, Limits.SmoothMin, Limits.SmoothMax,
                "Multiplier on the vehicle's smoothing time.");
            _steerUseVehicleCurve = _config.Bind("Steering.Custom", "UseVehicleCurve", true,
                "Use each vehicle's own speed-sensitive steering curve instead of the custom LockCurve.");
            _steerLockCurve = _config.Bind("Steering.Custom", "LockCurve", EditableCurve.DefaultLockCurveText,
                "Lock-at-speed curve: \"x:y;x:y\" points (x = speed/50, y = fraction of max steer, 2-8 points).");
            _steerReturnCurve = _config.Bind("Steering.Custom", "ReturnCurve", EditableCurve.DefaultReturnCurveText,
                "Return-to-center curve: \"x:y;x:y\" points (y = fraction of the steer-in rate while unwinding; 0 at rest = holds the wheels).");
            _steerTraction = _config.Bind("Steering.Custom", "TractionClampEnabled", true,
                "Clamp the steer angle so front tires stay near their peak-grip slip angle.");
            _steerSlip = BindRange("Steering.Custom", "SlipAngleDeg", 8.5f, Limits.SlipMin, Limits.SlipMax,
                "Peak-grip tire slip angle in degrees.");
            _steerOppLock = BindRange("Steering.Custom", "OppositeLockBoost", 1.75f, Limits.OppLockMin, Limits.OppLockMax,
                "Steering rate boost while applying opposite lock.");
            _steerLinearityOverride = _config.Bind("Steering.Custom", "LinearityOverride", false,
                "Replace the vehicle's input linearity curve with pow(|input|, exponent).");
            _steerLinExp = BindRange("Steering.Custom", "LinearityExponent", 1f, Limits.LinExpMin, Limits.LinExpMax,
                "Input linearity exponent (1 = linear, below 1 = sharper near center, above 1 = gentler).");
        }

        private static void BindSuspension()
        {
            _suspEnabled = _config.Bind("Suspension", "Enabled", false,
                "Master switch for suspension tuning (opt-in; overrides per-vehicle tuning).");
            _suspPreset = _config.Bind("Suspension", "Preset", "Stock",
                "Active suspension preset: Stock, Comfort, Sport, Off-road, Race, Custom.");
            _suspSplit = _config.Bind("Suspension", "SplitFrontRear", false,
                "Show separate front/rear sliders in the panel.");
            _suspBasedOn = _config.Bind("Suspension.Custom", "BasedOn", "",
                "Built-in preset the Custom tuning was copied from. Leave empty for none.");
            _suspSpringF = BindRange("Suspension.Custom", "SpringFront", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Front spring stiffness factor.");
            _suspSpringR = BindRange("Suspension.Custom", "SpringRear", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Rear spring stiffness factor.");
            _suspHeightF = BindRange("Suspension.Custom", "RideHeightFront", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Front ride height factor.");
            _suspHeightR = BindRange("Suspension.Custom", "RideHeightRear", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Rear ride height factor.");
            _suspBumpF = BindRange("Suspension.Custom", "BumpFront", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Front bump damping factor.");
            _suspBumpR = BindRange("Suspension.Custom", "BumpRear", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Rear bump damping factor.");
            _suspReboundF = BindRange("Suspension.Custom", "ReboundFront", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Front rebound damping factor.");
            _suspReboundR = BindRange("Suspension.Custom", "ReboundRear", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Rear rebound damping factor.");
            _suspArbF = BindRange("Suspension.Custom", "ArbFront", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Front anti-roll bar factor.");
            _suspArbR = BindRange("Suspension.Custom", "ArbRear", 1f, Limits.SuspFactorMin, Limits.SuspFactorMax, "Rear anti-roll bar factor.");
        }

        private static void BindAssists()
        {
            _assistsEnabled = _config.Bind("Assists", "Enabled", false, "Master switch for ABS/TCS assists (opt-in).");
            _assistsPreset = _config.Bind("Assists", "Preset", "Off", "Active assists preset: Off, Standard, Sport, Off-road, Race, Custom.");
            _assistsBasedOn = _config.Bind("Assists.Custom", "BasedOn", "", "Built-in preset the Custom tuning was copied from.");
            _assistsAbsEnabled = _config.Bind("Assists.Custom", "AbsEnabled", false, "Anti-lock braking on.");
            _assistsAbsThr = BindRange("Assists.Custom", "AbsSlipThreshold", 0.1f, Limits.SlipThrMin, Limits.SlipThrMax, "ABS slip threshold.");
            _assistsAbsCut = BindRange("Assists.Custom", "AbsCutoffSpeed", 1f, Limits.CutoffSpeedMin, Limits.CutoffSpeedMax, "ABS cutoff speed (m/s).");
            _assistsAbsMult = BindRange("Assists.Custom", "AbsCutMultiplier", 0.01f, Limits.CutMultMin, Limits.CutMultMax, "Brake modifier while ABS releases.");
            _assistsTcsEnabled = _config.Bind("Assists.Custom", "TcsEnabled", false, "Traction control on.");
            _assistsTcsThr = BindRange("Assists.Custom", "TcsSlipThreshold", 0.1f, Limits.SlipThrMin, Limits.SlipThrMax, "TCS slip threshold.");
            _assistsTcsCut = BindRange("Assists.Custom", "TcsCutoffSpeed", 2f, Limits.CutoffSpeedMin, Limits.CutoffSpeedMax, "TCS cutoff speed (m/s).");
            _assistsTcsMult = BindRange("Assists.Custom", "TcsCutMultiplier", 0.01f, Limits.CutMultMin, Limits.CutMultMax, "Power modifier while TCS cuts.");
        }

        private static void BindUi()
        {
            _uiFreeze = _config.Bind("UI", "FreezeWhileOpen", false,
                "Freeze the game while the panel is open. Off = keep driving with the panel open.");
            _uiScale = BindRange("UI", "PanelScale", 1f, Limits.PanelScaleMin, Limits.PanelScaleMax, "Panel interface size.");
            _uiWidth = BindRange("UI", "PanelWidth", Limits.PanelWidthDefault, Limits.PanelWidthMin, Limits.PanelWidthMax, "Panel width in reference pixels.");
            _uiAlpha = BindRange("UI", "PanelAlpha", 1f, Limits.PanelAlphaMin, Limits.PanelAlphaMax, "Panel opacity (1 = opaque).");
            _uiLastTab = BindIntRange("UI", "LastTab", 0, Limits.LastTabMin, Limits.LastTabMax, "Tab the panel opens on (remembered).");
        }

        // ---------------------------------------------------------------- wiring

        private static void Wire<T>(ConfigEntry<T> entry)
        {
            entry.SettingChanged += (sender, e) => OnEntryChanged(entry);
        }

        private static void WireAll()
        {
            Wire(_steerEnabled); Wire(_steerPreset); Wire(_matchGameSteeringSpeed); Wire(_steerBasedOn);
            Wire(_steerRate); Wire(_steerSmoothing); Wire(_steerUseVehicleCurve); Wire(_steerLockCurve);
            Wire(_steerReturnCurve); Wire(_steerTraction);
            Wire(_steerSlip); Wire(_steerOppLock); Wire(_steerLinearityOverride); Wire(_steerLinExp);
            Wire(_suspEnabled); Wire(_suspPreset); Wire(_suspSplit); Wire(_suspBasedOn);
            Wire(_suspSpringF); Wire(_suspSpringR); Wire(_suspHeightF); Wire(_suspHeightR);
            Wire(_suspBumpF); Wire(_suspBumpR); Wire(_suspReboundF); Wire(_suspReboundR);
            Wire(_suspArbF); Wire(_suspArbR);
            Wire(_assistsEnabled); Wire(_assistsPreset); Wire(_assistsBasedOn);
            Wire(_assistsAbsEnabled); Wire(_assistsAbsThr); Wire(_assistsAbsCut); Wire(_assistsAbsMult);
            Wire(_assistsTcsEnabled); Wire(_assistsTcsThr); Wire(_assistsTcsCut); Wire(_assistsTcsMult);
            Wire(_uiFreeze); Wire(_uiScale); Wire(_uiWidth); Wire(_uiAlpha); Wire(_uiLastTab);
        }

        // ---------------------------------------------------------------- save / push

        public static void Save()
        {
            if (_config == null)
            {
                return;
            }
            bool autoSave = _config.SaveOnConfigSet;
            _config.SaveOnConfigSet = false;
            _syncing = true;
            try
            {
                MirrorRuntimeToEntries();
            }
            finally
            {
                _syncing = false;
                _config.SaveOnConfigSet = autoSave;
            }
            _config.Save();
        }

        /// <summary>Runtime holders -> ConfigEntries (no file write). Caller sets _syncing.</summary>
        private static void MirrorRuntimeToEntries()
        {
            _steerEnabled.Value = SteeringSettings.Enabled;
            _steerPreset.Value = SteeringSettings.ActivePreset != null ? SteeringSettings.ActivePreset.Name : "Custom";
            _matchGameSteeringSpeed.Value = SteeringSettings.MatchGameSteeringSpeed;
            SteeringPreset sc = SteeringPreset.Custom;
            _steerBasedOn.Value = sc.BasedOn ?? "";
            _steerRate.Value = sc.RateMultiplier;
            _steerSmoothing.Value = sc.SmoothingScale;
            _steerUseVehicleCurve.Value = sc.UseVehicleCurve;
            _steerLockCurve.Value = sc.LockCurve != null ? sc.LockCurve.Serialize() : EditableCurve.DefaultLockCurveText;
            _steerReturnCurve.Value = sc.ReturnCurve != null ? sc.ReturnCurve.Serialize() : EditableCurve.DefaultReturnCurveText;
            _steerTraction.Value = sc.TractionClampEnabled;
            _steerSlip.Value = sc.SlipAngleDeg;
            _steerOppLock.Value = sc.OppositeLockBoost;
            _steerLinearityOverride.Value = sc.LinearityOverride;
            _steerLinExp.Value = sc.LinearityExponent;

            _suspEnabled.Value = SuspensionSettings.Enabled;
            _suspPreset.Value = SuspensionSettings.ActivePreset != null ? SuspensionSettings.ActivePreset.Name : "Stock";
            _suspSplit.Value = SuspensionSettings.SplitFrontRear;
            SuspensionPreset uc = SuspensionPreset.Custom;
            _suspBasedOn.Value = uc.BasedOn ?? "";
            _suspSpringF.Value = uc.SpringFront;
            _suspSpringR.Value = uc.SpringRear;
            _suspHeightF.Value = uc.RideHeightFront;
            _suspHeightR.Value = uc.RideHeightRear;
            _suspBumpF.Value = uc.BumpFront;
            _suspBumpR.Value = uc.BumpRear;
            _suspReboundF.Value = uc.ReboundFront;
            _suspReboundR.Value = uc.ReboundRear;
            _suspArbF.Value = uc.ArbFront;
            _suspArbR.Value = uc.ArbRear;

            _assistsEnabled.Value = AssistsSettings.Enabled;
            _assistsPreset.Value = AssistsSettings.ActivePreset != null ? AssistsSettings.ActivePreset.Name : "Off";
            AssistsPreset tc = AssistsPreset.Custom;
            _assistsBasedOn.Value = tc.BasedOn ?? "";
            _assistsAbsEnabled.Value = tc.AbsEnabled;
            _assistsAbsThr.Value = tc.AbsSlipThreshold;
            _assistsAbsCut.Value = tc.AbsCutoffSpeed;
            _assistsAbsMult.Value = tc.AbsCutMultiplier;
            _assistsTcsEnabled.Value = tc.TcsEnabled;
            _assistsTcsThr.Value = tc.TcsSlipThreshold;
            _assistsTcsCut.Value = tc.TcsCutoffSpeed;
            _assistsTcsMult.Value = tc.TcsCutMultiplier;

            _uiFreeze.Value = UiSettings.FreezeWhileOpen;
            _uiScale.Value = UiSettings.PanelScale;
            _uiWidth.Value = UiSettings.PanelWidth;
            _uiAlpha.Value = UiSettings.PanelAlpha;
            _uiLastTab.Value = UiSettings.ClampTab(UiSettings.LastTab);
        }

        /// <summary>
        /// An entry changed from outside (a config manager such as Apocasetter's Mods window).
        /// The runtime may hold panel edits that are not saved yet (Save runs when the panel
        /// closes), so the runtime is mirrored into the entries first (no file write), the
        /// externally changed value is re-applied on top, and only then pushed.
        /// </summary>
        private static void OnEntryChanged(ConfigEntryBase changed)
        {
            if (_syncing)
            {
                return;
            }
            object incoming = changed.BoxedValue;
            bool autoSave = _config.SaveOnConfigSet;
            _config.SaveOnConfigSet = false;
            _syncing = true;
            try
            {
                MirrorRuntimeToEntries();
                changed.BoxedValue = incoming;
            }
            finally
            {
                _syncing = false;
                _config.SaveOnConfigSet = autoSave;
            }
            PushAllToRuntime();
            SettingsChanged?.Invoke();
        }

        private static void PushAllToRuntime()
        {
            SteeringSettings.Enabled = _steerEnabled.Value;
            SteeringSettings.SetPresetByName(_steerPreset.Value);
            SteeringSettings.MatchGameSteeringSpeed = _matchGameSteeringSpeed.Value;
            SteeringPreset sc = SteeringPreset.Custom;
            sc.BasedOn = _steerBasedOn.Value ?? "";
            sc.RateMultiplier = _steerRate.Value;
            sc.SmoothingScale = _steerSmoothing.Value;
            sc.TractionClampEnabled = _steerTraction.Value;
            sc.SlipAngleDeg = _steerSlip.Value;
            sc.OppositeLockBoost = _steerOppLock.Value;
            sc.LinearityOverride = _steerLinearityOverride.Value;
            sc.LinearityExponent = _steerLinExp.Value;
            // Restore the BasedOn preset's curves first (fallback for missing or
            // garbage config strings), then override with parsed config values.
            sc.RestoreBaseCurve();
            sc.UseVehicleCurve = _steerUseVehicleCurve.Value;
            EditableCurve lockCurve;
            if (EditableCurve.TryParse(_steerLockCurve.Value, out lockCurve))
            {
                sc.LockCurve = lockCurve;
            }
            EditableCurve returnCurve;
            if (EditableCurve.TryParse(_steerReturnCurve.Value, out returnCurve))
            {
                sc.ReturnCurve = returnCurve;
            }

            SuspensionSettings.Enabled = _suspEnabled.Value;
            SuspensionSettings.SetPresetByName(_suspPreset.Value);
            SuspensionSettings.SplitFrontRear = _suspSplit.Value;
            SuspensionPreset uc = SuspensionPreset.Custom;
            uc.BasedOn = _suspBasedOn.Value ?? "";
            uc.SpringFront = _suspSpringF.Value;
            uc.SpringRear = _suspSpringR.Value;
            uc.RideHeightFront = _suspHeightF.Value;
            uc.RideHeightRear = _suspHeightR.Value;
            uc.BumpFront = _suspBumpF.Value;
            uc.BumpRear = _suspBumpR.Value;
            uc.ReboundFront = _suspReboundF.Value;
            uc.ReboundRear = _suspReboundR.Value;
            uc.ArbFront = _suspArbF.Value;
            uc.ArbRear = _suspArbR.Value;

            AssistsSettings.Enabled = _assistsEnabled.Value;
            AssistsSettings.SetPresetByName(_assistsPreset.Value);
            AssistsPreset tc = AssistsPreset.Custom;
            tc.BasedOn = _assistsBasedOn.Value ?? "";
            tc.AbsEnabled = _assistsAbsEnabled.Value;
            tc.AbsSlipThreshold = _assistsAbsThr.Value;
            tc.AbsCutoffSpeed = _assistsAbsCut.Value;
            tc.AbsCutMultiplier = _assistsAbsMult.Value;
            tc.TcsEnabled = _assistsTcsEnabled.Value;
            tc.TcsSlipThreshold = _assistsTcsThr.Value;
            tc.TcsCutoffSpeed = _assistsTcsCut.Value;
            tc.TcsCutMultiplier = _assistsTcsMult.Value;

            UiSettings.FreezeWhileOpen = _uiFreeze.Value;
            UiSettings.PanelScale = _uiScale.Value;
            UiSettings.PanelWidth = _uiWidth.Value;
            UiSettings.PanelAlpha = _uiAlpha.Value;
            UiSettings.LastTab = UiSettings.ClampTab(_uiLastTab.Value);
        }
    }
}
