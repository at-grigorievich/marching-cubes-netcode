using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Дуга» — тесла-ружьё. Снимать пауков со стен и свода (у игрока там 40–60% толпы,
    /// CLAUDE.md), куда гранатой не попасть. Лучшая пушка для тача: наводится сама.
    ///
    /// Огонь: удержание — захват ближайшего в конусе 12° на 14 юнитах, молния прыгает ещё
    /// на четыре цели в 5 юнитах (−15% за прыжок), 8 ударов в секунду по 16 HP, ток 0.25 с —
    /// оглушение. Батарея 100, −18 в секунду, +25 заряда без стрельбы.
    ///
    /// Альт «Громоотвод» (ур. 3): воткнуть катушку (до двух), 8 с бьёт трёх ближайших
    /// в 7 юнитах раз в полсекунды. Перекликается с пилонами Матки — та же эстетика.
    ///
    /// Эволюция «Индукция» (ур. 5 + Магнит 3): молния подтягивает мелочь к точке удара.
    /// </summary>
    public sealed class TeslaWeapon : Weapon
    {
        private const int KindRod = 0;
        private const float RodInterval = 0.5f;
        private const int RodTargets = 3;
        private const float JumpFalloff = 0.15f;

        private float _battery;
        private float _nextZap;
        private float _rodCooldown;
        private bool _firing;

        private Renderer _body;
        private MaterialPropertyBlock _block;
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private int _rodVisual = -1;
        private float _rodTimer;

        private readonly List<int> _found = new List<int>();
        private readonly List<int> _chain = new List<int>();
        private readonly List<float> _shares = new List<float>();
        private readonly List<Shot> _owned = new List<Shot>();

        private static readonly Color BoltColor = new Color(0.55f, 0.75f, 1f, 1f);

        public int Zaps { get; private set; }

        protected override void Build(Transform view)
        {
            _battery = N.Magazine;

            var m = Ctx.Materials;

            var metal = m.Lit("Tesla Metal", new Color(0.3f, 0.3f, 0.32f), 0.55f, 0.55f);
            var grip = m.Lit("Tesla Grip", new Color(0.12f, 0.1f, 0.08f), 0f, 0.35f);
            var dark = m.Lit("Tesla Dark", new Color(0.03f, 0.03f, 0.04f), 0.3f, 0.2f);
            var coil = m.Lit("Tesla Coil", new Color(0.55f, 0.35f, 0.15f), 0.9f, 0.6f, new Color(0.001f, 0.001f, 0.001f));

            _body = m.Part(view, "Body", m.Own(GunModels.Tesla()), metal, grip, dark, coil);
            _block = new MaterialPropertyBlock();

            Motion.Offset = new Vector3(0.2f, -0.19f, 0.28f);

            if (Ctx.Projectiles != null)
            {
                var glow = m.Lit("Rod Glow", new Color(0.3f, 0.45f, 0.8f), 0.5f, 0.5f, new Color(0.6f, 1f, 2.5f));

                _rodVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = m.Own(GunModels.Rod()),
                    Materials = new[] { metal, glow },
                    Scale = 1.5f,
                    AlignToVelocity = true
                });
            }
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && _rodCooldown <= 0f && CountRods() < MaxRods) PlantRod();

            _firing = commands.FireHeld && _battery > 0f;

            Ctx.Audio?.Loop(SoundId.TeslaLoop, _firing, 0.35f, 1f + 0.2f * Mathf.Sin(Ctx.Time * 9f));

            if (!_firing) return;

            _battery = Mathf.Max(0f, _battery - N[WeaponStat.Drain] * deltaTime);

            var interval = 1f / N.Rate;

            while (Ctx.Time >= _nextZap)
            {
                _nextZap = Mathf.Max(_nextZap + interval, Ctx.Time - interval);
                Zap();
            }
        }

        public override void Holster()
        {
            base.Holster();

            _firing = false;
            Ctx.Audio?.Loop(SoundId.TeslaLoop, false);
        }

        public override void Dispose()
        {
            Ctx.Audio?.Loop(SoundId.TeslaLoop, false);
            base.Dispose();
        }

        public override void TickBackground(float deltaTime)
        {
            _rodCooldown = Cooldown(_rodCooldown, deltaTime);

            if (!_firing || !IsEquipped) _battery = Mathf.Min(N.Magazine, _battery + N[WeaponStat.Refill] * deltaTime);
            if (!IsEquipped) _firing = false;

            _rodTimer -= deltaTime;

            if (_rodTimer <= 0f)
            {
                _rodTimer = RodInterval;
                UpdateRods();
            }
        }

        private int MaxRods => Mathf.RoundToInt(N[WeaponStat.AltCharges]) +
                               (Ctx.Stats != null ? Mathf.RoundToInt(Ctx.Stats.Get(Stat.ExtraRods)) : 0);

        // ------------------------------------------------------------------ молния

        private void Zap()
        {
            var eye = Eye;
            var muzzle = View.TransformPoint(GunModels.TeslaMuzzle);

            // Сначала босс в конусе: Матка и одиночки — главные цели, их видно и без толпы.
            if (ZapBoss(eye, muzzle)) return;

            if (Ctx.Crowd == null) return;

            Ctx.Crowd.FindNearest(eye.position, N.Range, 1, _found, eye.forward, N.Area);

            if (_found.Count == 0)
            {
                // Холостой разряд в воздух — видно, что пушка работает, но цели нет.
                var length = Ctx.RayToRock(eye.position, eye.forward, N.Range * 0.4f, out var rock) ? rock.distance : N.Range * 0.4f;
                Ctx.Fx?.Bolt(muzzle, eye.position + eye.forward * length, 0.025f, BoltColor * 0.6f, 0.06f, 5, 0.12f);
                return;
            }

            Chain(_found[0], muzzle, N.Damage, Mathf.RoundToInt(N[WeaponStat.Projectiles]), true);
        }

        /// <summary>Цепь от первой цели: каждая следующая — ближайшая к прошлой, на 15% слабее.</summary>
        private void Chain(int first, Vector3 from, float damage, int jumps, bool mark)
        {
            _chain.Clear();
            _shares.Clear();

            _chain.Add(first);
            _shares.Add(1f);

            var share = 1f;
            var current = first;

            for (var k = 0; k < jumps; k++)
            {
                if (!Ctx.Crowd.TryGetTarget(current, out var position, out _, out _)) break;

                Ctx.Crowd.FindNearest(position, N[WeaponStat.Extra1], 1, _found, default, 0f, false, _chain);
                if (_found.Count == 0) break;

                share *= 1f - JumpFalloff;
                current = _found[0];

                _chain.Add(current);
                _shares.Add(share);
            }

            var previous = from;

            foreach (var index in _chain)
            {
                if (!Ctx.Crowd.TryGetTarget(index, out var position, out _, out _)) continue;

                Ctx.Fx?.Bolt(previous, position, 0.04f, BoltColor, 0.08f, 6, 0.12f);
                previous = position;
            }

            var spec = HitSpec.Sphere(from, 1f, damage, Source, HitKind.Shock);
            spec.ShockSeconds = N[WeaponStat.Status];
            spec.Impulse = 4f;

            // Индукция: тянет мелочь к точке удара — отброс внутрь.
            if (Evolved && Ctx.Crowd.TryGetTarget(first, out var pull, out _, out _))
            {
                spec.Origin = pull;
                spec.Knockback = -3f;
            }

            var report = Ctx.Crowd.HitIndices(_chain, _shares, spec);

            Kills += report.Killed;
            DamageDealt += report.Dealt;
            Zaps++;

            if (mark && report.Killed > 0) Ctx.Feedback.Mark(report);

            if (_chain.Count > 0 && Ctx.Crowd.TryGetTarget(_chain[0], out var at, out _, out _))
            {
                Ctx.Explosions?.Light(at, 10f, 6f);
            }

            Ctx.Audio?.Play(SoundId.Zap, 0.35f, 1f, null, 3);
        }

        private bool ZapBoss(Transform eye, Vector3 muzzle)
        {
            var cone = HitSpec.Cone(eye.position, eye.forward, N.Range, N.Area, N.Damage, Source, HitKind.Shock);

            if (Ctx.Queen != null && Ctx.Queen.IsVulnerable && CombatContext.Touches(cone, Ctx.Queen.Centre, Ctx.Queen.HitRadius))
            {
                DamageDealt += Ctx.Queen.TakeHit(N.Damage);
                Ctx.Fx?.Bolt(muzzle, Ctx.Queen.Centre, 0.06f, BoltColor, 0.08f, 8, 0.1f);
                Ctx.Audio?.Play(SoundId.Zap, 0.4f);
                return true;
            }

            foreach (var solo in SoloTargets.All)
            {
                if (solo == null || !solo.IsAlive || !CombatContext.Touches(cone, solo.Centre, solo.HitRadius)) continue;

                DamageDealt += solo.TakeHit(N.Damage, HitKind.Shock, Source, eye.position);
                solo.Stagger(Vector3.zero, N[WeaponStat.Status]);

                Ctx.Fx?.Bolt(muzzle, solo.Centre, 0.05f, BoltColor, 0.08f, 7, 0.1f);
                Ctx.Audio?.Play(SoundId.Zap, 0.4f);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ громоотвод

        private void PlantRod()
        {
            if (Ctx.Projectiles == null) return;

            _rodCooldown = N[WeaponStat.AltCooldown];

            var eye = Eye;

            Ctx.Projectiles.Launch(new Shot
            {
                Owner = ShotOwner,
                Visual = _rodVisual,
                Flags = ShotFlags.None,
                Position = eye.position + eye.forward * 0.5f,
                Velocity = eye.forward * 18f + Vector3.up * 3f,
                Gravity = 14f,
                Radius = 0.06f,
                Life = 4f,
                Tag = KindRod
            });

            Ctx.Audio?.Play(SoundId.Reload, 0.6f, 0.8f);
        }

        private int CountRods()
        {
            if (Ctx.Projectiles == null) return 0;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);
            return _owned.Count;
        }

        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            if (what == ShotEvent.HitRock && !shot.IsStuck)
            {
                // Воткнулся: стоит торчком вдоль нормали камня и работает своё время.
                shot.Flags |= ShotFlags.Stuck;
                shot.Velocity = Vector3.zero;
                shot.Normal = hit.normal;
                shot.Age = 0f;
                shot.Life = N[WeaponStat.AltDuration];
                return;
            }

            if (what == ShotEvent.Expired) shot.Dead = true;
        }

        private void UpdateRods()
        {
            if (Ctx.Projectiles == null || Ctx.Crowd == null) return;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            foreach (var rod in _owned)
            {
                if (!rod.IsStuck) continue;

                var top = rod.Position + rod.Normal * 0.85f;

                Ctx.Crowd.FindNearest(top, N[WeaponStat.AltArea], RodTargets, _found);

                if (_found.Count == 0) continue;

                _chain.Clear();
                _shares.Clear();

                foreach (var index in _found)
                {
                    _chain.Add(index);
                    _shares.Add(1f);

                    if (Ctx.Crowd.TryGetTarget(index, out var position, out _, out _))
                    {
                        Ctx.Fx?.Bolt(top, position, 0.035f, BoltColor, 0.12f, 6, 0.15f);
                    }
                }

                var spec = HitSpec.Sphere(top, 1f, N[WeaponStat.AltDamage], Source, HitKind.Shock);
                spec.ShockSeconds = N[WeaponStat.Status];

                var report = Ctx.Crowd.HitIndices(_chain, _shares, spec);

                Kills += report.Killed;
                DamageDealt += report.Dealt;

                Ctx.Audio?.Play(SoundId.Zap, 0.3f, 0.9f, top, 2);
            }
        }

        // ------------------------------------------------------------------ модель

        public override void TickView(float deltaTime)
        {
            if (_body != null)
            {
                var charge = Mathf.Clamp01(_battery / N.Magazine);
                var flicker = _firing ? Random.Range(0.6f, 1.4f) : 0.5f;
                var glow = new Color(0.35f, 0.6f, 1.6f) * (charge * flicker * (_firing ? 2f : 0.4f));

                _body.GetPropertyBlock(_block, 3);
                _block.SetColor(EmissionId, glow);
                _body.SetPropertyBlock(_block, 3);
            }

            Motion.ExtraPosition = _firing ? Random.insideUnitSphere * 0.0015f : Vector3.zero;

            base.TickView(deltaTime);
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = -1,
            Gauge = Mathf.Clamp01(_battery / N.Magazine),
            AltUnlocked = AltUnlocked,
            AltReady = AltUnlocked ? 1f - _rodCooldown / Mathf.Max(0.01f, N[WeaponStat.AltCooldown]) : 0f,
            AltCharges = AltUnlocked ? Mathf.Max(0, MaxRods - CountRods()) : 0
        };
    }
}
