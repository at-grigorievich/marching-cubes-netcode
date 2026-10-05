using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Кнопки тач-управления (M5.5).</summary>
    public enum TouchButton
    {
        Fire,
        Alt,
        Dash,
        Swap,
        Pause,
        Interact,
        Count
    }

    /// <summary>Касание кадра — то, что нужно разбору, без <c>UnityEngine.Touch</c> (его не построить в тесте).</summary>
    public struct TouchPoint
    {
        public int Id;
        public TouchPhase Phase;
        public Vector2 Position;

        public TouchPoint(int id, TouchPhase phase, Vector2 position)
        {
            Id = id;
            Phase = phase;
            Position = position;
        }
    }

    /// <summary>
    /// Разбор касаний по зонам (M5.5): левая половина — плавающий джойстик под пальцем, правая —
    /// взгляд перетаскиванием, кнопки — по своим прямоугольникам. Чистая логика: касания и экран
    /// приходят параметрами, тесты — вне Unity.
    ///
    /// **Палец закрепляется за зоной в момент касания и не перескакивает**: джойстик, утянутый
    /// на правую половину, остаётся джойстиком, а палец огня, съехавший с кнопки, — огнём. Иначе
    /// двумя пальцами (ход и огонь, ход и взгляд) играть нельзя — главное требование тача.
    /// </summary>
    public sealed class TouchRouter
    {
        private enum Zone
        {
            Joystick,
            Look,
            Button
        }

        private struct Owner
        {
            public Zone Zone;
            public TouchButton Button;
            public Vector2 Last;
            public float Began;
        }

        /// <summary>Прямоугольники кнопок в пикселях экрана (начало — левый нижний угол, как у касаний).</summary>
        public readonly Rect[] Buttons = new Rect[(int)TouchButton.Count];

        /// <summary>Видима ли кнопка: «действие» появляется только у логова и провала.</summary>
        public readonly bool[] Visible = new bool[(int)TouchButton.Count];

        /// <summary>Радиус джойстика в пикселях — отклонение до края даёт полный ход.</summary>
        public float JoystickRadius = 120f;

        /// <summary>Точек на дюйм: взгляд меряется в дюймах, чтобы телефон и планшет крутили одинаково.</summary>
        public float Dpi = 160f;

        /// <summary>Градусов поворота на дюйм хода пальца при чувствительности 1.</summary>
        public float DegreesPerInch = 70f;

        /// <summary>Градусов на единицу оси мыши у стенда — чтобы чувствительность у игрока была одна.</summary>
        public float DegreesPerLookUnit = 2.5f;

        /// <summary>Множитель чувствительности из настроек (и «липкость» над целью).</summary>
        public float Sensitivity = 1f;

        /// <summary>Касание кнопки «смена» короче этого — следующая пушка.</summary>
        public float TapSeconds = 0.35f;

        public bool JoystickActive { get; private set; }
        public Vector2 JoystickOrigin { get; private set; }
        public Vector2 JoystickKnob { get; private set; }

        /// <summary>Пальцы на экране — пока они есть, мышь стенду не мешает.</summary>
        public int ActiveTouches => _owners.Count;

        private readonly Dictionary<int, Owner> _owners = new Dictionary<int, Owner>();
        private readonly bool[] _held = new bool[(int)TouchButton.Count];
        private readonly bool[] _pressed = new bool[(int)TouchButton.Count];
        private readonly bool[] _tapped = new bool[(int)TouchButton.Count];

        public bool Held(TouchButton button) => _held[(int)button];
        public bool Pressed(TouchButton button) => _pressed[(int)button];

        /// <summary>Касание кнопки, отпущенное быстрее <see cref="TapSeconds"/>.</summary>
        public bool Tapped(TouchButton button) => _tapped[(int)button];

        /// <summary>Сколько секунд держится кнопка (0 — не держится).</summary>
        public float HeldFor(TouchButton button, float time)
        {
            foreach (var pair in _owners)
            {
                if (pair.Value.Zone == Zone.Button && pair.Value.Button == button) return time - pair.Value.Began;
            }

            return 0f;
        }

        /// <summary>Где сейчас палец, держащий кнопку (для кругового меню смены).</summary>
        public bool TryFinger(TouchButton button, out Vector2 position)
        {
            foreach (var pair in _owners)
            {
                if (pair.Value.Zone != Zone.Button || pair.Value.Button != button) continue;

                position = pair.Value.Last;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>
        /// Кнопку смены держали дольше касания и отпустили в этом кадре: где отпустили. Круговое меню
        /// из трёх слотов (M5.5) выбирает слот по этой точке.
        /// </summary>
        public bool LongReleased(TouchButton button, out Vector2 position)
        {
            position = _longReleaseAt;
            return _longRelease == (int)button;
        }

        private int _longRelease = -1;
        private Vector2 _longReleaseAt;

        /// <summary>Разобрать касания кадра и дописать команды. Поля, которых тач не знает, не трогает.</summary>
        public void Process(IReadOnlyList<TouchPoint> touches, float screenWidth, float time, ref PlayerCommands commands)
        {
            for (var i = 0; i < _pressed.Length; i++)
            {
                _pressed[i] = false;
                _tapped[i] = false;
            }

            _longRelease = -1;

            var move = Vector2.zero;
            var look = Vector2.zero;

            for (var i = 0; i < touches.Count; i++)
            {
                var touch = touches[i];

                if (touch.Phase == TouchPhase.Began)
                {
                    Begin(touch, screenWidth, time);
                    continue;
                }

                if (!_owners.TryGetValue(touch.Id, out var owner)) continue;

                if (touch.Phase == TouchPhase.Ended || touch.Phase == TouchPhase.Canceled)
                {
                    owner.Last = touch.Position;
                    End(touch.Id, owner, time);
                    continue;
                }

                switch (owner.Zone)
                {
                    case Zone.Joystick:
                        JoystickKnob = touch.Position;
                        break;

                    case Zone.Look:
                        look += touch.Position - owner.Last;
                        owner.Last = touch.Position;
                        _owners[touch.Id] = owner;
                        break;

                    case Zone.Button:
                        owner.Last = touch.Position;
                        _owners[touch.Id] = owner;
                        break;
                }
            }

            // Кнопки держатся, пока на них лежит палец, закреплённый за ними.
            for (var i = 0; i < _held.Length; i++) _held[i] = false;

            foreach (var pair in _owners)
            {
                if (pair.Value.Zone == Zone.Button) _held[(int)pair.Value.Button] = true;
            }

            if (JoystickActive)
            {
                var offset = JoystickKnob - JoystickOrigin;
                move = Vector2.ClampMagnitude(offset / Mathf.Max(1f, JoystickRadius), 1f);

                // Мёртвая зона: дрожь пальца на месте не должна двигать игрока.
                if (move.magnitude < 0.12f) move = Vector2.zero;
            }

            commands.Move += move;
            commands.Move = Vector2.ClampMagnitude(commands.Move, 1f);
            commands.Look += LookUnits(look);

            commands.FireHeld |= Held(TouchButton.Fire);
            commands.FirePressed |= Pressed(TouchButton.Fire);
            commands.AltHeld |= Held(TouchButton.Alt);
            commands.AltPressed |= Pressed(TouchButton.Alt);
            commands.Dash |= Pressed(TouchButton.Dash);
            commands.Pause |= Pressed(TouchButton.Pause);
            commands.Interact |= Pressed(TouchButton.Interact);

            if (Tapped(TouchButton.Swap)) commands.WeaponCycle = 1;
        }

        /// <summary>
        /// Пиксели пальца → единицы оси мыши: дюймы хода × градусы на дюйм / градусы на единицу,
        /// с лёгким ускорением на быстром движении (резкий разворот к пауку за спиной одним мазком).
        /// </summary>
        public Vector2 LookUnits(Vector2 pixels)
        {
            if (pixels == Vector2.zero) return Vector2.zero;

            var inches = pixels / Mathf.Max(1f, Dpi);
            var speed = inches.magnitude;
            var acceleration = 1f + 0.6f * Mathf.Clamp01((speed - 0.05f) / 0.25f);

            return inches * (DegreesPerInch * Sensitivity * acceleration / Mathf.Max(0.01f, DegreesPerLookUnit));
        }

        /// <summary>Снять все касания — смена экрана, потеря фокуса.</summary>
        public void Reset()
        {
            _owners.Clear();
            JoystickActive = false;

            for (var i = 0; i < _held.Length; i++) _held[i] = false;
        }

        private void Begin(TouchPoint touch, float screenWidth, float time)
        {
            for (var b = 0; b < Buttons.Length; b++)
            {
                if (!Visible[b] || !Buttons[b].Contains(touch.Position)) continue;

                _owners[touch.Id] = new Owner { Zone = Zone.Button, Button = (TouchButton)b, Last = touch.Position, Began = time };
                _pressed[b] = true;
                return;
            }

            if (touch.Position.x < screenWidth * 0.5f)
            {
                // Второй палец слева при занятом джойстике — не перехватывает его.
                if (JoystickActive) return;

                JoystickActive = true;
                JoystickOrigin = touch.Position;
                JoystickKnob = touch.Position;
                _owners[touch.Id] = new Owner { Zone = Zone.Joystick, Last = touch.Position, Began = time };
                return;
            }

            _owners[touch.Id] = new Owner { Zone = Zone.Look, Last = touch.Position, Began = time };
        }

        private void End(int id, Owner owner, float time)
        {
            _owners.Remove(id);

            if (owner.Zone == Zone.Joystick) JoystickActive = false;
            if (owner.Zone != Zone.Button) return;

            if (time - owner.Began <= TapSeconds)
            {
                _tapped[(int)owner.Button] = true;
            }
            else
            {
                _longRelease = (int)owner.Button;
                _longReleaseAt = owner.Last;
            }
        }
    }
}
