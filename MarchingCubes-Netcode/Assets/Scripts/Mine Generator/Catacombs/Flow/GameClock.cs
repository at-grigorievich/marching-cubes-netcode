using System;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Почему время остановлено или замедлено.</summary>
    public enum ClockReason
    {
        /// <summary>Меню паузы.</summary>
        Pause,

        /// <summary>Экран выбора улучшения на уровне: как в VS и Megabonk, время стоит.</summary>
        LevelUp,

        /// <summary>Показ рекламы: время и звук стоят (требование обеих площадок).</summary>
        Ad,

        /// <summary>Окно потеряло фокус: время и звук стоят (Яндекс, п. 1.3). Только в сборке.</summary>
        FocusLost,

        /// <summary>Замедление смерти.</summary>
        Death,

        /// <summary>Стоп-кадр попадания (Ultrakill): десятки миллисекунд, ведётся самими часами.</summary>
        HitStop,

        Count
    }

    /// <summary>
    /// Владелец времени. Единственный, кто пишет <see cref="Time.timeScale"/>.
    ///
    /// Время скоро захотят менять многие: пауза, выбор улучшения, стоп-кадр попадания,
    /// замедление смерти, реклама, потеря фокуса окна. Если каждый пишет timeScale сам,
    /// они затирают друг друга: реклама кончилась и сняла паузу посреди выбора карточки.
    /// Поэтому здесь набор ПРИЧИН, каждая со своим множителем, и итог — минимум по активным.
    ///
    /// Звук останавливается не минимумом, а набором причин (пауза, реклама, фокус): стоп-кадр
    /// и выбор карточки звук не глушат — глухая пауза на долю секунды читается как сбой.
    ///
    /// Класс статический и чистый, чтобы его можно было проверять EditMode-тестами;
    /// стоп-кадр ведёт <see cref="Tick"/>, который зовёт <see cref="GameFlow"/> раз в кадр
    /// по реальному, а не игровому времени.
    /// </summary>
    public static class GameClock
    {
        /// <summary>Стоп-кадр не чаще, чем раз в столько секунд реального времени — иначе
        /// очередь из скорострельной пушки превратила бы игру в слайд-шоу.</summary>
        public const float HitStopCooldown = 0.25f;

        /// <summary>Дольше стоп-кадр не бывает, сколько бы ни попросили.</summary>
        public const float HitStopMax = 0.12f;

        private static readonly bool[] Active = new bool[(int)ClockReason.Count];
        private static readonly float[] Scales = new float[(int)ClockReason.Count];

        private static float _hitStopLeft;
        private static float _hitStopCooldown;

        /// <summary>Применять ли итог к движку. Тесты выключают, чтобы мерить чистую логику.</summary>
        public static bool ApplyToEngine = true;

        /// <summary>Итоговый множитель времени.</summary>
        public static float Scale { get; private set; } = 1f;

        /// <summary>Стоит ли звук.</summary>
        public static bool AudioPaused { get; private set; }

        /// <summary>Итог поменялся.</summary>
        public static event Action Changed;

        public static bool IsHeld(ClockReason reason) => Active[(int)reason];

        /// <summary>Включает причину с множителем времени (0 — стоп).</summary>
        public static void Hold(ClockReason reason, float scale = 0f)
        {
            if (reason == ClockReason.Count) return;

            Active[(int)reason] = true;
            Scales[(int)reason] = Mathf.Clamp01(scale);
            Recompute();
        }

        public static void Release(ClockReason reason)
        {
            if (reason == ClockReason.Count || !Active[(int)reason]) return;

            Active[(int)reason] = false;
            Recompute();
        }

        /// <summary>
        /// Стоп-кадр попадания. Возвращает false, если не время: идёт откат или уже стоит
        /// пауза, под которой стоп-кадр не виден и только продлит её.
        /// </summary>
        public static bool HitStop(float seconds, float scale = 0.05f)
        {
            if (_hitStopCooldown > 0f || seconds <= 0f) return false;
            if (IsHeld(ClockReason.Pause) || IsHeld(ClockReason.Ad) || IsHeld(ClockReason.LevelUp)) return false;

            _hitStopLeft = Mathf.Min(seconds, HitStopMax);
            _hitStopCooldown = HitStopCooldown;

            Hold(ClockReason.HitStop, scale);
            return true;
        }

        /// <summary>Ведёт стоп-кадр. Зовётся раз в кадр с РЕАЛЬНЫМ временем кадра.</summary>
        public static void Tick(float unscaledDeltaTime)
        {
            if (unscaledDeltaTime <= 0f) return;

            _hitStopCooldown = Mathf.Max(0f, _hitStopCooldown - unscaledDeltaTime);

            if (_hitStopLeft <= 0f) return;

            _hitStopLeft -= unscaledDeltaTime;

            if (_hitStopLeft <= 0f) Release(ClockReason.HitStop);
        }

        /// <summary>Снимает все причины — при перезапуске забега и выходе из play-режима.</summary>
        public static void ResetAll()
        {
            Array.Clear(Active, 0, Active.Length);
            _hitStopLeft = 0f;
            _hitStopCooldown = 0f;
            Recompute();
        }

        /// <summary>
        /// Статика переживает выход из play-режима, если перезагрузка домена выключена
        /// (Enter Play Mode Options): без сброса игра стартовала бы с паузой прошлого запуска.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Changed = null;
            ResetAll();
        }

        private static void Recompute()
        {
            var scale = 1f;
            var audio = false;

            for (var i = 0; i < Active.Length; i++)
            {
                if (!Active[i]) continue;

                scale = Mathf.Min(scale, Scales[i]);

                var reason = (ClockReason)i;
                if (reason == ClockReason.Pause || reason == ClockReason.Ad || reason == ClockReason.FocusLost)
                {
                    audio = true;
                }
            }

            var changed = !Mathf.Approximately(scale, Scale) || audio != AudioPaused;

            Scale = scale;
            AudioPaused = audio;

            if (ApplyToEngine)
            {
                Time.timeScale = scale;
                AudioListener.pause = audio;
            }

            if (changed) Changed?.Invoke();
        }
    }
}
