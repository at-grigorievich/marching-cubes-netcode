using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Напалм» — огнемёт. Роль — отказ в территории: отходишь — за тобой горит пол, толпа бежит
    /// через огонь.
    ///
    /// Огонь: конус 22° на 7 юнитов, 10 ударов в секунду по 9 HP плюс горение 3 с по 12 HP/с.
    /// Горение перекидывается на соседей (толпа, SpiderCrowd.SpreadFire) — горящие бегут дальше
    /// в толпу и разносят огонь. Топливо 100, −20 в секунду, +15 без стрельбы.
    ///
    /// Огонь на полу: раз в 0.25 с стрельбы — пятно на камне по прицелу (радиус 1.6, живёт 5 с,
    /// поджигает пересекающих, до 24 пятен). Пятно оставляет гарь шейдером породы — отдельной
    /// системы не нужно.
    ///
    /// Альт «Выдох» (ур. 3, 40 топлива, откат 6 с): кольцо огня вокруг игрока радиусом 4.5,
    /// отброс 12 и поджог. Кнопка «меня окружили».
    ///
    /// Эволюция «Пекло» (ур. 5 + Сила 3): сгоревшие лопаются огненным облачком.
    /// </summary>
    public sealed class FlamerWeapon : Weapon
    {
        private const float PatchRadius = 1.6f;
        private const float PatchInterval = 0.25f;
        private const float ExhaleFuel = 40f;

        private struct Patch
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float Left;
            public float Tick;
        }

        private float _fuel;
        private float _nextTick;
        private float _patchTimer;
        private float _exhaleCooldown;
        private bool _firing;
        private float _lightTimer;

        private readonly List<Patch> _patches = new List<Patch>();
        private SpiderCrowd _boundCrowd;
        private int _popsThisFrame;

        public int PatchesLaid { get; private set; }
        public int ActivePatches => _patches.Count;

        protected override void Build(Transform view)
        {
            _fuel = N.Magazine;

            var m = Ctx.Materials;

            var metal = m.Lit("Flamer Metal", new Color(0.3f, 0.28f, 0.25f), 0.5f, 0.45f);
            var tank = m.Lit("Flamer Tank", new Color(0.45f, 0.12f, 0.06f), 0.4f, 0.4f);
            var dark = m.Lit("Flamer Dark", new Color(0.03f, 0.03f, 0.03f), 0.3f, 0.2f);
            var pilot = m.Lit("Flamer Pilot", new Color(0.2f, 0.25f, 0.6f), 0f, 0.4f, new Color(0.3f, 0.5f, 2f));

            m.Part(view, "Body", m.Own(GunModels.Flamer()), metal, tank, dark, pilot);

            Motion.Offset = new Vector3(0.2f, -0.18f, 0.24f);

            if (Ctx.Crowd != null)
            {
                Ctx.Crowd.Killed += OnKilled;
                _boundCrowd = Ctx.Crowd;
            }
        }

        public override void Dispose()
        {
            if (_boundCrowd != null) _boundCrowd.Killed -= OnKilled;
            Ctx.Audio?.Loop(SoundId.FlameLoop, false);

            base.Dispose();
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && _exhaleCooldown <= 0f && _fuel >= ExhaleFuel) Exhale();

            _firing = commands.FireHeld && _fuel > 0f;

            if (!_firing)
            {
                Ctx.Audio?.Loop(SoundId.FlameLoop, false);
                return;
            }

            _fuel = Mathf.Max(0f, _fuel - N[WeaponStat.Drain] * deltaTime);

            Ctx.Audio?.Loop(SoundId.FlameLoop, true, 0.55f, 1f);

            Spray(deltaTime);

            var interval = 1f / N.Rate;

            while (Ctx.Time >= _nextTick)
            {
                _nextTick = Mathf.Max(_nextTick + interval, Ctx.Time - interval);
                Burn();
            }

            _patchTimer -= deltaTime;

            if (_patchTimer <= 0f)
            {
                _patchTimer = PatchInterval;
                LayPatch();
            }
        }

        public override void Holster()
        {
            base.Holster();

            _firing = false;
            Ctx.Audio?.Loop(SoundId.FlameLoop, false);
        }

        public override void TickBackground(float deltaTime)
        {
            _exhaleCooldown = Cooldown(_exhaleCooldown, deltaTime);
            _popsThisFrame = 0;

            if (!_firing || !IsEquipped) _fuel = Mathf.Min(N.Magazine, _fuel + N[WeaponStat.Refill] * deltaTime);

            UpdatePatches(deltaTime);

            if (!IsEquipped) _firing = false;
        }

        private void Burn()
        {
            var eye = Eye;

            var spec = HitSpec.Cone(eye.position, eye.forward, N.Range, N.Area, N.Damage, Source, HitKind.Fire);
            spec.FullShare = 0.6f;
            spec.MinShare = 0.5f;
            spec.BurnSeconds = N[WeaponStat.Extra1];
            spec.BurnDps = N[WeaponStat.Status];
            spec.Knockback = 0.6f;

            var report = Strike(spec, false);

            if (report.Killed > 0) Ctx.Feedback.Mark(report);
        }

        /// <summary>Пламя частицами — клубы вдоль конуса; свет — один источник из пула, мерцает.</summary>
        private void Spray(float deltaTime)
        {
            if (Ctx.Explosions == null) return;

            var eye = Eye;
            var muzzle = View.TransformPoint(GunModels.FlamerMuzzle);
            var tan = Mathf.Tan(N.Area * Mathf.Deg2Rad);
            var count = Mathf.Max(1, Mathf.RoundToInt(60f * deltaTime * CaveExplosions.Density));

            for (var i = 0; i < count; i++)
            {
                var spread = Random.insideUnitCircle * tan;
                var direction = (eye.forward + eye.right * spread.x + eye.up * spread.y).normalized;
                var speed = N.Range * Random.Range(1.6f, 2.4f);

                Ctx.Explosions.Flame(muzzle + direction * 0.1f, direction * speed, Random.Range(0.4f, 0.9f),
                    Random.Range(0.3f, 0.45f));
            }

            _lightTimer -= deltaTime;

            if (_lightTimer <= 0f)
            {
                _lightTimer = 0.12f;
                Ctx.Explosions.Light(muzzle + eye.forward * 2f, Random.Range(14f, 22f), 9f);
            }
        }

        // ------------------------------------------------------------------ огонь на полу

        private void LayPatch()
        {
            var eye = Eye;

            if (!Ctx.RayToRock(eye.position, eye.forward, N.Range + 1f, out var rock)) return;

            var maxPatches = Mathf.RoundToInt(N[WeaponStat.Extra2]);

            // Новое пятно рядом со старым — освежает его, а не множит.
            for (var i = 0; i < _patches.Count; i++)
            {
                if ((_patches[i].Position - rock.point).sqrMagnitude > PatchRadius * PatchRadius) continue;

                var patch = _patches[i];
                patch.Left = N[WeaponStat.AltDuration];
                _patches[i] = patch;
                return;
            }

            if (_patches.Count >= maxPatches) _patches.RemoveAt(0);

            _patches.Add(new Patch
            {
                Position = rock.point,
                Normal = rock.normal,
                Left = N[WeaponStat.AltDuration],
                Tick = 0f
            });

            PatchesLaid++;
            Ctx.Marks?.Scorch(rock.point, 0.65f);
        }

        private void UpdatePatches(float deltaTime)
        {
            for (var i = _patches.Count - 1; i >= 0; i--)
            {
                var patch = _patches[i];

                patch.Left -= deltaTime;
                patch.Tick -= deltaTime;

                if (patch.Left <= 0f)
                {
                    _patches.RemoveAt(i);
                    continue;
                }

                if (patch.Tick <= 0f)
                {
                    patch.Tick = 0.25f;

                    // Пятно поджигает пересекающих, само почти не жжёт: огонь — статус, а не урон.
                    var spec = HitSpec.Sphere(patch.Position + patch.Normal * 0.4f, PatchRadius, 1f, Source, HitKind.Fire);
                    spec.BurnSeconds = N[WeaponStat.Extra1];
                    spec.BurnDps = N[WeaponStat.Status];
                    spec.ThroughWalls = true;

                    Strike(spec, false);
                }

                if (Ctx.Explosions != null && Random.value < deltaTime * 10f * CaveExplosions.Density)
                {
                    var offset = Random.insideUnitSphere * PatchRadius * 0.8f;
                    offset -= patch.Normal * Vector3.Dot(offset, patch.Normal);

                    var life = Mathf.Clamp01(patch.Left / Mathf.Max(0.01f, N[WeaponStat.AltDuration]));

                    Ctx.Explosions.Flame(patch.Position + offset + patch.Normal * 0.2f,
                        patch.Normal * Random.Range(1f, 2.5f), Random.Range(0.3f, 0.7f) * (0.5f + life), Random.Range(0.4f, 0.7f));

                    if (Random.value < 0.3f) Ctx.Explosions.Ember(patch.Position + offset, patch.Normal * 1.5f);
                }

                _patches[i] = patch;
            }
        }

        // ------------------------------------------------------------------ выдох и пекло

        private void Exhale()
        {
            _fuel -= ExhaleFuel;
            _exhaleCooldown = N[WeaponStat.AltCooldown];

            var centre = Ctx.Body.BodyCentre;

            var spec = HitSpec.Sphere(centre, N[WeaponStat.AltArea], N[WeaponStat.AltDamage], Source, HitKind.Fire);
            spec.Knockback = N[WeaponStat.Knockback];
            spec.Stun = 0.3f;
            spec.BurnSeconds = N[WeaponStat.Extra1];
            spec.BurnDps = N[WeaponStat.Status];

            Strike(spec);

            if (Ctx.Explosions != null)
            {
                var count = Mathf.RoundToInt(40 * CaveExplosions.Density);

                for (var i = 0; i < count; i++)
                {
                    var angle = i / (float)count * Mathf.PI * 2f;
                    var direction = new Vector3(Mathf.Cos(angle), Random.Range(-0.1f, 0.25f), Mathf.Sin(angle));

                    Ctx.Explosions.Flame(centre + direction * 0.6f, direction * N[WeaponStat.AltArea] * 3f,
                        Random.Range(0.8f, 1.4f), Random.Range(0.35f, 0.55f));
                }

                Ctx.Explosions.Light(centre, 40f, 12f);
            }

            Ctx.Audio?.Play(SoundId.FlameBurst, 1f, 1f, null, 5);
            Ctx.Feedback.Shake(0.35f);
        }

        /// <summary>«Пекло»: сгоревший от этой пушки лопается облачком, поджигая соседей.</summary>
        private void OnKilled(IReadOnlyList<KillRecord> kills)
        {
            if (!Evolved) return;

            for (var i = 0; i < kills.Count && _popsThisFrame < 12; i++)
            {
                if (kills[i].Source != Source) continue;

                _popsThisFrame++;

                var spec = HitSpec.Sphere(kills[i].Position, 1.8f, N.Damage, Source, HitKind.Fire);
                spec.BurnSeconds = N[WeaponStat.Extra1];
                spec.BurnDps = N[WeaponStat.Status];
                spec.ThroughWalls = true;

                Strike(spec, false);

                Ctx.Explosions?.Flame(kills[i].Position, Vector3.up * 2f, 1.2f, 0.4f);
            }
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = -1,
            Gauge = Mathf.Clamp01(_fuel / N.Magazine),
            AltUnlocked = AltUnlocked,
            AltReady = AltUnlocked ? 1f - _exhaleCooldown / Mathf.Max(0.01f, N[WeaponStat.AltCooldown]) : 0f,
            AltCharges = AltUnlocked && _exhaleCooldown <= 0f && _fuel >= ExhaleFuel ? 1 : 0
        };
    }
}
