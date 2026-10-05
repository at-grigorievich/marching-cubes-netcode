using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Разбор касаний (M5.5): два пальца одновременно, палец закреплён за зоной, мёртвая зона
    /// джойстика, касание и удержание смены, взгляд в дюймах — одинаковый на телефоне и планшете.
    /// </summary>
    public sealed class TouchTests
    {
        private const float Width = 1920f;

        private static TouchRouter Router()
        {
            var router = new TouchRouter { JoystickRadius = 100f, Dpi = 400f, DegreesPerInch = 70f, DegreesPerLookUnit = 2.5f };

            router.Buttons[(int)TouchButton.Fire] = new Rect(1700f, 100f, 150f, 150f);
            router.Visible[(int)TouchButton.Fire] = true;
            router.Buttons[(int)TouchButton.Swap] = new Rect(1500f, 250f, 100f, 100f);
            router.Visible[(int)TouchButton.Swap] = true;
            router.Buttons[(int)TouchButton.Interact] = new Rect(1500f, 400f, 100f, 100f);
            router.Visible[(int)TouchButton.Interact] = false;

            return router;
        }

        private static PlayerCommands Step(TouchRouter router, float time, params TouchPoint[] touches)
        {
            var commands = PlayerCommands.Empty;
            router.Process(new List<TouchPoint>(touches), Width, time, ref commands);
            return commands;
        }

        private static TouchPoint T(int id, TouchPhase phase, float x, float y) => new TouchPoint(id, phase, new Vector2(x, y));

        [Test]
        public void MoveAndFireWithTwoFingers()
        {
            var router = Router();

            Step(router, 0f, T(1, TouchPhase.Began, 300f, 300f), T(2, TouchPhase.Began, 1770f, 170f));
            var c = Step(router, 0.1f, T(1, TouchPhase.Moved, 300f, 400f), T(2, TouchPhase.Stationary, 1770f, 170f));

            Assert.AreEqual(1f, c.Move.y, 1e-3f, "джойстик вверх до края — полный ход");
            Assert.IsTrue(c.FireHeld, "огонь держится вторым пальцем");
            Assert.IsFalse(c.FirePressed, "нажатие — только в кадре касания");
        }

        [Test]
        public void FingerStaysInItsZone()
        {
            var router = Router();

            // Джойстик утянут на правую половину — остаётся джойстиком, взгляд не крутится.
            Step(router, 0f, T(1, TouchPhase.Began, 800f, 300f));
            var c = Step(router, 0.1f, T(1, TouchPhase.Moved, 1100f, 300f));

            Assert.AreEqual(1f, c.Move.x, 1e-3f);
            Assert.AreEqual(Vector2.zero, c.Look);

            // Палец огня, съехавший с кнопки, — по-прежнему огонь.
            Step(router, 0.2f, T(2, TouchPhase.Began, 1770f, 170f));
            c = Step(router, 0.3f, T(2, TouchPhase.Moved, 1400f, 600f));
            Assert.IsTrue(c.FireHeld);
            Assert.AreEqual(Vector2.zero, c.Look);
        }

        [Test]
        public void JoystickDeadZone()
        {
            var router = Router();

            Step(router, 0f, T(1, TouchPhase.Began, 300f, 300f));
            var c = Step(router, 0.1f, T(1, TouchPhase.Moved, 305f, 304f));

            Assert.AreEqual(Vector2.zero, c.Move, "дрожь пальца на месте не двигает");
        }

        [Test]
        public void LookIsMeasuredInInches()
        {
            var phone = Router();
            phone.Dpi = 400f;
            var tablet = Router();
            tablet.Dpi = 200f;

            // Один и тот же дюйм хода пальца — один и тот же поворот.
            var a = phone.LookUnits(new Vector2(40f, 0f));
            var b = tablet.LookUnits(new Vector2(20f, 0f));
            Assert.AreEqual(a.x, b.x, 1e-4f);

            // 0.1 дюйма × 70°/дюйм = 7°, ускорение на такой скорости ×1.12, в единицах оси — делённое на 2.5.
            Assert.AreEqual(7f * 1.12f / 2.5f, phone.LookUnits(new Vector2(40f, 0f)).x, 1e-3f);
        }

        [Test]
        public void SwapTapCyclesHoldPicksSlot()
        {
            var router = Router();

            Step(router, 0f, T(3, TouchPhase.Began, 1550f, 300f));
            var c = Step(router, 0.2f, T(3, TouchPhase.Ended, 1550f, 300f));
            Assert.AreEqual(1, c.WeaponCycle, "короткое касание — следующая пушка");

            Step(router, 1f, T(4, TouchPhase.Began, 1550f, 300f));
            Assert.Greater(router.HeldFor(TouchButton.Swap, 1.6f), router.TapSeconds);

            c = Step(router, 1.6f, T(4, TouchPhase.Ended, 1450f, 420f));
            Assert.AreEqual(0, c.WeaponCycle, "удержание — не следующая");
            Assert.IsTrue(router.LongReleased(TouchButton.Swap, out var at));
            Assert.AreEqual(new Vector2(1450f, 420f), at, "меню выбирает по точке, где отпустили");
        }

        [Test]
        public void HiddenButtonIsNotPressed()
        {
            var router = Router();

            // «Действие» скрыто (не у логова): касание там — взгляд, а не кнопка.
            var c = Step(router, 0f, T(5, TouchPhase.Began, 1550f, 450f));
            Assert.IsFalse(c.Interact);

            c = Step(router, 0.1f, T(5, TouchPhase.Moved, 1600f, 450f));
            Assert.Greater(c.Look.x, 0f);
        }
    }
}
