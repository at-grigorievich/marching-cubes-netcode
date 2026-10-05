using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Интерфейс забега (M1.9): опыт, шкала стадии со значками событий и кривой давления,
    /// объявление и предупреждение, здоровье с временным, заряды рывка, три слота пушек,
    /// полоса Матки, указатель логова, маркеры попаданий, виньетка и дуги направления урона,
    /// хитин и убитые.
    ///
    /// Два канваса: статичный (рамки, шкала, кривая) и динамичный (числа, полосы) — динамичный
    /// перестраивается без статичного. Тексты меняются только при смене числа и через
    /// <c>SetText</c> с числовыми аргументами — без выделений памяти в кадре.
    /// </summary>
    public sealed class HudView
    {
        private const int CurveBars = 56;
        private const int Arcs = 4;
        private const float ArcLife = 0.7f;
        private const float WarnAhead = 8f;

        private readonly GameUi _ui;

        private RectTransform _static;
        private RectTransform _dynamic;

        // опыт и стадия
        private Image _xpFill;
        private TextMeshProUGUI _level;
        private Image _stageFill;
        private TextMeshProUGUI _timer;
        private RectTransform _stageTrack;
        private readonly List<Image> _eventMarks = new List<Image>();
        private readonly Image[] _curve = new Image[CurveBars];

        // объявление и рог орды
        private TextMeshProUGUI _banner;
        private TextMeshProUGUI _warning;
        private string _bannerText = "";
        private bool _warned;
        private float _warningLeft;

        // здоровье и рывок
        private Image _healthFill;
        private Image _tempFill;
        private Image _healthBack;
        private TextMeshProUGUI _healthText;
        private readonly List<Image> _dashDots = new List<Image>();

        // пушки
        private readonly WeaponSlotView[] _slots = new WeaponSlotView[PlayerArsenal.Slots];

        // босс
        private RectTransform _boss;
        private Image _bossFill;
        private TextMeshProUGUI _bossText;

        // указатель логова
        private RectTransform _pointer;
        private TextMeshProUGUI _pointerText;

        // прицел и попадания
        private RectTransform _crosshair;
        private readonly Image[] _markers = new Image[4];
        private float _markerLeft;
        private float _markerScale = 1f;

        // урон
        private Image _vignette;
        private Image _web;
        private readonly Image[] _arcs = new Image[Arcs];
        private readonly float[] _arcLeft = new float[Arcs];
        private readonly float[] _arcYaw = new float[Arcs];
        private int _nextArc;
        private float _hurtFlash;

        // счётчики
        private TextMeshProUGUI _chitin;
        private TextMeshProUGUI _kills;

        // последние показанные значения — текст меняется только при смене числа
        private int _shownLevel = -1;
        private int _shownSecond = -1;
        private int _shownHealth = -1;
        private int _shownMax = -1;
        private int _shownChitin = -1;
        private int _shownKills = -1;
        private int _shownSteps = int.MinValue;
        private int _shownPylons = -1;
        private float _scheduledFor = -1f;

        private sealed class WeaponSlotView
        {
            public Image Frame;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Ammo;
            public Image Gauge;
            public Image GaugeBack;
            public Image Reload;
            public Image Alt;
            public TextMeshProUGUI AltCount;
            public WeaponId Shown = WeaponId.Count;
            public int ShownAmmo = int.MinValue;
            public int ShownCapacity = -1;
            public int ShownLevel = -1;
            public int ShownAlt = -1;
        }

        public HudView(GameUi ui) => _ui = ui;

        public void Build(Transform staticRoot, Transform dynamicRoot)
        {
            _static = UiKit.Node(staticRoot, "HUD Static").Stretch();
            _dynamic = UiKit.Node(dynamicRoot, "HUD Dynamic").Stretch();

            BuildTop();
            BuildBanner();
            BuildHealth();
            BuildWeapons();
            BuildBoss();
            BuildPointer();
            BuildCrosshair();
            BuildDamage();
            BuildCounters();
        }

        public void SetVisible(bool visible)
        {
            _static.gameObject.SetActive(visible);
            _dynamic.gameObject.SetActive(visible);
        }

        // ------------------------------------------------------------------ сборка

        private void BuildTop()
        {
            var xpBack = UiKit.Panel(_static, "XP Back", new Color(0f, 0f, 0f, 0.55f));
            xpBack.rectTransform.anchorMin = new Vector2(0f, 1f);
            xpBack.rectTransform.anchorMax = new Vector2(1f, 1f);
            xpBack.rectTransform.pivot = new Vector2(0.5f, 1f);
            xpBack.rectTransform.offsetMin = new Vector2(64f, -16f);
            xpBack.rectTransform.offsetMax = new Vector2(-12f, -6f);

            _xpFill = UiKit.Panel(_dynamic, "XP Fill", UiKit.Amber);
            _xpFill.rectTransform.anchorMin = new Vector2(0f, 1f);
            _xpFill.rectTransform.anchorMax = new Vector2(1f, 1f);
            _xpFill.rectTransform.pivot = new Vector2(0.5f, 1f);
            _xpFill.rectTransform.offsetMin = new Vector2(66f, -14f);
            _xpFill.rectTransform.offsetMax = new Vector2(-14f, -8f);
            _xpFill.type = Image.Type.Filled;
            _xpFill.fillMethod = Image.FillMethod.Horizontal;

            var circle = UiKit.Panel(_static, "Level Circle", new Color(0.12f, 0.08f, 0.05f, 0.9f), UiKit.Circle);
            circle.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, -2f), new Vector2(46f, 46f));

            _level = UiKit.Text(_dynamic, "Level", 24f, UiKit.Amber, TextAlignmentOptions.Center, true);
            _level.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, -2f), new Vector2(46f, 46f));

            // Шкала стадии: заполнение, значки событий, бледная кривая давления под ней.
            var track = UiKit.Panel(_static, "Stage Track", new Color(0f, 0f, 0f, 0.5f));
            track.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(560f, 10f));
            _stageTrack = track.rectTransform;

            for (var i = 0; i < CurveBars; i++)
            {
                var bar = UiKit.Panel(_static, "Curve " + i, new Color(1f, 0.55f, 0.25f, 0.22f));
                bar.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, 1f),
                    new Vector2(-280f + i * 560f / CurveBars, -38f), new Vector2(560f / CurveBars - 1f, 2f));
                _curve[i] = bar;
            }

            _stageFill = UiKit.Panel(_dynamic, "Stage Fill", new Color(0.85f, 0.75f, 0.6f, 0.55f));
            _stageFill.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -27f), new Vector2(556f, 6f));
            _stageFill.type = Image.Type.Filled;
            _stageFill.fillMethod = Image.FillMethod.Horizontal;

            _timer = UiKit.Text(_dynamic, "Timer", 22f, UiKit.Paper, TextAlignmentOptions.Right, true);
            _timer.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(1f, 1f), new Vector2(-290f, -18f), new Vector2(90f, 28f));
        }

        private void RebuildSchedule(CaveRunDirector director)
        {
            foreach (var mark in _eventMarks) Object.Destroy(mark.gameObject);
            _eventMarks.Clear();

            foreach (var e in director.Schedule)
            {
                var color = e.Type switch
                {
                    StageEventType.Elite => new Color(1f, 0.45f, 0.25f, 1f),
                    StageEventType.Swarm => UiKit.Amber,
                    _ => UiKit.Blood
                };

                var mark = UiKit.Panel(_static, "Event " + e.Type, color);
                mark.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
                    new Vector2(-280f + Mathf.Clamp01(e.Share) * 560f, -31f), new Vector2(11f, 11f));
                mark.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);

                _eventMarks.Add(mark);
            }

            // Кривая давления — бледными столбиками: игрок видит, что волна близко.
            var curve = director.Curve;
            var peak = 0.01f;

            for (var i = 0; i < CurveBars; i++) peak = Mathf.Max(peak, curve.Intensity((i + 0.5f) / CurveBars));

            for (var i = 0; i < CurveBars; i++)
            {
                var height = 2f + 14f * curve.Intensity((i + 0.5f) / CurveBars) / peak;
                _curve[i].rectTransform.sizeDelta = new Vector2(_curve[i].rectTransform.sizeDelta.x, height);
            }

            _scheduledFor = director.StageSeconds;
        }

        private void BuildBanner()
        {
            _banner = UiKit.Text(_dynamic, "Banner", 38f, new Color(1f, 0.85f, 0.6f), TextAlignmentOptions.Center, true);
            _banner.rectTransform.Place(new Vector2(0.5f, 0.82f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 60f));
            _banner.outlineWidth = 0.15f;
            _banner.outlineColor = new Color32(0, 0, 0, 200);

            _warning = UiKit.Text(_dynamic, "Warning", 24f, new Color(0.85f, 0.75f, 0.95f), TextAlignmentOptions.Center);
            _warning.rectTransform.Place(new Vector2(0.5f, 0.82f), new Vector2(0.5f, 0.5f), new Vector2(0f, -46f), new Vector2(900f, 40f));
            _warning.fontStyle = FontStyles.Italic;
        }

        private void BuildHealth()
        {
            _healthBack = UiKit.Panel(_static, "Health Back", new Color(0f, 0f, 0f, 0.6f));
            _healthBack.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(24f, 24f), new Vector2(330f, 24f));

            _tempFill = UiKit.Panel(_dynamic, "Temp Fill", new Color(0.62f, 0.6f, 0.58f, 0.85f));
            _tempFill.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(26f, 26f), new Vector2(326f, 20f));
            _tempFill.type = Image.Type.Filled;
            _tempFill.fillMethod = Image.FillMethod.Horizontal;

            _healthFill = UiKit.Panel(_dynamic, "Health Fill", UiKit.Blood);
            _healthFill.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(26f, 26f), new Vector2(326f, 20f));
            _healthFill.type = Image.Type.Filled;
            _healthFill.fillMethod = Image.FillMethod.Horizontal;

            _healthText = UiKit.Text(_dynamic, "Health Text", 18f, UiKit.Paper, TextAlignmentOptions.Center, true);
            _healthText.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(24f, 24f), new Vector2(330f, 24f));

            for (var i = 0; i < 5; i++)
            {
                var dot = UiKit.Panel(_dynamic, "Dash " + i, UiKit.Teal, UiKit.Circle);
                dot.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(26f + i * 22f, 56f), new Vector2(16f, 16f));
                _dashDots.Add(dot);
            }
        }

        private void BuildWeapons()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var x = -24f - (PlayerArsenal.Slots - 1 - i) * 168f;
                var slot = new WeaponSlotView();

                slot.Frame = UiKit.Panel(_static, "Weapon " + i, new Color(0.07f, 0.055f, 0.045f, 0.82f), UiKit.Rounded);
                slot.Frame.rectTransform.Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(x, 24f), new Vector2(160f, 74f));

                var frame = slot.Frame.transform;

                slot.Name = UiKit.Text(frame, "Name", 16f, UiKit.Paper, TextAlignmentOptions.TopLeft, true);
                slot.Name.rectTransform.Stretch(10f, 34f, 6f, 40f);

                slot.Ammo = UiKit.Text(frame, "Ammo", 22f, UiKit.Amber, TextAlignmentOptions.BottomLeft, true);
                slot.Ammo.rectTransform.Stretch(10f, 40f, 30f, 8f);

                slot.Gauge = UiKit.Bar(frame, "Gauge", new Color(0f, 0f, 0f, 0.6f), UiKit.Amber, out slot.GaugeBack);
                slot.GaugeBack.rectTransform.Place(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(10f, 10f), new Vector2(104f, 10f));

                slot.Reload = UiKit.Panel(frame, "Reload", new Color(0f, 0f, 0f, 0.55f));
                slot.Reload.rectTransform.Stretch();
                slot.Reload.type = Image.Type.Filled;
                slot.Reload.fillMethod = Image.FillMethod.Vertical;
                slot.Reload.fillOrigin = 1;

                slot.Alt = UiKit.Panel(frame, "Alt", new Color(0.55f, 0.75f, 1f, 0.9f), UiKit.Circle);
                slot.Alt.rectTransform.Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-8f, 8f), new Vector2(26f, 26f));
                slot.Alt.type = Image.Type.Filled;
                slot.Alt.fillMethod = Image.FillMethod.Radial360;

                slot.AltCount = UiKit.Text(slot.Alt.transform, "Count", 15f, new Color(0.05f, 0.05f, 0.1f), TextAlignmentOptions.Center, true);
                slot.AltCount.rectTransform.Stretch();

                _slots[i] = slot;
            }
        }

        private void BuildBoss()
        {
            _boss = UiKit.Node(_dynamic, "Boss");
            _boss.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -62f), new Vector2(640f, 40f));

            var back = UiKit.Panel(_boss, "Back", new Color(0f, 0f, 0f, 0.6f));
            back.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(640f, 16f));

            _bossFill = UiKit.Panel(_boss, "Fill", new Color(0.75f, 0.12f, 0.3f));
            _bossFill.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(636f, 12f));
            _bossFill.type = Image.Type.Filled;
            _bossFill.fillMethod = Image.FillMethod.Horizontal;

            // Засечки щитов на 2/3 и 1/3.
            foreach (var share in new[] { 2f / 3f, 1f / 3f })
            {
                var notch = UiKit.Panel(_boss, "Notch", new Color(0.55f, 0.75f, 1f, 0.95f));
                notch.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-318f + 636f * share, 0f),
                    new Vector2(3f, 20f));
            }

            _bossText = UiKit.Text(_boss, "Name", 18f, UiKit.Paper, TextAlignmentOptions.Bottom, true);
            _bossText.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(640f, 22f));

            _boss.gameObject.SetActive(false);
        }

        private void BuildPointer()
        {
            _pointer = UiKit.Node(_dynamic, "Lair Pointer");
            _pointer.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40f, 40f));

            var arrow = UiKit.Panel(_pointer, "Arrow", new Color(1f, 0.3f, 0.2f, 0.9f), UiKit.Arrow);
            arrow.rectTransform.Stretch();

            _pointerText = UiKit.Text(_dynamic, "Lair Distance", 16f, new Color(1f, 0.6f, 0.5f), TextAlignmentOptions.Center, true);
            _pointerText.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(100f, 22f));

            _pointer.gameObject.SetActive(false);
            _pointerText.gameObject.SetActive(false);
        }

        private void BuildCrosshair()
        {
            _crosshair = UiKit.Node(_dynamic, "Crosshair");
            _crosshair.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40f, 40f));

            var color = new Color(1f, 0.9f, 0.75f, 0.85f);

            Line(_crosshair, new Vector2(-12f, 0f), new Vector2(9f, 2f), color);
            Line(_crosshair, new Vector2(12f, 0f), new Vector2(9f, 2f), color);
            Line(_crosshair, new Vector2(0f, -12f), new Vector2(2f, 9f), color);
            Line(_crosshair, new Vector2(0f, 12f), new Vector2(2f, 9f), color);

            // Маркер попадания: четыре косых штриха вокруг прицела.
            for (var i = 0; i < 4; i++)
            {
                var angle = 45f + i * 90f;
                var direction = Quaternion.Euler(0f, 0f, angle) * Vector3.up;

                var marker = Line(_crosshair, (Vector2)direction * 14f, new Vector2(3f, 10f), Color.white);
                marker.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
                marker.enabled = false;

                _markers[i] = marker;
            }
        }

        private static Image Line(RectTransform parent, Vector2 position, Vector2 size, Color color)
        {
            var line = UiKit.Panel(parent, "Line", color);
            line.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position, size);
            return line;
        }

        private void BuildDamage()
        {
            _vignette = UiKit.Panel(_dynamic, "Vignette", new Color(0.7f, 0f, 0f, 0f), UiKit.Vignette);
            _vignette.rectTransform.Stretch(-40f, -40f, -40f, -40f);
            _vignette.transform.SetAsFirstSibling();

            _web = UiKit.Panel(_dynamic, "Web", new Color(0.75f, 0.9f, 0.65f, 0f), UiKit.Vignette);
            _web.rectTransform.Stretch(-60f, -60f, -60f, -60f);
            _web.transform.SetSiblingIndex(1);

            for (var i = 0; i < Arcs; i++)
            {
                var arc = UiKit.Panel(_dynamic, "Arc " + i, new Color(1f, 0.15f, 0.08f, 0f), UiKit.Arc);
                arc.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 420f));
                _arcs[i] = arc;
            }
        }

        private void BuildCounters()
        {
            var chitinIcon = UiKit.Panel(_static, "Chitin Icon", UiKit.Teal);
            chitinIcon.rectTransform.Place(new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(-150f, -32f), new Vector2(12f, 12f));
            chitinIcon.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);

            _chitin = UiKit.Text(_dynamic, "Chitin", 20f, UiKit.Teal, TextAlignmentOptions.Left, true);
            _chitin.rectTransform.Place(new Vector2(1f, 1f), new Vector2(0f, 0.5f), new Vector2(-138f, -32f), new Vector2(80f, 26f));

            _kills = UiKit.Text(_dynamic, "Kills", 20f, UiKit.Dim, TextAlignmentOptions.Right, true);
            _kills.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-14f, -32f), new Vector2(90f, 26f));
        }

        // ------------------------------------------------------------------ события

        public void OnHitMarker(HitMarker marker)
        {
            _markerLeft = marker.Kill ? 0.22f : 0.12f;
            _markerScale = marker.Crit ? 1.6f : 1f;

            var color = marker.Kill ? new Color(1f, 0.25f, 0.15f) : marker.Crit ? new Color(1f, 0.85f, 0.3f) : Color.white;

            foreach (var line in _markers)
            {
                line.enabled = true;
                line.color = color;
            }
        }

        public void OnDamaged(DamageHit hit, DamageResult result, Transform eye)
        {
            if (result.Dealt <= 0f || eye == null) return;

            _hurtFlash = Mathf.Min(1f, _hurtFlash + 0.35f + result.Dealt * 0.02f);

            // Дуга направления: откуда пришло, относительно взгляда по горизонтали.
            var from = hit.From - eye.position;
            from.y = 0f;
            if (from.sqrMagnitude < 1e-4f) return;

            var forward = eye.forward;
            forward.y = 0f;

            var yaw = Vector3.SignedAngle(forward.normalized, from.normalized, Vector3.up);

            _arcYaw[_nextArc] = yaw;
            _arcLeft[_nextArc] = ArcLife;
            _nextArc = (_nextArc + 1) % Arcs;
        }

        // ------------------------------------------------------------------ кадр

        public void Tick(float deltaTime)
        {
            var controller = _ui.Controller;
            var director = _ui.Director;
            var rig = _ui.Rig;

            if (controller != null) TickRun(controller);
            if (director != null) TickStage(director, deltaTime);
            if (rig != null) TickBody(rig, deltaTime);

            TickWeapons(_ui.Arsenal);
            TickBoss(_ui.Queen);
            TickPointer(_ui.Queen, rig, director, controller);
            TickMarkers(deltaTime);
        }

        private void TickRun(CaveRunController controller)
        {
            var session = controller.Session;

            _xpFill.fillAmount = session.XpFraction;

            if (session.Level != _shownLevel)
            {
                _shownLevel = session.Level;
                _level.SetText("{0}", session.Level);
            }

            var chitin = Mathf.FloorToInt(session.Chitin);

            if (chitin != _shownChitin)
            {
                _shownChitin = chitin;
                _chitin.SetText("{0}", chitin);
            }

            if (session.Kills != _shownKills)
            {
                _shownKills = session.Kills;
                _kills.SetText("{0}", session.Kills);
            }
        }

        private void TickStage(CaveRunDirector director, float deltaTime)
        {
            if (!Mathf.Approximately(_scheduledFor, director.StageSeconds)) RebuildSchedule(director);

            _stageFill.fillAmount = Mathf.Clamp01(director.Progress);

            var seconds = director.Phase == CaveRunDirector.RunPhase.FinalSwarm
                ? Mathf.FloorToInt(director.Elapsed - director.StageSeconds)
                : Mathf.FloorToInt(Mathf.Max(0f, director.StageSeconds - director.Elapsed));

            if (seconds != _shownSecond)
            {
                _shownSecond = seconds;
                _timer.SetText(director.Phase == CaveRunDirector.RunPhase.FinalSwarm ? "+{0:00}:{1:00}" : "{0:00}:{1:00}",
                    seconds / 60, seconds % 60);
                _timer.color = director.Phase == CaveRunDirector.RunPhase.FinalSwarm ? UiKit.Blood : UiKit.Paper;
            }

            // Объявление: крупно, гаснет само.
            var alpha = Mathf.Clamp01(director.BannerLeft);

            if (alpha > 0f && !ReferenceEquals(director.Banner, _bannerText))
            {
                _bannerText = director.Banner;
                _banner.text = BannerText(director.Banner);
            }

            _banner.alpha = alpha;

            // Рог орды: за 8 секунд до роя и элиты — «Своды дрожат…» и низкий гул.
            if (director.TryGetNextEvent(out var next, out var left) && left <= WarnAhead && next.Type != StageEventType.Final)
            {
                if (!_warned)
                {
                    _warned = true;
                    _warningLeft = WarnAhead;
                    _warning.text = Loc.T(next.Type == StageEventType.Swarm ? "hud.warn.swarm" : "hud.warn.elite");
                    _ui.Audio?.Play(SoundId.Horn, 0.9f, 1f, null, 8);
                }
            }
            else
            {
                _warned = false;
            }

            _warningLeft = Mathf.Max(0f, _warningLeft - deltaTime);
            _warning.alpha = Mathf.Clamp01(_warningLeft / 2f);
        }

        /// <summary>Объявления директора — ключи или готовые строки (имя вида роя пишется как есть).</summary>
        private static string BannerText(string banner)
        {
            if (string.IsNullOrEmpty(banner)) return "";
            if (Loc.Has(banner)) return Loc.T(banner);

            var colon = banner.IndexOf(':');
            if (colon > 0)
            {
                var key = banner.Substring(0, colon);
                if (Loc.Has(key)) return Loc.T(key) + banner.Substring(colon);
            }

            return banner;
        }

        private void TickBody(CatacombTestRig rig, float deltaTime)
        {
            var vitals = rig.Vitals;

            if (vitals != null)
            {
                var max = Mathf.Max(1f, vitals.MaxHealth);

                _healthFill.fillAmount = vitals.Health / max;
                _tempFill.fillAmount = (vitals.Health + vitals.Temp) / max;

                var health = Mathf.CeilToInt(vitals.Health);
                var maxShown = Mathf.RoundToInt(max);

                if (health != _shownHealth || maxShown != _shownMax)
                {
                    _shownHealth = health;
                    _shownMax = maxShown;
                    _healthText.SetText("{0} / {1}", health, maxShown);
                }

                // Мигает ниже четверти.
                var low = vitals.Fraction < 0.25f && !vitals.IsDead;
                var blink = low ? 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) : 1f;
                _healthFill.color = new Color(UiKit.Blood.r * blink, UiKit.Blood.g, UiKit.Blood.b, 1f);

                // Виньетка: постоянная по здоровью и вспышка от свежего удара.
                _hurtFlash = Mathf.Max(0f, _hurtFlash - deltaTime * 2.5f);
                var danger = Mathf.Clamp01((0.5f - vitals.Fraction) / 0.5f);
                _vignette.color = new Color(0.7f, 0f, 0f, Mathf.Clamp01(danger * 0.55f + _hurtFlash * 0.45f));
            }

            var mobility = rig.Mobility;

            if (mobility != null)
            {
                var dash = mobility.Dash;

                for (var i = 0; i < _dashDots.Count; i++)
                {
                    var dot = _dashDots[i];
                    var visible = i < dash.MaxCharges;

                    dot.enabled = visible;
                    if (!visible) continue;

                    var fill = Mathf.Clamp01(dash.Charges - i);
                    dot.color = fill >= 1f ? UiKit.Teal : new Color(UiKit.Teal.r, UiKit.Teal.g, UiKit.Teal.b, 0.2f + 0.3f * fill);
                }
            }

            _web.color = new Color(0.75f, 0.9f, 0.65f, rig.IsEntangled ? 0.35f : 0f);

            for (var i = 0; i < Arcs; i++)
            {
                _arcLeft[i] = Mathf.Max(0f, _arcLeft[i] - deltaTime);
                var arc = _arcs[i];

                arc.color = new Color(1f, 0.15f, 0.08f, 0.75f * _arcLeft[i] / ArcLife);
                arc.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -_arcYaw[i]);
            }
        }

        private void TickWeapons(PlayerArsenal arsenal)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var view = _slots[i];
                var weapon = arsenal != null && i < arsenal.Weapons.Count ? arsenal.Weapons[i] : null;

                view.Frame.gameObject.SetActive(weapon != null);
                if (weapon == null) continue;

                var hud = weapon.Hud;
                var active = arsenal.ActiveSlot == i;

                view.Frame.color = active
                    ? new Color(0.2f, 0.14f, 0.08f, 0.95f)
                    : new Color(0.07f, 0.055f, 0.045f, 0.7f);

                if (view.Shown != hud.Id || view.ShownLevel != hud.Level)
                {
                    view.Shown = hud.Id;
                    view.ShownLevel = hud.Level;
                    view.Name.text = $"{i + 1}  {Loc.T(weapon.Def.NameKey)}  <size=70%><color=#a89c86>{Loc.F("hud.level", hud.Level)}{(hud.Evolved ? " ★" : "")}</color></size>";
                }

                var showAmmo = hud.Ammo >= 0;
                view.Ammo.gameObject.SetActive(showAmmo);
                view.GaugeBack.gameObject.SetActive(hud.Gauge >= 0f);

                if (showAmmo && (hud.Ammo != view.ShownAmmo || hud.Capacity != view.ShownCapacity))
                {
                    view.ShownAmmo = hud.Ammo;
                    view.ShownCapacity = hud.Capacity;
                    view.Ammo.SetText("{0}<size=60%>/{1}</size>", hud.Ammo, hud.Capacity);
                }

                if (hud.Gauge >= 0f)
                {
                    view.Gauge.fillAmount = hud.Gauge;
                    view.Gauge.color = hud.GaugeIsHeat ? Color.Lerp(UiKit.Amber, UiKit.Blood, hud.Gauge) : UiKit.Amber;
                }

                view.Reload.fillAmount = hud.Reload > 0f && hud.Reload < 1f ? 1f - hud.Reload : 0f;

                view.Alt.gameObject.SetActive(hud.AltUnlocked);

                if (hud.AltUnlocked)
                {
                    view.Alt.fillAmount = hud.AltCharges > 0 ? 1f : Mathf.Clamp01(hud.AltReady);
                    view.Alt.color = hud.AltCharges > 0 ? new Color(0.55f, 0.75f, 1f, 0.95f) : new Color(0.4f, 0.45f, 0.55f, 0.6f);

                    if (hud.AltCharges != view.ShownAlt)
                    {
                        view.ShownAlt = hud.AltCharges;
                        view.AltCount.SetText(hud.AltCharges > 1 ? "{0}" : "", hud.AltCharges);
                    }
                }
            }
        }

        private void TickBoss(CaveQueen queen)
        {
            var visible = queen != null && (queen.State == CaveQueen.QueenState.Awake || queen.State == CaveQueen.QueenState.Shielded);
            _boss.gameObject.SetActive(visible);

            if (!visible) return;

            _bossFill.fillAmount = queen.HealthFraction;
            _bossFill.color = queen.State == CaveQueen.QueenState.Shielded ? new Color(0.45f, 0.65f, 1f) : new Color(0.75f, 0.12f, 0.3f);

            var pylons = queen.State == CaveQueen.QueenState.Shielded ? queen.PylonsLitInPhase * 10 + queen.PylonsInPhase : -1;

            if (pylons != _shownPylons)
            {
                _shownPylons = pylons;
                _bossText.text = pylons >= 0
                    ? Loc.F("hud.boss.shield", queen.PylonsLitInPhase, queen.PylonsInPhase)
                    : Loc.T(queen.Profile != null ? queen.Profile.NameKey : "hud.boss.name");
            }
        }

        /// <summary>
        /// Указатель логова: стрелка у края экрана и «~80 м» ПО ПУТИ (заливка, не по прямой —
        /// грабли №42). До пробуждения — на логово, с 1:30 или сразу с «Чутьём логова»;
        /// под щитом — на ближайший незапущенный генератор.
        /// </summary>
        private void TickPointer(CaveQueen queen, CatacombTestRig rig, CaveRunDirector director, CaveRunController controller)
        {
            var show = false;
            var target = Vector3.zero;

            if (queen != null && rig != null && director != null && queen.HasLair)
            {
                var sense = controller != null && controller.Stats.Has(Stat.LairSense);

                if (queen.State == CaveQueen.QueenState.Dormant && director.Phase != CaveRunDirector.RunPhase.Victory &&
                    (director.Elapsed >= 90f || sense))
                {
                    show = true;
                    target = queen.LairPosition;
                }
                else if (queen.State == CaveQueen.QueenState.Shielded && queen.TryGetNearestDarkPylon(rig.transform.position, out var pylon))
                {
                    show = true;
                    target = pylon;
                }
            }

            _pointer.gameObject.SetActive(show);
            _pointerText.gameObject.SetActive(show);

            if (!show) return;

            var camera = _ui.Camera;
            if (camera == null) return;

            var viewport = camera.WorldToViewportPoint(target + Vector3.up);
            var behind = viewport.z < 0f;

            var centre = new Vector2(viewport.x - 0.5f, viewport.y - 0.5f);
            if (behind) centre = -centre;

            var onScreen = !behind && Mathf.Abs(centre.x) < 0.45f && Mathf.Abs(centre.y) < 0.42f;

            if (!onScreen)
            {
                // Две пары, а не Mathf.Max(a, b, c): у трёх аргументов перегрузка через params —
                // массив в каждом кадре, пока указатель за краем экрана (поймал UiCheck).
                var scale = Mathf.Max(Mathf.Max(Mathf.Abs(centre.x) / 0.45f, Mathf.Abs(centre.y) / 0.42f), 1e-3f);
                centre /= scale;
            }

            var canvasSize = ((RectTransform)_pointer.parent).rect.size;
            var position = new Vector2(centre.x * canvasSize.x, centre.y * canvasSize.y);

            _pointer.anchoredPosition = position;
            _pointer.localRotation = Quaternion.Euler(0f, 0f, onScreen ? 180f : Mathf.Atan2(-centre.x, centre.y) * Mathf.Rad2Deg);

            _pointerText.rectTransform.anchoredPosition = position + (onScreen ? new Vector2(0f, 34f) : -centre.normalized * 40f);

            var steps = _ui.Crowd != null ? _ui.Crowd.PathStepsTo(target) : -1;

            if (steps != _shownSteps)
            {
                _shownSteps = steps;

                if (steps < 0 || steps == SpiderFlowField.Unreachable) _pointerText.SetText(Loc.T("hud.far"));
                else _pointerText.SetText(Loc.T("hud.distance"), Mathf.RoundToInt(steps * (_ui.Crowd != null ? _ui.Crowd.StepLength : 1f)));
            }

            var pulse = queen.State == CaveQueen.QueenState.Dormant ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f)) : 1f;
            _pointer.localScale = Vector3.one * pulse;
        }

        private void TickMarkers(float deltaTime)
        {
            if (_markerLeft <= 0f) return;

            _markerLeft -= Time.unscaledDeltaTime;

            if (_markerLeft <= 0f)
            {
                foreach (var line in _markers) line.enabled = false;
                return;
            }

            _crosshair.localScale = Vector3.one * Mathf.Lerp(1f, _markerScale, Mathf.Clamp01(_markerLeft * 6f));
        }
    }
}
