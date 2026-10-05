using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Звуки игры. Порядок — номер клипа в таблице.</summary>
    public enum SoundId
    {
        LauncherFire,
        Explosion,
        RevolverFire,
        CoinFlip,
        CoinPing,
        ShotgunFire,
        ShellLoad,
        NailFire,
        Overheat,
        RailCharge,
        RailFire,
        FlameLoop,
        FlameBurst,
        TeslaLoop,
        Zap,
        Swing,
        Crunch,
        Slam,
        Parry,
        Reload,
        Empty,
        HitTick,
        KillTick,
        Pickup,
        LevelUp,
        Horn,
        Hurt,
        Dash,
        UiClick,

        // Метки особых пауков (M3.2): их слышно раньше, чем видно — главный урок Vermintide 2.
        SpecialAppear,
        SpitterGurgle,
        LurkerShriek,
        WeaverTwang,
        BomberSwell,
        AcidSplash,
        Count
    }

    /// <summary>
    /// Звук оружия и интерфейса. Пока звукового пака нет — заглушки, синтезированные кодом
    /// (щелчок, шум с огибающей, тон со спадом): тракт, приоритеты и ограничение голосов
    /// готовы, пак встанет заменой клипов (см. Docs/Plan/M2-weapons.md, «Звук»).
    ///
    /// Пул из 16 источников. Приоритет: игрок > Матка > особые > толпа — при нехватке
    /// вытесняется самый тихий из низших. Одинаковых звуков одновременно не больше лимита
    /// (гвоздей — шести): иначе очередь из гвоздомёта забивает весь пул.
    ///
    /// Высота ±6% на каждый выстрел: одинаковый звук подряд слышен как пулемётная запись.
    /// </summary>
    public sealed class WeaponAudio : MonoBehaviour
    {
        private const int Voices = 16;
        private const int Rate = 22050;

        private sealed class Voice
        {
            public AudioSource Source;
            public SoundId Id;
            public int Priority;
            public float Started;
        }

        private readonly Voice[] _voices = new Voice[Voices];
        private readonly Dictionary<SoundId, AudioSource> _loops = new Dictionary<SoundId, AudioSource>();
        private AudioClip[] _clips;

        private static readonly int[] Limits = BuildLimits();

        public float Volume = 0.8f;

        /// <summary>Звук выключен (прогоны, тихий режим).</summary>
        public bool Muted;

        private static int[] BuildLimits()
        {
            var limits = new int[(int)SoundId.Count];
            for (var i = 0; i < limits.Length; i++) limits[i] = 3;

            limits[(int)SoundId.NailFire] = 6;
            limits[(int)SoundId.HitTick] = 2;
            limits[(int)SoundId.Pickup] = 4;
            limits[(int)SoundId.Zap] = 3;
            limits[(int)SoundId.Explosion] = 4;

            return limits;
        }

        public void Build()
        {
            _clips = new AudioClip[(int)SoundId.Count];

            for (var i = 0; i < _clips.Length; i++) _clips[i] = Synth((SoundId)i);

            for (var i = 0; i < Voices; i++)
            {
                var go = new GameObject("Voice " + i) { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);

                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;

                _voices[i] = new Voice { Source = source, Priority = -1 };
            }
        }

        public AudioClip Clip(SoundId id) => _clips != null ? _clips[(int)id] : null;

        /// <summary>Сыграть. <paramref name="at"/> — точка в мире (взрыв вдали), иначе — у уха.</summary>
        public void Play(SoundId id, float volume = 1f, float pitch = 1f, Vector3? at = null, int priority = 3)
        {
            if (Muted || _clips == null || !Application.isPlaying) return;

            var playing = 0;
            Voice free = null;
            Voice weakest = null;

            foreach (var voice in _voices)
            {
                var busy = voice.Source.isPlaying;

                if (busy && voice.Id == id) playing++;

                if (!busy)
                {
                    free ??= voice;
                    continue;
                }

                if (voice.Priority > priority) continue;
                if (weakest == null || voice.Priority < weakest.Priority ||
                    (voice.Priority == weakest.Priority && voice.Started < weakest.Started))
                {
                    weakest = voice;
                }
            }

            if (playing >= Limits[(int)id]) return;

            var chosen = free ?? weakest;
            if (chosen == null) return;

            var source = chosen.Source;

            source.Stop();
            source.clip = _clips[(int)id];
            source.volume = Volume * volume;
            source.pitch = pitch * (1f + UnityEngine.Random.Range(-0.06f, 0.06f));

            if (at.HasValue)
            {
                source.transform.position = at.Value;
                source.spatialBlend = 0.6f;
                source.minDistance = 4f;
                source.maxDistance = 60f;
            }
            else
            {
                source.transform.localPosition = Vector3.zero;
                source.spatialBlend = 0f;
            }

            source.Play();

            chosen.Id = id;
            chosen.Priority = priority;
            chosen.Started = Time.unscaledTime;
        }

        /// <summary>Петля (рёв огнемёта, вой заряда, треск теслы): включить, выключить, высота.</summary>
        public void Loop(SoundId id, bool on, float volume = 1f, float pitch = 1f)
        {
            if (_clips == null || !Application.isPlaying) return;

            if (!_loops.TryGetValue(id, out var source))
            {
                if (!on) return;

                var go = new GameObject("Loop " + id) { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);

                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.spatialBlend = 0f;
                source.clip = _clips[(int)id];

                _loops[id] = source;
            }

            if (!on || Muted)
            {
                if (source.isPlaying) source.Stop();
                return;
            }

            source.volume = Volume * volume;
            source.pitch = pitch;

            if (!source.isPlaying) source.Play();
        }

        public void StopLoops()
        {
            foreach (var loop in _loops.Values)
            {
                if (loop != null) loop.Stop();
            }
        }

        private void OnDestroy()
        {
            if (_clips == null) return;

            foreach (var clip in _clips)
            {
                if (clip != null) Destroy(clip);
            }
        }

        // ------------------------------------------------------------------ синтез

        private static AudioClip Synth(SoundId id)
        {
            var random = new System.Random((int)id * 7919 + 13);

            float[] data = id switch
            {
                SoundId.LauncherFire => Mix(Tone(0.28f, 130f, 45f, 18f), Noise(random, 0.08f, 40f, 0.35f)),
                SoundId.Explosion => Mix(LowNoise(random, 1.3f, 3f, 0.08f, 1f), Tone(0.6f, 70f, 30f, 6f, 0.7f)),
                SoundId.RevolverFire => Mix(Noise(random, 0.16f, 28f, 0.9f), Tone(0.08f, 1400f, 300f, 40f, 0.4f)),
                SoundId.CoinFlip => Tone(0.18f, 1800f, 2600f, 14f, 0.35f),
                SoundId.CoinPing => Mix(Tone(0.55f, 2400f, 2380f, 6f, 0.6f), Tone(0.55f, 3610f, 3600f, 9f, 0.25f)),
                SoundId.ShotgunFire => Mix(LowNoise(random, 0.45f, 9f, 0.25f, 1f), Tone(0.3f, 90f, 40f, 10f)),
                SoundId.ShellLoad => Mix(Noise(random, 0.05f, 80f, 0.5f), Tone(0.06f, 500f, 420f, 60f, 0.3f)),
                SoundId.NailFire => Noise(random, 0.05f, 90f, 0.35f),
                SoundId.Overheat => Mix(Noise(random, 0.9f, 3f, 0.25f), Tone(0.9f, 900f, 300f, 2f, 0.2f)),
                SoundId.RailCharge => Loopable(Mix(Tone(1f, 320f, 320f, 0f, 0.4f), Tone(1f, 960f, 960f, 0f, 0.15f))),
                SoundId.RailFire => Mix(Noise(random, 0.7f, 6f, 0.7f), Tone(0.7f, 2400f, 160f, 5f, 0.8f)),
                SoundId.FlameLoop => Loopable(LowNoise(random, 1f, 0f, 0.15f, 0.6f)),
                SoundId.FlameBurst => LowNoise(random, 0.7f, 4f, 0.1f, 1f),
                SoundId.TeslaLoop => Loopable(Mix(Buzz(1f, 120f, 0.25f), Crackle(random, 1f, 0.3f))),
                SoundId.Zap => Mix(Crackle(random, 0.16f, 0.8f), Tone(0.12f, 2800f, 1800f, 20f, 0.3f)),
                SoundId.Swing => Whoosh(random, 0.28f),
                SoundId.Crunch => Mix(LowNoise(random, 0.2f, 18f, 0.3f, 1f), Tone(0.12f, 160f, 60f, 25f, 0.6f)),
                SoundId.Slam => Mix(LowNoise(random, 0.8f, 5f, 0.1f, 1f), Tone(0.5f, 60f, 30f, 6f, 0.9f)),
                SoundId.Parry => Mix(Tone(0.4f, 1600f, 1580f, 8f, 0.6f), Noise(random, 0.05f, 60f, 0.4f)),
                SoundId.Reload => Mix(Noise(random, 0.06f, 70f, 0.5f), Tone(0.08f, 700f, 500f, 40f, 0.25f)),
                SoundId.Empty => Noise(random, 0.03f, 120f, 0.3f),
                SoundId.HitTick => Tone(0.035f, 2200f, 2000f, 90f, 0.35f),
                SoundId.KillTick => Mix(Tone(0.05f, 1600f, 1500f, 60f, 0.4f), Delay(Tone(0.05f, 2400f, 2300f, 60f, 0.35f), 0.05f)),
                SoundId.Pickup => Tone(0.09f, 620f, 940f, 30f, 0.25f),
                SoundId.LevelUp => Mix(Tone(0.5f, 523f, 523f, 5f, 0.3f), Delay(Tone(0.45f, 659f, 659f, 5f, 0.3f), 0.08f),
                    Delay(Tone(0.4f, 784f, 784f, 5f, 0.3f), 0.16f)),
                SoundId.Horn => Horn(2.6f),
                SoundId.Hurt => Mix(LowNoise(random, 0.14f, 25f, 0.3f, 0.7f), Tone(0.12f, 140f, 80f, 25f, 0.5f)),
                SoundId.Dash => Whoosh(random, 0.2f),
                SoundId.UiClick => Tone(0.03f, 1200f, 1100f, 80f, 0.25f),

                // Особые: каждый — своим тембром, узнаваемым на фоне шороха толпы.
                SoundId.SpecialAppear => Mix(Tone(0.9f, 180f, 120f, 3f, 0.35f), Delay(Tone(0.7f, 240f, 150f, 4f, 0.25f), 0.2f)),
                SoundId.SpitterGurgle => Mix(LowNoise(random, 0.8f, 3f, 0.05f, 0.6f), Buzz(0.8f, 55f, 0.25f)),
                SoundId.LurkerShriek => Mix(Tone(1f, 1900f, 3200f, 2f, 0.45f), Tone(1f, 2850f, 4100f, 2.5f, 0.25f),
                    Noise(random, 1f, 2f, 0.12f)),
                SoundId.WeaverTwang => Mix(Tone(0.7f, 330f, 300f, 5f, 0.5f), Tone(0.7f, 990f, 905f, 7f, 0.2f)),
                SoundId.BomberSwell => Mix(Tone(1f, 60f, 140f, 0.5f, 0.6f), LowNoise(random, 1f, 0.5f, 0.04f, 0.4f)),
                SoundId.AcidSplash => Mix(Noise(random, 0.35f, 10f, 0.5f), LowNoise(random, 0.4f, 8f, 0.2f, 0.4f)),
                _ => new float[64]
            };

            var clip = AudioClip.Create(id.ToString(), data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontSave;

            return clip;
        }

        /// <summary>Тон со сдвигом высоты и экспоненциальным спадом.</summary>
        private static float[] Tone(float seconds, float from, float to, float decay, float gain = 1f)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];
            var phase = 0.0;

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)Rate;
                var k = t / seconds;
                var frequency = Mathf.Lerp(from, to, k);

                phase += 2.0 * Math.PI * frequency / Rate;

                var attack = Mathf.Clamp01(t / 0.004f);
                data[i] = (float)Math.Sin(phase) * Mathf.Exp(-t * decay) * attack * gain * (1f - k * 0.2f);
            }

            return data;
        }

        /// <summary>Белый шум со спадом и простым высоким срезом (разность соседних).</summary>
        private static float[] Noise(System.Random random, float seconds, float decay, float gain)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];
            var previous = 0f;

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)Rate;
                var white = (float)random.NextDouble() * 2f - 1f;

                data[i] = (white - previous * 0.6f) * Mathf.Exp(-t * decay) * gain;
                previous = white;
            }

            return data;
        }

        /// <summary>Низкий шум: однополюсный фильтр — гул, рёв, хруст.</summary>
        private static float[] LowNoise(System.Random random, float seconds, float decay, float smooth, float gain)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];
            var state = 0f;

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)Rate;
                var white = (float)random.NextDouble() * 2f - 1f;

                state += (white - state) * smooth;

                var attack = Mathf.Clamp01(t / 0.01f);
                data[i] = state * 3f * Mathf.Exp(-t * decay) * attack * gain;
            }

            return data;
        }

        private static float[] Buzz(float seconds, float frequency, float gain)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];

            for (var i = 0; i < count; i++)
            {
                var phase = i * frequency / Rate;
                data[i] = (phase - Mathf.Floor(phase) - 0.5f) * 2f * gain;
            }

            return data;
        }

        private static float[] Crackle(System.Random random, float seconds, float gain)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];

            for (var i = 0; i < count; i++)
            {
                if (random.NextDouble() > 0.004) continue;

                var length = random.Next(20, 120);
                var amplitude = (float)random.NextDouble() * gain;

                for (var k = 0; k < length && i + k < count; k++)
                {
                    data[i + k] += ((float)random.NextDouble() * 2f - 1f) * amplitude * (1f - k / (float)length);
                }
            }

            return data;
        }

        private static float[] Whoosh(System.Random random, float seconds)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];
            var state = 0f;

            for (var i = 0; i < count; i++)
            {
                var k = i / (float)count;
                var white = (float)random.NextDouble() * 2f - 1f;

                // Фильтр раскрывается и закрывается — взмах.
                var smooth = Mathf.Lerp(0.05f, 0.5f, Mathf.Sin(k * Mathf.PI));
                state += (white - state) * smooth;

                data[i] = state * Mathf.Sin(k * Mathf.PI) * 0.8f;
            }

            return data;
        }

        /// <summary>Рог орды: низкий пилообразный гул с нарастанием — «своды дрожат».</summary>
        private static float[] Horn(float seconds)
        {
            var count = (int)(seconds * Rate);
            var data = new float[count];

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)Rate;
                var k = t / seconds;

                var swell = Mathf.Sin(Mathf.Clamp01(k * 1.4f) * Mathf.PI * 0.5f) * Mathf.Clamp01((1f - k) * 4f);
                var f = 52f + 4f * Mathf.Sin(t * 3f);

                var saw = 0f;
                for (var h = 1; h <= 6; h++) saw += Mathf.Sin(2f * Mathf.PI * f * h * t) / h;

                data[i] = saw * 0.35f * swell;
            }

            return data;
        }

        private static float[] Delay(float[] data, float seconds)
        {
            var offset = (int)(seconds * Rate);
            var result = new float[data.Length + offset];

            Array.Copy(data, 0, result, offset, data.Length);
            return result;
        }

        private static float[] Mix(params float[][] parts)
        {
            var length = 0;
            foreach (var part in parts) length = Mathf.Max(length, part.Length);

            var data = new float[length];

            foreach (var part in parts)
            {
                for (var i = 0; i < part.Length; i++) data[i] += part[i];
            }

            for (var i = 0; i < length; i++) data[i] = Mathf.Clamp(data[i], -1f, 1f);

            return data;
        }

        /// <summary>Петля без щелчка на стыке: края сведены к нулю короткими полками.</summary>
        private static float[] Loopable(float[] data)
        {
            var fade = Mathf.Min(data.Length / 4, Rate / 50);

            for (var i = 0; i < fade; i++)
            {
                var k = i / (float)fade;

                data[i] *= k;
                data[data.Length - 1 - i] *= k;
            }

            return data;
        }
    }
}
