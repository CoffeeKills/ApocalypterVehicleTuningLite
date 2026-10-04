using System;
using System.Collections.Generic;
using ApocalypterVehicleTuningLite.Game;
using ApocalypterVehicleTuningLite.Persistence;
using ApocalypterVehicleTuningLite.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace ApocalypterVehicleTuningLite.Runtime
{
    /// <summary>
    /// The tuning panel: a window docked to the right edge, full height, so the game
    /// stays visible (and drivable) beside it. Four tabs: Steering, Suspension, Assists,
    /// Settings. Every category follows the same recipe: master ON/OFF switch, preset
    /// buttons, labelled sliders with hints, live values, "changed" highlight and per-slider
    /// Reset. Moving any slider on a built-in preset copies it into "Custom (Base)" so
    /// presets stay intact. Everything applies live and is saved when the panel closes.
    ///
    /// Layout: every size comes from PanelLayout as a function of the window width and is
    /// re-applied by Relayout(width) to stored RectTransforms, so width/scale/alpha changes
    /// apply live without a rebuild (and without touching the blocker state).
    /// </summary>
    public sealed class SettingsPanel
    {
        private const float Dimmed = 0.35f;

        public const int TabCount = 4;
        public static readonly string[] TabNames =
        {
            "Steering", "Suspension", "Assists", "Settings"
        };
        // Preset book behind each tab (the Settings tab has none).
        private static readonly PresetCategory?[] TabCategory =
        {
            PresetCategory.Steering, PresetCategory.Suspension, PresetCategory.Assists, null
        };

        public GameObject Root;

        private readonly VehicleTuner _tuner;
        private readonly Action _requestClose;
        private readonly Action _onModeChanged;
        private readonly List<Action> _refreshers = new List<Action>();
        private int _targetNameIndex;   // selected-vehicle cycle position (targeting)
        private readonly List<Action<float>> _relayouts = new List<Action<float>>();   // arg: content width
        private bool _suppress;

        private int _tab;
        private readonly Image[] _tabBg = new Image[TabCount];
        private readonly Image[] _tabUnderline = new Image[TabCount];
        private readonly RectTransform[] _tabRt = new RectTransform[TabCount];
        private readonly Text[] _tabLabel = new Text[TabCount];
        private readonly GameObject[] _pages = new GameObject[TabCount];
        private readonly RectTransform[] _pageHosts = new RectTransform[TabCount];
        private readonly ScrollRect[] _scrolls = new ScrollRect[TabCount];

        // Window chrome (relayout targets).
        private CanvasScaler _scaler;
        private RectTransform _win;
        private CanvasGroup _winGroup;
        private GameObject _dim;
        private RectTransform _titleRt, _subtitleRt, _tabBar, _footer, _footerLine, _statusRt;
        private Text _subtitle, _status;
        private RectTransform _allOffRt, _resetRt, _doneRt;
        private Button _allOffButton;
        private Text _allOffLabel;
        private float _appliedWidth = -1f, _appliedScale = -1f;
        private int _appliedScreenW = -1, _appliedScreenH = -1;

        private Button _resetButton;
        private Text _resetLabel;
        private float _resetArmedUntil = -1f;
        private float _allOffArmedUntil = -1f;
        private float _nextStatusRefresh;
        private Action[] _resetActions;
        private string[] _resetLabels;

        public static SettingsPanel Create(VehicleTuner tuner, Action requestClose, Action onModeChanged = null)
        {
            return new SettingsPanel(tuner, requestClose, onModeChanged);
        }

        private SettingsPanel(VehicleTuner tuner, Action requestClose, Action onModeChanged)
        {
            _tuner = tuner;
            _requestClose = requestClose;
            _onModeChanged = onModeChanged;
            _resetActions = new Action[]
            {
                () => SteeringSettings.ResetAll(),
                () => { SuspensionSettings.ResetAll(); _tuner.ReapplyNow(); },
                () => { AssistsSettings.ResetAll(); _tuner.ReapplyNow(); },
                () =>
                {
                    UiSettings.ResetPanel();
                    ModeChanged();
                }
            };
            _resetLabels = new[]
            {
                "Reset all steering", "Reset all suspension", "Reset all assists", "Reset panel settings"
            };
            Build();
            ApplyDisplaySettings();
            ShowTab(UiSettings.ClampTab(UiSettings.LastTab));
            Refresh();
            Root.SetActive(false);
        }

        // ================================================================ skeleton

        private void Build()
        {
            var canvasGo = new GameObject("ApocalypterVehicleTuningLiteCanvas", typeof(RectTransform));
            canvasGo.layer = 5;
            canvasGo.transform.SetParent(_tuner.transform, false);
            Root = canvasGo;

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;   // above every game menu canvas
            _scaler = canvasGo.AddComponent<CanvasScaler>();
            // Mod-owned interface size (the game has no UI-scale setting): at PanelScale 1 this
            // renders exactly like 0.5.0's height-matched 1920x1080 ScaleWithScreenSize scaler.
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            RectTransform rootRt = (RectTransform)canvasGo.transform;

            // Dim layer — Freeze ON only (the 0.5.0 modal: blocks clicks to the game, a click
            // closes). Live mode has NO click catcher: only the window raycasts, the rest of the
            // screen belongs to the game (accepted risk: opened from the pause menu, its buttons
            // stay clickable beside the panel).
            RectTransform dim = UiKit.Make("Dim", rootRt);
            UiKit.Fill(dim);
            Image dimImg = UiKit.Paint(dim, new Color(0f, 0f, 0f, 0.55f), true);
            Button dimBtn = dim.gameObject.AddComponent<Button>();
            dimBtn.transition = Selectable.Transition.None;
            dimBtn.targetGraphic = dimImg;
            dimBtn.navigation = new Navigation { mode = Navigation.Mode.None };
            dimBtn.onClick.AddListener(() => _requestClose());
            _dim = dim.gameObject;

            _win = UiKit.Make("Window", rootRt);
            _win.anchorMin = new Vector2(1f, 0.02f);
            _win.anchorMax = new Vector2(1f, 0.98f);
            _win.pivot = new Vector2(1f, 0.5f);
            _win.anchoredPosition = new Vector2(-10f, 0f);
            _win.sizeDelta = new Vector2(Limits.PanelWidthDefault, 0f);
            UiKit.Paint(_win, UiKit.WindowBg, true);   // eats clicks inside the window only
            _winGroup = _win.gameObject.AddComponent<CanvasGroup>();

            BuildHeader(_win);
            BuildTabs(_win);
            BuildFooter(_win);

            _pages[0] = BuildPage(_win, "SteeringPage", 0, BuildSteering);
            _pages[1] = BuildPage(_win, "SuspensionPage", 1, BuildSuspension);
            _pages[2] = BuildPage(_win, "AssistsPage", 2, BuildAssists);
            _pages[3] = BuildPage(_win, "SettingsPage", 3, BuildPanelTab);
        }

        private void BuildHeader(RectTransform win)
        {
            _titleRt = UiKit.Make("Title", win);
            UiKit.Label(_titleRt, "Vehicle Tuning", 26, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);

            _subtitleRt = UiKit.Make("Subtitle", win);
            _subtitle = UiKit.Label(_subtitleRt, "", 13, UiKit.TextMuted, TextAnchor.UpperLeft, FontStyle.Normal, true);
            _refreshers.Add(() =>
            {
                _subtitle.text = UiSettings.FreezeWhileOpen
                    ? "Changes apply instantly and are saved when you close. " + ModConfig.ToggleKeyString
                      + ", Esc or a click outside closes. Keys 1-4 switch tabs."
                    : "Changes apply instantly; you can keep driving. Saved when you close. " + ModConfig.ToggleKeyString
                      + " or Esc closes. Keys 1-4 switch tabs.";
            });

            Button close = UiKit.MakeButton(win, "Close", "X", UiKit.RowBase, 18, () => _requestClose(), out Text _);
            RectTransform crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.sizeDelta = new Vector2(PanelLayout.CloseSize, PanelLayout.CloseSize);
            crt.anchoredPosition = new Vector2(-12f, -12f);
        }

        private void BuildTabs(RectTransform win)
        {
            _tabBar = UiKit.Make("Tabs", win);
            for (int i = 0; i < TabCount; i++)
            {
                int index = i;
                Button b = UiKit.MakeButton(_tabBar, "Tab_" + TabNames[i], TabNames[i], UiKit.RowBase, 14, () => ShowTab(index), out _tabLabel[i]);
                _tabRt[i] = (RectTransform)b.transform;
                _tabBg[i] = (Image)b.targetGraphic;
                RectTransform under = UiKit.Bottom(UiKit.Make("Underline", _tabRt[i]), 0f, 3f);
                _tabUnderline[i] = UiKit.Paint(under, UiKit.Accent, false);
            }
        }

        private GameObject BuildPage(RectTransform win, string name, int index, Action<RectTransform> fill)
        {
            RectTransform host = UiKit.Make(name, win);
            _pageHosts[index] = host;
            RectTransform content = UiKit.MakeScroll(host, "Scroll", out ScrollRect scroll);
            _scrolls[index] = scroll;
            fill(content);
            return host.gameObject;
        }

        private void BuildFooter(RectTransform win)
        {
            _footer = UiKit.Bottom(UiKit.Make("Footer", win), 0f, PanelLayout.FooterHeight);
            _footerLine = UiKit.Make("Line", _footer);
            UiKit.Paint(_footerLine, UiKit.Divider, false);

            _statusRt = UiKit.Make("Status", _footer);
            _status = UiKit.Label(_statusRt, "", 13, UiKit.TextMuted, TextAnchor.MiddleLeft);

            _allOffButton = UiKit.MakeButton(_footer, "AllOff", "", UiKit.RowBase, 14, OnAllOffClicked, out _allOffLabel);
            _allOffRt = (RectTransform)_allOffButton.transform;

            _resetButton = UiKit.MakeButton(_footer, "ResetTab", "", UiKit.RowBase, 15, OnResetClicked, out _resetLabel);
            _resetRt = (RectTransform)_resetButton.transform;
            Button done = UiKit.MakeButton(_footer, "Done", "Done", UiKit.Accent, 18, () => _requestClose(), out Text _);
            _doneRt = (RectTransform)done.transform;
        }

        // ================================================================ display settings + relayout

        /// <summary>
        /// Apply scale, width, transparency and the freeze dim (live, no rebuild). Called on build,
        /// on every Panel-tab change, on external config changes and once a second while open
        /// (screen-resolution changes).
        /// </summary>
        public void ApplyDisplaySettings()
        {
            if (Root == null)
            {
                return;
            }
            int sw = Screen.width, sh = Screen.height;
            float scale = PanelLayout.ScaleFactor(sh, UiSettings.PanelScale);
            if (scale != _appliedScale)
            {
                _scaler.scaleFactor = scale;
                _appliedScale = scale;
            }
            float width = PanelLayout.EffectiveWidth(UiSettings.PanelWidth, sw, sh, UiSettings.PanelScale);
            if (width != _appliedWidth || sw != _appliedScreenW || sh != _appliedScreenH)
            {
                _appliedScreenW = sw;
                _appliedScreenH = sh;
                Relayout(width);
            }
            _winGroup.alpha = Mathf.Clamp(UiSettings.PanelAlpha, Limits.PanelAlphaMin, Limits.PanelAlphaMax);
            bool dimOn = UiSettings.FreezeWhileOpen;
            if (_dim.activeSelf != dimOn)
            {
                _dim.SetActive(dimOn);
            }
        }

        /// <summary>Re-apply every width-dependent rect for a window <paramref name="width"/> px wide.</summary>
        public void Relayout(float width)
        {
            _appliedWidth = width;
            float pad = PanelLayout.Pad(width);
            _win.sizeDelta = new Vector2(width, 0f);

            UiKit.Top(_titleRt, PanelLayout.TitleTop, PanelLayout.TitleHeight, pad, PanelLayout.CloseSize + 24f);
            UiKit.Top(_subtitleRt, PanelLayout.SubtitleTop, PanelLayout.SubtitleHeight, pad, PanelLayout.CloseSize + 24f);

            // One-row tab strip (4 tabs).
            UiKit.Top(_tabBar, PanelLayout.TabsTop, PanelLayout.TabRowHeight, pad, pad);
            float barW = width - 2f * pad;
            const float gap = 4f;
            float tabW = (barW - (TabCount - 1) * gap) / TabCount;
            int font = PanelLayout.TabFont(width);
            for (int i = 0; i < TabCount; i++)
            {
                PanelLayout.Apply(_tabRt[i], new PanelLayout.Band(i * (tabW + gap), 0f, tabW, PanelLayout.TabRowHeight));
                _tabLabel[i].fontSize = font;
            }

            // Footer: status line, All off | Reset tab | Done.
            UiKit.Top(_footerLine, 0f, 1f, pad, pad);
            float fw = width - 2f * pad;
            float top = PanelLayout.FooterHeight;
            PanelLayout.Apply(_statusRt, new PanelLayout.Band(pad, top - PanelLayout.FooterStatusBottom - PanelLayout.FooterStatusHeight, fw, PanelLayout.FooterStatusHeight));
            float r1Top = top - PanelLayout.FooterRow1Bottom - PanelLayout.FooterRow1Height;
            PanelLayout.Apply(_allOffRt, new PanelLayout.Band(pad, r1Top, fw, PanelLayout.FooterRow1Height));
            float r2Top = top - PanelLayout.FooterRow2Bottom - PanelLayout.FooterRow2Height;
            float doneW = Mathf.Min(140f, fw * 0.3f);
            PanelLayout.Apply(_resetRt, new PanelLayout.Band(pad, r2Top, fw - doneW - 8f, PanelLayout.FooterRow2Height));
            PanelLayout.Apply(_doneRt, new PanelLayout.Band(pad + fw - doneW, r2Top, doneW, PanelLayout.FooterRow2Height));

            for (int i = 0; i < TabCount; i++)
            {
                UiKit.Place(_pageHosts[i], 0f, 0f, 1f, 1f, pad, PanelLayout.PageBottom, pad - 10f, PanelLayout.PageTop);
            }

            float c = PanelLayout.ContentWidth(width);
            for (int i = 0; i < _relayouts.Count; i++)
            {
                _relayouts[i](c);
            }
        }

        public void ShowTab(int index)
        {
            index = UiSettings.ClampTab(index);
            _tab = index;
            UiSettings.LastTab = index;
            for (int i = 0; i < _pages.Length; i++)
            {
                bool on = i == index;
                _pages[i].SetActive(on);
                _tabUnderline[i].enabled = on;
                _tabBg[i].color = on ? UiKit.ChipBase : UiKit.RowNormal;
            }
            _scrolls[index].verticalNormalizedPosition = 1f;
            _status.text = "";
            DisarmReset();
        }

        // ================================================================ row builders

        private static RectTransform AddBlock(RectTransform content, string name, float height, bool background, out LayoutElement le)
        {
            RectTransform rt = UiKit.Make(name, content);
            le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            if (background)
            {
                UiKit.Paint(rt, UiKit.RowNormal, false);
            }
            return rt;
        }

        private static void SetHeight(LayoutElement le, float h)
        {
            le.preferredHeight = h;
            le.minHeight = h;
        }

        private static void AddSectionTitle(RectTransform content, string text)
        {
            RectTransform rt = AddBlock(content, "Section_" + text, 38f, false, out LayoutElement _);
            RectTransform t = UiKit.Place(UiKit.Make("T", rt), 0f, 0f, 1f, 1f, 2f, 4f, 0f, 0f);
            UiKit.Label(t, text.ToUpperInvariant(), 14, UiKit.TextMuted, TextAnchor.LowerLeft, FontStyle.Bold);
        }

        private Text AddNote(RectTransform content, string name, float height, int size = 16)
        {
            RectTransform block = AddBlock(content, name, height, false, out LayoutElement le);
            RectTransform t = UiKit.Place(UiKit.Make("T", block), 0f, 0f, 1f, 1f, 4f, 0f, 4f, 0f);
            Text text = UiKit.Label(t, "", size, UiKit.TextMuted, TextAnchor.MiddleLeft, FontStyle.Normal, true);
            _relayouts.Add(c =>
            {
                SetHeight(le, PanelLayout.NoteHeight(c, height));
                text.fontSize = PanelLayout.Wide(c) ? size : Mathf.Min(size, 14);
            });
            return text;
        }

        /// <summary>Sub-list whose rows can be dimmed/disabled together.</summary>
        private static CanvasGroup AddGroup(RectTransform content, string name, out RectTransform inner)
        {
            inner = UiKit.Make(name, content);
            var vlg = inner.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return inner.gameObject.AddComponent<CanvasGroup>();
        }

        private void BindGroup(CanvasGroup group, Func<bool> enabled)
        {
            _refreshers.Add(() =>
            {
                bool on = enabled();
                group.interactable = on;
                group.alpha = on ? 1f : Dimmed;
            });
        }

        /// <summary>A pill switch showing ON (green) / OFF (grey); returns its RectTransform for relayout.</summary>
        private RectTransform AddSwitch(RectTransform row, int fontSize, Func<bool> get, Action<bool> set)
        {
            Text label;
            Button pill = UiKit.MakeButton(row, "Switch", "", UiKit.SwitchOff, fontSize, () =>
            {
                if (_suppress)
                {
                    return;
                }
                set(!get());
                Refresh();
            }, out label);
            Image img = (Image)pill.targetGraphic;
            _refreshers.Add(() =>
            {
                bool on = get();
                img.color = on ? UiKit.Good : UiKit.SwitchOff;
                label.text = on ? "ON" : "OFF";
            });
            return (RectTransform)pill.transform;
        }

        private void LayoutSwitchRow(LayoutElement le, RectTransform title, Text titleText, RectTransform hint, Text hintText,
            RectTransform pill, PanelLayout.SwitchRowGeom g)
        {
            SetHeight(le, g.RowHeight);
            PanelLayout.Apply(title, g.Title);
            PanelLayout.Apply(hint, g.Hint);
            titleText.fontSize = g.TitleFont;
            hintText.fontSize = g.HintFont;
            hintText.horizontalOverflow = g.WrapHint ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            hintText.alignment = g.WrapHint ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            UiKit.RightBox(pill, g.SwitchW, g.SwitchH, g.SwitchRight);
        }

        /// <summary>The big ON/OFF row at the top of each tab.</summary>
        private void AddMasterSwitch(RectTransform content, string title, string hint, Func<bool> get, Action<bool> set)
        {
            RectTransform row = AddBlock(content, "Master", 76f, true, out LayoutElement le);
            RectTransform t = UiKit.Make("Title", row);
            Text tt = UiKit.Label(t, title, 21, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            RectTransform h = UiKit.Make("Hint", row);
            Text ht = UiKit.Label(h, hint, 15, UiKit.TextMuted, TextAnchor.MiddleLeft);
            RectTransform pill = AddSwitch(row, 18, get, set);
            _relayouts.Add(c => LayoutSwitchRow(le, t, tt, h, ht, pill, PanelLayout.MasterRow(c)));
        }

        /// <summary>Secondary on/off option with a one-line explanation.</summary>
        private Text AddOption(RectTransform content, string title, string hint, Func<bool> get, Action<bool> set,
            Func<bool> enabled = null)
        {
            RectTransform row = AddBlock(content, "Opt_" + title, 58f, true, out LayoutElement le);
            CanvasGroup group = row.gameObject.AddComponent<CanvasGroup>();
            RectTransform t = UiKit.Make("Title", row);
            Text tt = UiKit.Label(t, title, 17, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            RectTransform h = UiKit.Make("Hint", row);
            Text hintText = UiKit.Label(h, hint, 14, UiKit.TextMuted, TextAnchor.MiddleLeft);
            RectTransform pill = AddSwitch(row, 15, get, set);
            _relayouts.Add(c => LayoutSwitchRow(le, t, tt, h, hintText, pill, PanelLayout.OptionRow(c)));
            if (enabled != null)
            {
                BindGroup(group, enabled);
            }
            return hintText;
        }

        /// <summary>Grid of preset buttons; the active one is filled with the accent colour.</summary>
        private void AddPresetButtons(RectTransform content, string name, int count, int perRowWide, int perRowNarrow,
            Func<int, string> label, Func<int> active, Action<int> select)
        {
            const float h = 44f, gap = 8f;
            RectTransform block = AddBlock(content, name, h, false, out LayoutElement le);

            var images = new Image[count];
            var texts = new Text[count];
            var rts = new RectTransform[count];
            for (int i = 0; i < count; i++)
            {
                int index = i;
                Button b = UiKit.MakeButton(block, "Preset" + i, "", UiKit.ChipBase, 17, () =>
                {
                    if (_suppress)
                    {
                        return;
                    }
                    select(index);
                    Refresh();
                }, out texts[i]);
                rts[i] = (RectTransform)b.transform;
                images[i] = (Image)b.targetGraphic;
            }

            _relayouts.Add(c =>
            {
                int perRow = Mathf.Max(1, PanelLayout.Wide(c) ? perRowWide : Mathf.Min(perRowWide, perRowNarrow));
                int rows = (count + perRow - 1) / perRow;
                SetHeight(le, rows * h + (rows - 1) * gap);
                int font = PanelLayout.PresetFont(c);
                for (int i = 0; i < count; i++)
                {
                    int r = i / perRow, col = i % perRow;
                    float top = r * (h + gap);
                    RectTransform rt = rts[i];
                    rt.anchorMin = new Vector2((float)col / perRow, 1f);
                    rt.anchorMax = new Vector2((float)(col + 1) / perRow, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.offsetMin = new Vector2(col == 0 ? 0f : gap / 2f, -(top + h));
                    rt.offsetMax = new Vector2(col == perRow - 1 ? 0f : -gap / 2f, -top);
                    texts[i].fontSize = font;
                }
            });

            _refreshers.Add(() =>
            {
                int a = active();
                for (int i = 0; i < count; i++)
                {
                    images[i].color = i == a ? UiKit.Accent : UiKit.ChipBase;
                    texts[i].text = label(i);
                }
            });
        }

        /// <summary>
        /// Name and plain-language hint, slider, value (and an optional absolute readout below
        /// it) and Reset. Wide: one line (0.5.0 geometry); narrow: stacked. The value turns
        /// blue when it differs from the reference; Reset puts it back.
        /// </summary>
        private GameObject AddSlider(RectTransform content, string title, string hint, float min, float max,
            Func<float> get, Action<float> set, Func<float> reference, Func<float, string> format,
            Func<bool> enabled = null, Func<float, string> readout = null, bool wholeNumbers = false)
        {
            RectTransform row = AddBlock(content, "Row_" + title, 58f, true, out LayoutElement le);
            CanvasGroup group = row.gameObject.AddComponent<CanvasGroup>();

            RectTransform t = UiKit.Make("Title", row);
            Text titleText = UiKit.Label(t, title, 17, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            RectTransform h = UiKit.Make("Hint", row);
            Text hintText = UiKit.Label(h, hint, 13, UiKit.TextMuted, TextAnchor.MiddleLeft);

            Slider slider = UiKit.MakeSlider(row, "Slider");
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.onValueChanged.AddListener(v =>
            {
                if (_suppress)
                {
                    return;
                }
                set(v);
                Refresh();
            });

            RectTransform valRt = UiKit.Make("Value", row);
            Text value;
            Text valueReadout = null;
            if (readout != null)
            {
                RectTransform factorRt = UiKit.Place(UiKit.Make("Factor", valRt), 0f, 0.5f, 1f, 1f, 0f, 0f, 0f, 0f);
                value = UiKit.Label(factorRt, "", 16, UiKit.TextMain, TextAnchor.MiddleCenter, FontStyle.Bold);
                RectTransform readoutRt = UiKit.Place(UiKit.Make("Readout", valRt), 0f, 0f, 1f, 0.5f, 0f, 0f, 0f, 0f);
                valueReadout = UiKit.Label(readoutRt, "", 12, UiKit.TextMuted, TextAnchor.MiddleCenter);
            }
            else
            {
                value = UiKit.Label(valRt, "", 17, UiKit.TextMain, TextAnchor.MiddleCenter, FontStyle.Bold);
            }

            Text resetLabel;
            Button reset = UiKit.MakeButton(row, "Reset", "Reset", UiKit.ChipBase, 13, () =>
            {
                if (_suppress)
                {
                    return;
                }
                set(reference());
                Refresh();
            }, out resetLabel);
            RectTransform resetRt = (RectTransform)reset.transform;

            bool hasReadout = readout != null;
            _relayouts.Add(c =>
            {
                PanelLayout.SliderGeom g = PanelLayout.SliderRow(c, hasReadout);
                SetHeight(le, g.RowHeight);
                PanelLayout.Apply(t, g.Title);
                PanelLayout.Apply(h, g.Hint);
                PanelLayout.Apply((RectTransform)slider.transform, g.Slider);
                PanelLayout.Apply(valRt, g.Value);
                PanelLayout.Apply(resetRt, g.Reset);
                titleText.fontSize = g.TitleFont;
                hintText.fontSize = g.HintFont;
            });

            _refreshers.Add(() =>
            {
                bool on = enabled == null || enabled();
                group.interactable = on;
                group.alpha = on ? 1f : Dimmed;

                float v = get();
                float r = reference();
                slider.value = v;
                bool changed = Mathf.Abs(v - r) > 0.005f * Mathf.Max(1f, Mathf.Abs(r));
                value.text = format(v);
                value.color = changed ? UiKit.Accent : UiKit.TextMain;
                if (valueReadout != null)
                {
                    valueReadout.text = readout(v);
                }
                reset.interactable = changed;
                resetLabel.color = changed ? UiKit.TextMain : UiKit.TextMuted;
            });
            return row.gameObject;
        }

        /// <summary>Show/hide a row from a predicate on every refresh.</summary>
        private void BindVisible(GameObject row, Func<bool> visible)
        {
            _refreshers.Add(() =>
            {
                bool on = visible();
                if (row.activeSelf != on)
                {
                    row.SetActive(on);
                }
            });
        }

        // ================================================================ shared semantics

        // Picks the singular/plural template for the per-category vehicle status lines.
        private static string VehicleStatus(int n, string oneFmt, string manyFmt)
        {
            return string.Format(n == 1 ? oneFmt : manyFmt, n);
        }

        private static string PresetButtonLabel<T>(PresetBook<T> book, T p) where T : class, ITunablePreset
        {
            if (p == book.Custom)
            {
                T b = book.FindBuiltIn(p.BasedOn);
                return b != null ? string.Format(UiStrings.CustomPresetFmt, b.Label) : "Custom";
            }
            return p.Label;
        }

        private static string CustomDescription<T>(PresetBook<T> book) where T : class, ITunablePreset
        {
            T b = book.FindBuiltIn(book.Custom.BasedOn);
            return b != null
                ? string.Format(UiStrings.CustomBasedOnDescFmt, b.Label, b.Label)
                : UiStrings.CustomPlainDesc;
        }

        /// <summary>An absolute readout, or nothing while no vehicle supplies a baseline.</summary>
        private static string Absolute(float factor, float baseline, Func<float, string> unit)
        {
            return baseline > 0f ? unit(factor * baseline) : "";
        }

        private void Edit<T>(PresetBook<T> book, Action<T> edit) where T : class, ITunablePreset
        {
            T p = book.BeginEdit();
            if (p == null)
            {
                return;
            }
            edit(p);
            _tuner.ApplyLive();
        }

        private void EditSuspension(Action<SuspensionPreset> edit) { Edit(SuspensionSettings.Book, edit); }
        private void EditAssists(Action<AssistsPreset> edit) { Edit(AssistsSettings.Book, edit); }

        private void ModeChanged()
        {
            ApplyDisplaySettings();
            if (_onModeChanged != null)
            {
                _onModeChanged();
            }
        }

        // ================================================================ steering tab

        /// <summary>Values the sliders display: the active preset, or the defaults while Vanilla is active.</summary>
        private static SteeringPreset Shown
        {
            get
            {
                SteeringPreset a = SteeringSettings.ActivePreset ?? SteeringPreset.Custom;
                return a.IsVanilla ? SteeringPreset.Defaults : a;
            }
        }

        private static bool IsVanilla
        {
            get { return SteeringSettings.ActivePreset != null && SteeringSettings.ActivePreset.IsVanilla; }
        }

        private static void EditSteering(Action<SteeringPreset> edit)
        {
            SteeringPreset p = SteeringSettings.BeginEdit();
            if (p != null)
            {
                edit(p);
            }
        }

        /// <summary>
        /// Curve-editor edit entry: forks the active preset into Custom (copying
        /// its curves), applies the mutation to the chosen curve and returns the
        /// live curve, or null when the active preset cannot be edited (Vanilla).
        /// </summary>
        private EditableCurve EditCurve(Func<SteeringPreset, EditableCurve> pick, Action<EditableCurve> mutate)
        {
            SteeringPreset p = SteeringSettings.BeginEdit();
            if (p == null)
            {
                return null;
            }
            mutate(pick(p));
            Refresh();
            return pick(p);
        }

        private void BuildSteering(RectTransform content)
        {
            AddMasterSwitch(content, "Steering tuning", "OFF = the game's original steering, exactly as shipped.",
                () => SteeringSettings.Enabled, v => SteeringSettings.Enabled = v);

            CanvasGroup body = AddGroup(content, "Body", out RectTransform c);
            BindGroup(body, () => SteeringSettings.Enabled);

            AddSectionTitle(c, "Preset");
            SteeringPreset[] presets = SteeringPreset.Presets;
            AddPresetButtons(c, "Presets", presets.Length, 3, 2,
                i => PresetButtonLabel(SteeringSettings.Book, presets[i]),
                () => Array.IndexOf(presets, SteeringSettings.ActivePreset),
                i => SteeringSettings.Select(presets[i]));

            Text desc = AddNote(c, "Description", 50f);
            _refreshers.Add(() =>
            {
                SteeringPreset a = SteeringSettings.ActivePreset ?? SteeringPreset.Custom;
                if (a == SteeringPreset.Custom)
                {
                    desc.text = CustomDescription(SteeringSettings.Book);
                }
                else
                {
                    desc.text = a.IsVanilla ? a.Description : string.Format(UiStrings.PresetEditNoteFmt, a.Description);
                }
            });

            CanvasGroup tuning = AddGroup(c, "Tuning", out RectTransform t);
            BindGroup(tuning, () => !IsVanilla);

            AddSectionTitle(t, "Response");
            AddSlider(t, "Steering speed", "How fast the wheels turn toward your input",
                Limits.RateMin, Limits.RateMax,
                () => Shown.RateMultiplier, v => EditSteering(p => p.RateMultiplier = v),
                () => SteeringSettings.Reference().RateMultiplier, UiStrings.Times);
            AddSlider(t, "Smoothing", "Higher = softer, lazier response",
                Limits.SmoothMin, Limits.SmoothMax,
                () => Shown.SmoothingScale, v => EditSteering(p => p.SmoothingScale = v),
                () => SteeringSettings.Reference().SmoothingScale, UiStrings.Times);
            AddOption(t, "Use the vehicle's input curve", "OFF = pow curve with the exponent below",
                () => !Shown.LinearityOverride, v => EditSteering(p => p.LinearityOverride = !v));
            AddSlider(t, "Centre sensitivity", "Low = twitchy at centre, high = gentle",
                Limits.LinExpMin, Limits.LinExpMax,
                () => Shown.LinearityExponent, v => EditSteering(p => p.LinearityExponent = v),
                () => SteeringSettings.Reference().LinearityExponent, v => v.ToString("0.00"),
                () => Shown.LinearityOverride);

            AddOption(t, "Use the vehicle's own curve", "OFF = use the custom lock curve below",
                () => Shown.UseVehicleCurve, v => EditSteering(p => p.UseVehicleCurve = v));
            CurveEditor lockEditor = CurveEditor.Create(t,
                "Lock at speed",
                "How much steering you keep at speed. Left edge = stopped, right edge = 180 km/h and above. Click = add a point, drag = move, double-click = remove.",
                () => Shown.LockCurve,
                () => SteeringSettings.Reference().LockCurve,
                mutate => EditCurve(pp => pp.LockCurve, mutate),
                () => SteeringSettings.Enabled && !IsVanilla);
            _refreshers.Add(lockEditor.Refresh);
            _relayouts.Add(lockEditor.Relayout);
            GameObject lockRow = lockEditor.Row;
            BindVisible(lockRow, () => !Shown.UseVehicleCurve);

            CurveEditor returnEditor = CurveEditor.Create(t,
                "Return to center",
                "How fast the wheel straightens after you let go. Flat at 1 = steers back as fast as it steers in. A lower line = lazier. Ramping up from 0 = holds the wheels while stopped, then straightens out as you drive. Flat at the left edge = vanilla low-speed handling.",
                () => Shown.ReturnCurve,
                () => SteeringSettings.Reference().ReturnCurve,
                mutate => EditCurve(pp => pp.ReturnCurve, mutate),
                () => SteeringSettings.Enabled && !IsVanilla);
            _refreshers.Add(returnEditor.Refresh);
            _relayouts.Add(returnEditor.Relayout);

            AddSectionTitle(t, "Grip and slides");
            AddOption(t, "Front slip clamp", "Stops the front tyres turning past their grip limit",
                () => Shown.TractionClampEnabled, v => EditSteering(p => p.TractionClampEnabled = v));
            AddSlider(t, "Slip window", "Higher = more steering before tyres slide",
                Limits.SlipMin, Limits.SlipMax,
                () => Shown.SlipAngleDeg, v => EditSteering(p => p.SlipAngleDeg = v),
                () => SteeringSettings.Reference().SlipAngleDeg, v => UiStrings.Deg(v),
                () => Shown.TractionClampEnabled);
            AddSlider(t, "Counter-steer speed", "Extra steering speed while catching a slide",
                Limits.OppLockMin, Limits.OppLockMax,
                () => Shown.OppositeLockBoost, v => EditSteering(p => p.OppositeLockBoost = v),
                () => SteeringSettings.Reference().OppositeLockBoost, UiStrings.Times);

            AddSectionTitle(c, "Game setting");
            Text gameHint = AddOption(c, "Follow game's steering speed", "",
                () => SteeringSettings.MatchGameSteeringSpeed, v => SteeringSettings.MatchGameSteeringSpeed = v,
                () => !IsVanilla);
            _refreshers.Add(() =>
            {
                gameHint.text = GameSettingsReader.Loaded
                    ? string.Format(UiStrings.GameSpeedHintFoundFmt, GameSettingsReader.Steeringspeed.ToString("0"))
                    : UiStrings.GameSpeedHintMissing;
            });
        }

        // ================================================================ suspension tab

        private void BuildSuspension(RectTransform content)
        {
            AddMasterSwitch(content, "Suspension tuning", "OFF = every vehicle keeps its original suspension.",
                () => SuspensionSettings.Enabled, v =>
                {
                    SuspensionSettings.Enabled = v;
                    _tuner.ReapplyNow();
                });

            CanvasGroup body = AddGroup(content, "Body", out RectTransform c);
            BindGroup(body, () => SuspensionSettings.Enabled);

            AddSectionTitle(c, "Preset");
            SuspensionPreset[] presets = SuspensionPreset.Presets;
            AddPresetButtons(c, "Presets", presets.Length, 3, 2,
                i => PresetButtonLabel(SuspensionSettings.Book, presets[i]),
                () => Array.IndexOf(presets, SuspensionSettings.ActivePreset),
                i =>
                {
                    SuspensionSettings.Book.Select(presets[i]);
                    _tuner.ApplyLive();
                });

            Text desc = AddNote(c, "Description", 50f);
            _refreshers.Add(() =>
            {
                SuspensionPreset p = SuspensionSettings.Shown;
                desc.text = p == SuspensionPreset.Custom
                    ? CustomDescription(SuspensionSettings.Book)
                    : p.Description + " Move any slider to customise it.";
            });

            AddSectionTitle(c, "Tuning");
            AddOption(c, "Separate front and rear", "Tune each axle on its own",
                () => SuspensionSettings.SplitFrontRear, v =>
                {
                    SuspensionSettings.SplitFrontRear = v;
                    // Linking only needs an edit (and a fork into Custom) when the
                    // axles actually differ; Stock/Comfort/Off-road stay selected.
                    if (!v && SuspensionSettings.AxlesDiffer())
                    {
                        SuspensionSettings.LinkRearToFront();
                        _tuner.ApplyLive();
                    }
                });

            AddAxleFactor(c, "Stiffness", "Higher = firmer ride, less body movement",
                p => p.SpringFront, (p, v) => p.SpringFront = v,
                p => p.SpringRear, (p, v) => p.SpringRear = v,
                VehicleTuner.Readout.SpringForce, UiStrings.Force);
            AddAxleFactor(c, "Ride height", "Suspension travel; higher sits taller",
                p => p.RideHeightFront, (p, v) => p.RideHeightFront = v,
                p => p.RideHeightRear, (p, v) => p.RideHeightRear = v,
                VehicleTuner.Readout.RideHeight, UiStrings.Length);
            AddAxleFactor(c, "Bump damping", "Resists compression over bumps",
                p => p.BumpFront, (p, v) => p.BumpFront = v,
                p => p.BumpRear, (p, v) => p.BumpRear = v,
                VehicleTuner.Readout.BumpRate, UiStrings.Rate);
            AddAxleFactor(c, "Rebound damping", "Stops the body bouncing back up",
                p => p.ReboundFront, (p, v) => p.ReboundFront = v,
                p => p.ReboundRear, (p, v) => p.ReboundRear = v,
                VehicleTuner.Readout.ReboundRate, UiStrings.Rate);
            AddAxleFactor(c, "Anti-roll bar", "Higher = flatter in corners",
                p => p.ArbFront, (p, v) => p.ArbFront = v,
                p => p.ArbRear, (p, v) => p.ArbRear = v,
                VehicleTuner.Readout.ArbForce, UiStrings.Force);

            Text status = AddNote(content, "Status", 44f, 14);
            _refreshers.Add(() =>
            {
                int n = _tuner.TargetedCount;
                status.text = !SuspensionSettings.Enabled
                    ? "Suspension tuning is off. Vehicles use their original setup."
                    : n == 0
                        ? "No vehicles found yet. Settings apply as soon as one spawns."
                        : VehicleStatus(n, UiStrings.AppliedOneArbFmt, UiStrings.AppliedManyArbFmt);
            });
        }

        /// <summary>
        /// One factor slider for both axles, or two ("front" / "rear") when split mode is on.
        /// Linked mode writes the same value to both. Readouts show the computed absolute
        /// value (mean stock baseline x factor).
        /// </summary>
        private void AddAxleFactor(RectTransform content, string title, string hint,
            Func<SuspensionPreset, float> getF, Action<SuspensionPreset, float> setF,
            Func<SuspensionPreset, float> getR, Action<SuspensionPreset, float> setR,
            VehicleTuner.Readout readout, Func<float, string> unitFormat)
        {
            Func<float, string> readoutF = v => Absolute(v, _tuner.MeanBaseline(readout, true), unitFormat);
            Func<float, string> readoutR = v => Absolute(v, _tuner.MeanBaseline(readout, false), unitFormat);

            GameObject both = AddSlider(content, title, hint, Limits.SuspFactorMin, Limits.SuspFactorMax,
                () => getF(SuspensionSettings.Shown),
                v => EditSuspension(p =>
                {
                    setF(p, v);
                    setR(p, v);
                }),
                () => getF(SuspensionSettings.Reference()), UiStrings.Times, null, readoutF);
            GameObject front = AddSlider(content, title + " (front)", hint, Limits.SuspFactorMin, Limits.SuspFactorMax,
                () => getF(SuspensionSettings.Shown), v => EditSuspension(p => setF(p, v)),
                () => getF(SuspensionSettings.Reference()), UiStrings.Times, null, readoutF);
            GameObject rear = AddSlider(content, title + " (rear)", hint, Limits.SuspFactorMin, Limits.SuspFactorMax,
                () => getR(SuspensionSettings.Shown), v => EditSuspension(p => setR(p, v)),
                () => getR(SuspensionSettings.Reference()), UiStrings.Times, null, readoutR);

            BindVisible(both, () => !SuspensionSettings.SplitFrontRear);
            BindVisible(front, () => SuspensionSettings.SplitFrontRear);
            BindVisible(rear, () => SuspensionSettings.SplitFrontRear);
        }

        // ================================================================ assists tab

        private void BuildAssists(RectTransform content)
        {
            AddMasterSwitch(content, "Stability assists", "OFF = no assists from this mod (the game's own ABS/TCS still work).",
                () => AssistsSettings.Enabled, v =>
                {
                    AssistsSettings.Enabled = v;
                    _tuner.ReapplyNow();
                });

            CanvasGroup body = AddGroup(content, "Body", out RectTransform c);
            BindGroup(body, () => AssistsSettings.Enabled);

            AddSectionTitle(c, "Preset");
            AssistsPreset[] presets = AssistsPreset.Presets;
            AddPresetButtons(c, "Presets", presets.Length, 3, 2,
                i => PresetButtonLabel(AssistsSettings.Book, presets[i]),
                () => Array.IndexOf(presets, AssistsSettings.ActivePreset),
                i =>
                {
                    AssistsSettings.Book.Select(presets[i]);
                    _tuner.ApplyLive();
                });

            Text desc = AddNote(c, "Description", 50f);
            _refreshers.Add(() =>
            {
                AssistsPreset p = AssistsSettings.Shown;
                desc.text = p == AssistsPreset.Custom ? CustomDescription(AssistsSettings.Book) : p.Description;
            });

            AddSectionTitle(c, "ABS");
            AddOption(c, "ABS", "Prevents wheel lock-up under braking",
                () => AssistsSettings.Shown.AbsEnabled, v => EditAssists(p => p.AbsEnabled = v));
            AddSlider(c, "ABS slip threshold", "How much wheel slip before release",
                Limits.SlipThrMin, Limits.SlipThrMax,
                () => AssistsSettings.Shown.AbsSlipThreshold, v => EditAssists(p => p.AbsSlipThreshold = v),
                () => AssistsSettings.Reference().AbsSlipThreshold, v => v.ToString("0.00"),
                () => AssistsSettings.Shown.AbsEnabled);
            AddSlider(c, "ABS cutoff speed", "No ABS below this speed",
                Limits.CutoffSpeedMin, Limits.CutoffSpeedMax,
                () => AssistsSettings.Shown.AbsCutoffSpeed, v => EditAssists(p => p.AbsCutoffSpeed = v),
                () => AssistsSettings.Reference().AbsCutoffSpeed, v => UiStrings.SpeedMps(v),
                () => AssistsSettings.Shown.AbsEnabled);
            AddSlider(c, "ABS release force", "Brake strength while releasing",
                Limits.CutMultMin, Limits.CutMultMax,
                () => AssistsSettings.Shown.AbsCutMultiplier, v => EditAssists(p => p.AbsCutMultiplier = v),
                () => AssistsSettings.Reference().AbsCutMultiplier, UiStrings.Percent,
                () => AssistsSettings.Shown.AbsEnabled);

            AddSectionTitle(c, "TCS");
            AddOption(c, "TCS", "Cuts power while the wheels spin",
                () => AssistsSettings.Shown.TcsEnabled, v => EditAssists(p => p.TcsEnabled = v));
            AddSlider(c, "TCS slip threshold", "How much wheel spin before cutting",
                Limits.SlipThrMin, Limits.SlipThrMax,
                () => AssistsSettings.Shown.TcsSlipThreshold, v => EditAssists(p => p.TcsSlipThreshold = v),
                () => AssistsSettings.Reference().TcsSlipThreshold, v => v.ToString("0.00"),
                () => AssistsSettings.Shown.TcsEnabled);
            AddSlider(c, "TCS cutoff speed", "No TCS below this speed",
                Limits.CutoffSpeedMin, Limits.CutoffSpeedMax,
                () => AssistsSettings.Shown.TcsCutoffSpeed, v => EditAssists(p => p.TcsCutoffSpeed = v),
                () => AssistsSettings.Reference().TcsCutoffSpeed, v => UiStrings.SpeedMps(v),
                () => AssistsSettings.Shown.TcsEnabled);
            AddSlider(c, "TCS cut strength", "Power allowed while spinning",
                Limits.CutMultMin, Limits.CutMultMax,
                () => AssistsSettings.Shown.TcsCutMultiplier, v => EditAssists(p => p.TcsCutMultiplier = v),
                () => AssistsSettings.Reference().TcsCutMultiplier, UiStrings.Percent,
                () => AssistsSettings.Shown.TcsEnabled);

            Text status = AddNote(content, "Status", 44f, 14);
            _refreshers.Add(() =>
            {
                int n = _tuner.TargetedCount;
                status.text = !AssistsSettings.Enabled
                    ? "Assists are off. The game's own ABS/TCS still work if a vehicle has them."
                    : n == 0
                        ? "No vehicles found yet. Assists attach as soon as one spawns."
                        : VehicleStatus(n, UiStrings.ActiveOnOneFmt, UiStrings.ActiveOnManyFmt);
            });
        }

        // ================================================================ panel tab

        private void BuildPanelTab(RectTransform content)
        {
            AddSectionTitle(content, "Apply to");
            AddPresetButtons(content, "ApplyTarget", 3, 3, 3,
                i => TargetSettings.ModeName((TargetMode)i),
                () => (int)TargetSettings.Mode,
                i =>
                {
                    TargetSettings.Mode = (TargetMode)i;
                    if (TargetSettings.Mode != TargetMode.Selected && _targetNameIndex > 0)
                    {
                        _targetNameIndex = 0;
                    }
                    _tuner.ApplyLive();
                    Refresh();
                });
            GameObject targetRow = AddBlock(content, "TargetVehicle", 58f, true, out LayoutElement _).gameObject;
            BindVisible(targetRow, () => TargetSettings.Mode == TargetMode.Selected);
            RectTransform tr = (RectTransform)targetRow.transform;
            RectTransform tl = UiKit.Place(UiKit.Make("T", tr), 0f, 0.5f, 1f, 1f, 16f, 0f, 100f, 6f);
            UiKit.Label(tl, "Vehicle", 17, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            Button targetBtn = UiKit.MakeButton(tr, "VehicleBtn", "", UiKit.ChipBase, 15, () =>
            {
                List<string> names = _tuner.TrackedNames();
                if (names.Count > 0)
                {
                    _targetNameIndex = (_targetNameIndex + 1) % names.Count;
                    TargetSettings.SelectedName = names[_targetNameIndex];
                    _tuner.ApplyLive();
                    Refresh();
                }
            }, out Text targetNameLabel);
            UiKit.RightBox((RectTransform)targetBtn.transform, 220f, 34f, 14f);
            Text targetHint = AddNote(content, "TargetHint", 26f, 13);
            _refreshers.Add(() =>
            {
                List<string> names = _tuner.TrackedNames();
                if (names.Count == 0)
                {
                    targetNameLabel.text = "-";
                    targetHint.text = "No vehicles found yet.";
                    return;
                }
                if (_targetNameIndex >= names.Count)
                {
                    _targetNameIndex = 0;
                }
                if (TargetSettings.SelectedName == "" || !names.Contains(TargetSettings.SelectedName))
                {
                    TargetSettings.SelectedName = names[_targetNameIndex];
                }
                targetNameLabel.text = TargetSettings.SelectedName;
                targetHint.text = TargetSettings.Mode == TargetMode.Selected
                    ? "Only this vehicle is tuned. Click the button to pick another."
                    : "";
            });

            AddSectionTitle(content, "Settings");
            GameObject keyRow = AddBlock(content, "ToggleKey", 58f, true, out LayoutElement _).gameObject;
            RectTransform kr = (RectTransform)keyRow.transform;
            RectTransform kl = UiKit.Place(UiKit.Make("T", kr), 0f, 0.5f, 1f, 1f, 16f, 0f, 140f, 6f);
            UiKit.Label(kl, "Panel hotkey", 17, UiKit.TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            Button keyBtn = UiKit.MakeButton(kr, "KeyBtn", "", UiKit.ChipBase, 15, () =>
            {
                SettingsPanelManager m = SettingsPanelManager.Current;
                if (m != null)
                {
                    m.CaptureToggleKey();
                }
                Refresh();
            }, out Text keyLabel);
            UiKit.RightBox((RectTransform)keyBtn.transform, 130f, 34f, 14f);
            _refreshers.Add(() =>
            {
                SettingsPanelManager m = SettingsPanelManager.Current;
                keyLabel.text = m != null && m.CapturingToggleKey ? "Press a key..." : ModConfig.ToggleKeyString;
            });
            AddOption(content, "Freeze game while open", "OFF = keep driving while the panel is open",
                () => UiSettings.FreezeWhileOpen, v =>
                {
                    UiSettings.FreezeWhileOpen = v;
                    ModeChanged();
                });
            AddSlider(content, "Transparency", "Lower = see more of the game through the panel",
                Limits.PanelAlphaMin, Limits.PanelAlphaMax,
                () => UiSettings.PanelAlpha, v => { UiSettings.PanelAlpha = v; ApplyDisplaySettings(); },
                () => UiSettings.DefaultAlpha, UiStrings.Percent);
            AddSlider(content, "Size", "Interface size of the panel",
                Limits.PanelScaleMin, Limits.PanelScaleMax,
                () => UiSettings.PanelScale, v => { UiSettings.PanelScale = Mathf.Round(v * 20f) / 20f; ApplyDisplaySettings(); },
                () => UiSettings.DefaultScale, UiStrings.Times);
            AddSlider(content, "Width", "How wide the docked panel is",
                Limits.PanelWidthMin, Limits.PanelWidthMax,
                () => UiSettings.PanelWidth, v => { UiSettings.PanelWidth = Mathf.Round(v / 10f) * 10f; ApplyDisplaySettings(); },
                () => Limits.PanelWidthDefault, UiStrings.Px);
            Text note = AddNote(content, "PanelNote", 30f, 13);
            note.text = "Changes apply immediately.";
        }

        // ================================================================ footer: reset, copy/paste, all off

        private void OnResetClicked()
        {
            if (Time.unscaledTime > _resetArmedUntil)
            {
                // First click arms; a second click within 3 s confirms. No accidental wipes.
                _resetArmedUntil = Time.unscaledTime + 3f;
                UpdateResetButton();
                return;
            }
            DisarmReset();
            _resetActions[_tab]();
            Refresh();
        }

        private void DisarmReset()
        {
            _resetArmedUntil = -1f;
            UpdateResetButton();
        }

        private void UpdateResetButton()
        {
            if (_resetButton == null)
            {
                return;
            }
            bool armed = _resetArmedUntil > 0f;
            ((Image)_resetButton.targetGraphic).color = armed ? UiKit.Danger : UiKit.RowBase;
            _resetLabel.text = armed ? "Click again to confirm" : _resetLabels[_tab];
            bool allArmed = _allOffArmedUntil > 0f;
            ((Image)_allOffButton.targetGraphic).color = allArmed ? UiKit.Danger : UiKit.RowBase;
            _allOffLabel.text = allArmed ? "Click again" : "Turn everything off";
        }

        /// <summary>Two-click arm state (pure, for the harness): armed until <paramref name="armedUntil"/>.</summary>
        public static bool ConfirmTwoClick(ref float armedUntil, float now, float window = 3f)
        {
            if (now > armedUntil)
            {
                armedUntil = now + window;
                return false;   // first click (or the arm expired): arm
            }
            armedUntil = -1f;
            return true;        // second click inside the window: confirm
        }

        /// <summary>Switch all three tuning categories off (the panel's "Turn everything off").</summary>
        public static void TurnEverythingOff()
        {
            SteeringSettings.Enabled = false;
            SuspensionSettings.Enabled = false;
            AssistsSettings.Enabled = false;
        }

        private void OnAllOffClicked()
        {
            if (!ConfirmTwoClick(ref _allOffArmedUntil, Time.unscaledTime))
            {
                UpdateResetButton();
                return;
            }
            TurnEverythingOff();
            _tuner.ReapplyNow();
            UpdateResetButton();
            Refresh();
        }

        // ================================================================ lifecycle

        /// <summary>Called every frame while visible.</summary>
        public void Tick()
        {
            if (_resetArmedUntil > 0f && Time.unscaledTime > _resetArmedUntil)
            {
                DisarmReset();
            }
            if (_allOffArmedUntil > 0f && Time.unscaledTime > _allOffArmedUntil)
            {
                _allOffArmedUntil = -1f;
                UpdateResetButton();
            }
            // The vehicle count changes as vehicles spawn; refresh it now and then
            // (and follow screen-resolution changes).
            if (Time.unscaledTime > _nextStatusRefresh)
            {
                _nextStatusRefresh = Time.unscaledTime + 1f;
                ApplyDisplaySettings();
                Refresh();
            }
        }

        public void OnOpened()
        {
            DisarmReset();
            _allOffArmedUntil = -1f;
            ApplyDisplaySettings();
            ShowTab(UiSettings.ClampTab(UiSettings.LastTab));
            Refresh();
        }

        public void Refresh()
        {
            if (Root == null)
            {
                return;
            }
            _suppress = true;
            try
            {
                for (int i = 0; i < _refreshers.Count; i++)
                {
                    _refreshers[i]();
                }
            }
            finally
            {
                _suppress = false;
            }
        }
    }
}
