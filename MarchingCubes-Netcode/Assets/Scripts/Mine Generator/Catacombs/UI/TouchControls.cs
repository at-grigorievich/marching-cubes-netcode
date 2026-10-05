using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Тач-управление (M5.5): плавающий джойстик слева, взгляд перетаскиванием справа, кнопки огня,
    /// альта, рывка, смены (касание — следующая, удержание — круговое меню из трёх) и паузы,
    /// «действие» — только у логова и провала. Источник команд стенда (<see cref="IPlayerInputSource"/>)
    /// поверх клавиатуры: на ноутбуке с сенсорным экраном работает и то и другое.
    ///
    /// Помощь прицеливания — только для пальцев: взгляд мягко тянется к ближайшему пауку в конусе 6°,
    /// над целью чувствительность ×0.6 («липкость»). Автоогонь — пока перекрестие на цели, кроме
    /// гранатомёта и рельсы: граната себе под ноги и рельса в упор — смерть.
    ///
    /// Касания приходят из <c>Input.touches</c> (старый Input Manager работает в мобильных браузерах);
    /// прогон подставляет свои через <see cref="Injected"/>.
    /// </summary>
    public sealed class TouchControls : IPlayerInputSource
    {
        public const float AssistCone = 6f;
        public const float StickyCone = 2.5f;
        public const float AutoFireCone = 4f;
        public const float AssistRange = 28f;
        public const float StickyScale = 0.6f;

        /// <summary>Наименьший размер кнопки — 9 мм (M5.5), иначе палец промахивается.</summary>
        public const float MinButtonMm = 9f;

        public readonly TouchRouter Router = new TouchRouter();

        /// <summary>Касания для прогона вместо <c>Input.touches</c>.</summary>
        public List<TouchPoint> Injected;

        /// <summary>Помощь прицеливания и автоогонь — в этом кадре (для прогона и подсказок).</summary>
        public bool HasTarget { get; private set; }
        public float TargetAngle { get; private set; }
        public bool AutoFiring { get; private set; }

        private readonly GameUi _ui;
        private readonly DesktopInputSource _desktop = new DesktopInputSource();
        private readonly List<TouchPoint> _touches = new List<TouchPoint>();
        private readonly List<int> _found = new List<int>(1);

        private RectTransform _root;
        private Canvas _canvas;
        private Image _joyBase;
        private Image _joyKnob;
        private readonly Image[] _buttons = new Image[(int)TouchButton.Count];
        private readonly Image[] _radial = new Image[3];
        private readonly Vector3[] _corners = new Vector3[4];

        public TouchControls(GameUi ui) => _ui = ui;

        public bool Visible => _root != null && _root.gameObject.activeSelf;

        // ------------------------------------------------------------------ сборка

        public void Build(RectTransform parent, Canvas canvas)
        {
            _canvas = canvas;
            _root = UiKit.Node(parent, "Touch Controls").Stretch();

            _joyBase = UiKit.Panel(_root, "Joystick", new Color(1f, 1f, 1f, 0.12f), UiKit.Ring);
            _joyBase.rectTransform.sizeDelta = new Vector2(220f, 220f);
            _joyKnob = UiKit.Panel(_root, "Knob", new Color(1f, 0.85f, 0.6f, 0.35f), UiKit.Circle);
            _joyKnob.rectTransform.sizeDelta = new Vector2(90f, 90f);

            // Значки — только из букв, что есть в шрифте (◆ и ⇄ в Roboto нет — рисовались квадратами);
            // слова — из таблицы строк, чтобы UiCheck сверил их буквы со шрифтом.
            Button(TouchButton.Fire, "●", null, new Vector2(-120f, 140f), 150f, new Color(0.55f, 0.18f, 0.06f, 0.55f));
            Button(TouchButton.Alt, null, "touch.alt", new Vector2(-275f, 95f), 104f, new Color(0.3f, 0.2f, 0.1f, 0.5f));
            Button(TouchButton.Dash, "»", null, new Vector2(-120f, 310f), 104f, new Color(0.15f, 0.3f, 0.32f, 0.5f));
            Button(TouchButton.Swap, null, "touch.swap", new Vector2(-275f, 245f), 96f, new Color(0.25f, 0.22f, 0.18f, 0.5f));
            Button(TouchButton.Interact, null, "touch.use", new Vector2(-300f, 400f), 110f, new Color(0.5f, 0.32f, 0.05f, 0.65f));

            // Пауза — справа сверху под хитином: слева сверху значок уровня.
            var pause = Button(TouchButton.Pause, "II", null, Vector2.zero, 72f, new Color(0f, 0f, 0f, 0.4f));
            pause.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -64f), pause.rectTransform.sizeDelta);

            Loc.Changed += Relabel;
            Relabel();

            for (var i = 0; i < _radial.Length; i++)
            {
                _radial[i] = UiKit.Panel(_root, "Slot " + (i + 1), new Color(0.2f, 0.16f, 0.12f, 0.75f), UiKit.Circle);
                _radial[i].rectTransform.sizeDelta = new Vector2(80f, 80f);

                var label = UiKit.Text(_radial[i].transform, "Label", 30f, UiKit.Paper, TextAlignmentOptions.Center, true);
                label.rectTransform.Stretch();
                label.text = (i + 1).ToString();

                _radial[i].gameObject.SetActive(false);
            }

            _root.gameObject.SetActive(false);
        }

        private readonly TextMeshProUGUI[] _labels = new TextMeshProUGUI[(int)TouchButton.Count];
        private readonly string[] _keys = new string[(int)TouchButton.Count];

        private Image Button(TouchButton id, string glyph, string key, Vector2 position, float size, Color color)
        {
            var image = UiKit.Panel(_root, id.ToString(), color, UiKit.Circle);
            image.rectTransform.Place(new Vector2(1f, 0f), new Vector2(0.5f, 0.5f), position, new Vector2(size, size));

            var label = UiKit.Text(image.transform, "Glyph", size * (key != null ? 0.2f : 0.36f), UiKit.Paper, TextAlignmentOptions.Center, true);
            label.rectTransform.Stretch();
            label.text = glyph ?? "";

            _buttons[(int)id] = image;
            _labels[(int)id] = label;
            _keys[(int)id] = key;
            return image;
        }

        /// <summary>Отписаться от смены языка — таблица строк живёт дольше интерфейса.</summary>
        public void Dispose() => Loc.Changed -= Relabel;

        private void Relabel()
        {
            for (var i = 0; i < _keys.Length; i++)
            {
                if (_keys[i] != null && _labels[i] != null) _labels[i].text = Loc.T(_keys[i]);
            }
        }

        // ------------------------------------------------------------------ кадр

        /// <summary>Показывать ли управление: только в игре — на экранах кнопки перекрывали бы свои.</summary>
        public void Tick(bool show)
        {
            if (_root == null) return;

            if (_root.gameObject.activeSelf != show)
            {
                _root.gameObject.SetActive(show);
                if (!show) Router.Reset();
            }

            if (!show) return;

            ScaleButtons();

            var rig = _ui.Rig;
            var interact = false;

            if (rig != null)
            {
                var body = rig.BodyCentre;
                var queen = _ui.Queen;
                var controller = _ui.Controller;

                interact = (queen != null && queen.CanWake(body)) ||
                           (controller != null && controller.DescentOpen && (body - controller.DescentPosition).magnitude < 3.5f);
            }

            _buttons[(int)TouchButton.Interact].gameObject.SetActive(interact);

            for (var i = 0; i < _buttons.Length; i++)
            {
                Router.Visible[i] = _buttons[i].gameObject.activeSelf;
                if (Router.Visible[i]) Router.Buttons[i] = ScreenRect(_buttons[i].rectTransform);

                var held = Router.Held((TouchButton)i) || (i == (int)TouchButton.Fire && AutoFiring);
                var color = _buttons[i].color;
                color.a = held ? 0.85f : 0.5f;
                _buttons[i].color = color;
            }

            UpdateJoystick();
            UpdateRadial();
        }

        /// <summary>Размер кнопок — не меньше 9 мм в точках устройства.</summary>
        private void ScaleButtons()
        {
            var dpi = Screen.dpi > 1f ? Screen.dpi : 160f;
            Router.Dpi = dpi;

            var scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            var minUnits = MinButtonMm / 25.4f * dpi / scale;

            foreach (var button in _buttons)
            {
                var rect = button.rectTransform;
                var size = Mathf.Max(rect.sizeDelta.x, minUnits);
                if (!Mathf.Approximately(size, rect.sizeDelta.x)) rect.sizeDelta = new Vector2(size, size);
            }

            Router.JoystickRadius = 0.45f * 220f * scale;
        }

        private Rect ScreenRect(RectTransform rect)
        {
            rect.GetWorldCorners(_corners);
            return Rect.MinMaxRect(_corners[0].x, _corners[0].y, _corners[2].x, _corners[2].y);
        }

        private void UpdateJoystick()
        {
            _joyBase.gameObject.SetActive(Router.JoystickActive);
            _joyKnob.gameObject.SetActive(Router.JoystickActive);

            if (!Router.JoystickActive) return;

            var scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            var origin = Router.JoystickOrigin;
            var knob = origin + Vector2.ClampMagnitude(Router.JoystickKnob - origin, Router.JoystickRadius);

            _joyBase.rectTransform.position = origin;
            _joyKnob.rectTransform.position = knob;
            _joyBase.rectTransform.sizeDelta = Vector2.one * (Router.JoystickRadius * 2f / scale);
        }

        /// <summary>Круговое меню смены: три слота вокруг кнопки, пока её держат дольше касания.</summary>
        private void UpdateRadial()
        {
            var open = Router.HeldFor(TouchButton.Swap, Time.unscaledTime) > Router.TapSeconds;
            var centre = (Vector2)_buttons[(int)TouchButton.Swap].rectTransform.position;

            for (var i = 0; i < _radial.Length; i++)
            {
                _radial[i].gameObject.SetActive(open);
                if (open) _radial[i].rectTransform.position = centre + RadialOffset(i);
            }
        }

        private Vector2 RadialOffset(int slot)
        {
            var scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
            var angle = (90f + 60f * slot) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (130f * scale);
        }

        // ------------------------------------------------------------------ команды

        public void Read(ref PlayerCommands commands) => Step(ref commands, Time.unscaledDeltaTime, Time.unscaledTime);

        /// <summary>Шаг с явным временем — прогон зовёт его в режиме редактирования, где часов нет.</summary>
        public void Step(ref PlayerCommands commands, float deltaTime, float time)
        {
            _desktop.Read(ref commands);

            if (!Visible && Injected == null) return;

            _touches.Clear();

            if (Injected != null)
            {
                _touches.AddRange(Injected);
            }
            else
            {
                for (var i = 0; i < Input.touchCount; i++)
                {
                    var touch = Input.GetTouch(i);
                    _touches.Add(new TouchPoint(touch.fingerId, touch.phase, touch.position));
                }
            }

            var rig = _ui.Rig;

            // Цель до разбора: «липкость» меняет чувствительность уже этого кадра.
            FindTarget(rig, out var target);
            Router.Sensitivity = HasTarget && TargetAngle < StickyCone ? StickyScale : 1f;
            Router.DegreesPerLookUnit = rig != null ? Mathf.Max(0.01f, rig.LookDegreesPerUnit) : 2.5f;

            Router.Process(_touches, Screen.width, time, ref commands);

            if (Router.LongReleased(TouchButton.Swap, out var at)) commands.WeaponSlot = RadialSlot(at);

            Assist(rig, target, deltaTime, ref commands);

            // На таче курсора нет: «в игре» — это когда на экране управление.
            if (rig != null && !rig.IsLooking && !rig.InputBlocked) rig.SetLooking(true);
        }

        private int RadialSlot(Vector2 releasedAt)
        {
            var centre = (Vector2)_buttons[(int)TouchButton.Swap].rectTransform.position;
            var best = -1;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < _radial.Length; i++)
            {
                var distance = (centre + RadialOffset(i) - releasedAt).sqrMagnitude;
                if (distance >= bestDistance) continue;

                best = i;
                bestDistance = distance;
            }

            // Отпустили на самой кнопке — передумали.
            return (releasedAt - centre).magnitude < RadialOffset(0).magnitude * 0.5f ? -1 : best;
        }

        private void FindTarget(CatacombTestRig rig, out Vector3 target)
        {
            target = default;
            HasTarget = false;
            TargetAngle = 180f;

            var crowd = _ui.Crowd;
            if (rig == null || crowd == null) return;

            var eye = rig.transform.position;
            var forward = rig.transform.forward;

            // Без заливки «сквозь стену» (её цена на радиусе 28 — тысячи клеток в кадр): один луч ниже.
            if (crowd.FindNearest(eye, AssistRange, 1, _found, forward, AssistCone, false, null, false) == 0) return;

            var position = crowd.PositionAt(_found[0]);
            var delta = position - eye;
            var distance = delta.magnitude;

            if (distance < 0.5f) return;
            if (Physics.Raycast(eye, delta / distance, distance - 1f, ~0, QueryTriggerInteraction.Ignore)) return;

            target = position;
            HasTarget = true;
            TargetAngle = Vector3.Angle(forward, delta);
        }

        private void Assist(CatacombTestRig rig, Vector3 target, float deltaTime, ref PlayerCommands commands)
        {
            var wasFiring = AutoFiring;
            AutoFiring = false;

            if (rig == null || !HasTarget || rig.InputBlocked) return;

            var forward = rig.transform.forward;
            var direction = (target - rig.transform.position).normalized;

            // Магнетизм: доля угла за кадр, как пружина, — не рывок к цели, а подтяжка.
            var pull = 1f - Mathf.Exp(-3.5f * deltaTime);
            var perUnit = Mathf.Max(0.01f, rig.LookDegreesPerUnit);

            var flatForward = new Vector3(forward.x, 0f, forward.z);
            var flatTarget = new Vector3(direction.x, 0f, direction.z);
            var yaw = Vector3.SignedAngle(flatForward, flatTarget, Vector3.up);
            var rise = (Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f))) * Mathf.Rad2Deg;

            commands.Look.x += yaw * pull / perUnit;
            commands.Look.y += rise * pull / perUnit * (rig.InvertY ? -1f : 1f);

            var weapon = _ui.Arsenal != null ? _ui.Arsenal.Active : null;
            var autoFire = MetaGame.Instance == null || MetaGame.Instance.Profile.Settings.AutoFire;
            var allowed = weapon != null && weapon.Id != WeaponId.Launcher && weapon.Id != WeaponId.Rail;

            if (!autoFire || !allowed || TargetAngle > AutoFireCone) return;

            AutoFiring = true;
            commands.FireHeld = true;
            if (!wasFiring) commands.FirePressed = true;
        }
    }
}
