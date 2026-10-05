using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Гвоздомёт» (Nailgun из Ultrakill). Роль — контроль: замедляет толпу, держит коридор.
    ///
    /// Огонь: 14 гвоздей в секунду снарядами (скорость 60, лёгкий разброс), урон 6, каждый гвоздь
    /// замедляет на 6% (до −45%, держится 1.5 с — «гвозди в лапах»). Нагрев +5 за гвоздь из 100,
    /// остывание 35 в секунду, перегрев — 1.5 с блокировки. Ствол краснеет от нагрева.
    ///
    /// Альт «Магнит» (ур. 3): бросок магнита, прилипает к камню, живёт 6 с, до двух. Гвозди,
    /// пролетающие в 8 юнитах, заворачивают на орбиту вокруг него (радиус 2.5) и бьют всё, что
    /// пробегает. Мясорубка в развилке.
    ///
    /// Эволюция «Пилорама» (ур. 5 + Темп 3): гвозди становятся пилами — пробивают двоих
    /// и дважды рикошетят от камня.
    /// </summary>
    public sealed class NailgunWeapon : Weapon
    {
        private const int KindNail = 0;
        private const int KindMagnet = 1;
        private const int KindOrbit = 2;

        private const float SlowSeconds = 1.5f;
        private const float SlowMax = 0.45f;
        private const float OrbitSpeed = 14f;

        private float _heat;
        private float _lock;
        private float _nextNail;
        private float _magnetCooldown;
        private float _spin;

        private Transform _barrel;
        private Renderer _barrelRenderer;
        private MaterialPropertyBlock _block;
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private int _nailVisual = -1;
        private int _sawVisual = -1;
        private int _magnetVisual = -1;

        private readonly List<Shot> _owned = new List<Shot>();
        private readonly List<Shot> _magnets = new List<Shot>();

        public int Overheats { get; private set; }

        protected override void Build(Transform view)
        {
            var m = Ctx.Materials;

            var metal = m.Lit("Nailgun Metal", new Color(0.3f, 0.31f, 0.33f), 0.5f, 0.5f);
            var grip = m.Lit("Nailgun Grip", new Color(0.12f, 0.12f, 0.12f), 0f, 0.3f);
            var dark = m.Lit("Nailgun Dark", new Color(0.03f, 0.03f, 0.03f), 0.3f, 0.2f);
            var barrel = m.Lit("Nailgun Barrel", new Color(0.35f, 0.35f, 0.36f), 0.6f, 0.5f, new Color(0.001f, 0f, 0f));

            _barrelRenderer = m.Part(view, "Body", m.Own(GunModels.Nailgun()), metal, grip, dark, barrel);
            _barrel = _barrelRenderer.transform;
            _block = new MaterialPropertyBlock();

            Motion.Offset = new Vector3(0.2f, -0.19f, 0.3f);

            if (Ctx.Projectiles == null) return;

            var steel = m.Lit("Nail Steel", new Color(0.7f, 0.7f, 0.72f), 0.8f, 0.7f);
            var saw = m.Lit("Saw Steel", new Color(0.8f, 0.75f, 0.7f), 0.9f, 0.8f, new Color(0.4f, 0.2f, 0.05f));
            var magnetRed = m.Lit("Magnet Red", new Color(0.7f, 0.08f, 0.06f), 0.3f, 0.5f, new Color(0.3f, 0.02f, 0.01f));
            var magnetSteel = m.Lit("Magnet Steel", new Color(0.6f, 0.6f, 0.62f), 0.8f, 0.6f);

            _nailVisual = Ctx.Projectiles.AddVisual(new ShotVisual
            {
                Mesh = m.Own(GunModels.Nail()),
                Materials = new[] { steel },
                Scale = 1.6f,
                StreakHead = new Color(1f, 0.9f, 0.7f, 0.7f),
                StreakTail = new Color(1f, 0.7f, 0.4f, 0f),
                StreakLength = 1.2f,
                StreakWidth = 0.025f
            });

            _sawVisual = Ctx.Projectiles.AddVisual(new ShotVisual
            {
                Mesh = m.Own(GunModels.Coin()),
                Materials = new[] { saw },
                Scale = 2.4f,
                AlignToVelocity = true,
                Spin = 2000f,
                StreakHead = new Color(1f, 0.8f, 0.5f, 0.6f),
                StreakTail = new Color(1f, 0.5f, 0.2f, 0f),
                StreakLength = 1f,
                StreakWidth = 0.05f
            });

            _magnetVisual = Ctx.Projectiles.AddVisual(new ShotVisual
            {
                Mesh = m.Own(GunModels.Magnet()),
                Materials = new[] { magnetRed, magnetSteel },
                Scale = 2.2f,
                AlignToVelocity = false
            });
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && _magnetCooldown <= 0f && CountMagnets() < N[WeaponStat.AltCharges])
            {
                ThrowMagnet();
            }

            if (_lock > 0f || !commands.FireHeld) return;

            _spin += deltaTime * 30f;

            // Несколько гвоздей за кадр при низкой частоте кадров: темп — свойство пушки, а не кадра.
            var interval = 1f / N.Rate;

            while (Ctx.Time >= _nextNail && _lock <= 0f)
            {
                _nextNail = Mathf.Max(_nextNail + interval, Ctx.Time - interval);
                FireNail();
            }
        }

        public override void TickBackground(float deltaTime)
        {
            _magnetCooldown = Cooldown(_magnetCooldown, deltaTime);

            if (_lock > 0f)
            {
                _lock -= deltaTime;
                if (_lock <= 0f) _heat = 0f;
            }
            else
            {
                _heat = Mathf.Max(0f, _heat - N[WeaponStat.Refill] * deltaTime);
            }

            UpdateOrbits(deltaTime);
        }

        private void FireNail()
        {
            _heat += N[WeaponStat.Drain];
            Motion.Kick = Mathf.Min(1f, Motion.Kick + 0.25f);

            if (_heat >= N.Magazine)
            {
                _lock = N.Reload;
                Overheats++;
                Ctx.Audio?.Play(SoundId.Overheat, 0.8f);
            }

            var eye = Eye;
            var spread = Random.insideUnitCircle * Mathf.Tan(N[WeaponStat.Spread] * Mathf.Deg2Rad);
            var direction = (eye.forward + eye.right * spread.x + eye.up * spread.y).normalized;

            // Из глаз, а не из ствола: гвоздь должен лететь в перекрестие, а ствол сбоку
            // уводил бы очередь вбок у стены.
            var start = eye.position + eye.forward * 0.35f;
            var saw = Evolved;

            Ctx.Projectiles?.Launch(new Shot
            {
                Owner = ShotOwner,
                Visual = saw ? _sawVisual : _nailVisual,
                Flags = ShotFlags.NearSpiders | ShotFlags.NearBosses,
                Position = start,
                Velocity = direction * N[WeaponStat.Speed],
                Gravity = 0f,
                Radius = 0.03f,
                Proximity = 0.25f,
                Life = N.Range,
                Tag = KindNail,
                Hits = saw ? 1 + Mathf.RoundToInt(N[WeaponStat.Pierce]) : 1,
                Payload = saw ? 2f : 0f
            });

            Ctx.Audio?.Play(SoundId.NailFire, 0.45f, 1f, null, 4);
            Ctx.Feedback.Kick(0.35f);
        }

        // ------------------------------------------------------------------ снаряды

        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            if (shot.Tag == KindMagnet)
            {
                if (what == ShotEvent.HitRock || what == ShotEvent.NearSpider)
                {
                    shot.Flags = (shot.Flags | ShotFlags.Stuck) & ~(ShotFlags.NearSpiders | ShotFlags.NearBosses);
                    shot.Velocity = Vector3.zero;
                    shot.Normal = what == ShotEvent.HitRock ? hit.normal : Vector3.up;
                    shot.Age = 0f;
                    shot.Life = N[WeaponStat.AltDuration];
                }
                else if (what == ShotEvent.Expired)
                {
                    shot.Dead = true;
                }

                return;
            }

            switch (what)
            {
                case ShotEvent.NearSpider:
                case ShotEvent.NearBoss:
                    HitAt(shot.Position, shot.Velocity);

                    shot.Hits--;
                    if (shot.Hits <= 0) shot.Dead = true;
                    else shot.Position += shot.Velocity.normalized * 0.8f;

                    return;

                case ShotEvent.HitRock:
                    // Пила рикошетит от камня, гвоздь застревает искрой.
                    if (shot.Payload >= 1f && shot.Tag != KindOrbit)
                    {
                        shot.Payload -= 1f;
                        shot.Velocity = Vector3.Reflect(shot.Velocity, hit.normal);
                        shot.Position = hit.point + hit.normal * 0.1f;
                        return;
                    }

                    if (shot.Tag == KindOrbit) return;

                    Ctx.Explosions?.Impact(hit.point, hit.normal, 2);
                    shot.Dead = true;
                    return;

                default:
                    shot.Dead = true;
                    return;
            }
        }

        private readonly List<int> _nearest = new List<int>(1);
        private static readonly List<float> FullShare = new List<float> { 1f };

        /// <summary>
        /// Гвоздь бьёт ОДНОГО — ближайшего к точке, где сработал взрыватель. Сфера у края тела
        /// промахивалась мимо центра крупных (взрыватель срабатывает на лапах), а сфера шире —
        /// била бы троих одним гвоздём. Заливки по ходам нет: гвоздь уже долетел сам.
        /// </summary>
        private void HitAt(Vector3 point, Vector3 velocity)
        {
            if (Ctx.Crowd != null && Ctx.Crowd.FindNearest(point, 2.5f, 1, _nearest, default, 0f, false, null, false) > 0)
            {
                var single = HitSpec.Sphere(point, 0.7f, N.Damage, Source, HitKind.Bullet);
                single.SlowSeconds = SlowSeconds;
                single.SlowAdd = N[WeaponStat.Status];
                single.SlowMax = SlowMax;
                single.Impulse = 5f;
                single.Direction = velocity.normalized;
                single.CritChance = N[WeaponStat.Crit];
                single.CritScale = 2f;

                var hit = Ctx.Crowd.HitIndices(_nearest, FullShare, single);

                Kills += hit.Killed;
                DamageDealt += hit.Dealt;

                if (hit.Killed > 0) Ctx.Feedback.Mark(hit);
                return;
            }

            var spec = HitSpec.Sphere(point, 0.7f, N.Damage, Source, HitKind.Bullet);
            spec.SlowSeconds = SlowSeconds;
            spec.SlowAdd = N[WeaponStat.Status];
            spec.SlowMax = SlowMax;
            spec.Impulse = 5f;
            spec.Direction = velocity.normalized;

            // Маркер у прицела — не на каждый гвоздь: четырнадцать в секунду превращают его в рябь.
            var report = Strike(spec, false);

            if (report.Killed > 0) Ctx.Feedback.Mark(report);
        }

        // ------------------------------------------------------------------ магнит

        private void ThrowMagnet()
        {
            if (Ctx.Projectiles == null) return;

            _magnetCooldown = N[WeaponStat.AltCooldown];

            var eye = Eye;

            Ctx.Projectiles.Launch(new Shot
            {
                Owner = ShotOwner,
                Visual = _magnetVisual,
                Flags = ShotFlags.NearSpiders,
                Position = eye.position + eye.forward * 0.5f,
                Velocity = eye.forward * 22f + Vector3.up * 2f,
                Gravity = 9f,
                Radius = 0.08f,
                Proximity = 0.4f,
                Life = 4f,
                Tag = KindMagnet
            });

            Ctx.Audio?.Play(SoundId.Reload, 0.6f, 0.7f);
        }

        private int CountMagnets()
        {
            if (Ctx.Projectiles == null) return 0;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            var count = 0;
            foreach (var shot in _owned) if (shot.Tag == KindMagnet) count++;

            return count;
        }

        /// <summary>
        /// Гвозди у магнита — на орбиту: позиция по кругу, скорость по касательной (росчерк
        /// читается дугой). Орбитальный гвоздь живёт, пока жив магнит, и выдерживает три удара.
        /// </summary>
        private void UpdateOrbits(float deltaTime)
        {
            if (Ctx.Projectiles == null || !AltUnlocked) return;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            _magnets.Clear();
            foreach (var shot in _owned) if (shot.Tag == KindMagnet && shot.IsStuck) _magnets.Add(shot);

            if (_magnets.Count == 0) return;

            var capture = N[WeaponStat.AltArea];
            var radius = N[WeaponStat.Extra1];

            foreach (var shot in _owned)
            {
                if (shot.Tag == KindMagnet) continue;

                var nail = shot;

                if (nail.Tag == KindNail)
                {
                    foreach (var magnet in _magnets)
                    {
                        if ((nail.Position - magnet.Position).sqrMagnitude > capture * capture) continue;

                        nail.Tag = KindOrbit;
                        nail.Link = magnet.Id;
                        nail.Hits = Mathf.Max(nail.Hits, 3);
                        nail.Flags |= ShotFlags.Ghost;
                        nail.Age = 0f;
                        nail.Life = Mathf.Max(0.5f, magnet.Life - magnet.Age);

                        var offset = nail.Position - magnet.Position;
                        nail.Payload = Mathf.Atan2(Vector3.Dot(offset, Basis(magnet).v), Vector3.Dot(offset, Basis(magnet).u));
                        break;
                    }
                }

                if (nail.Tag != KindOrbit) continue;

                if (!Ctx.Projectiles.TryGet(nail.Link, out var owner))
                {
                    nail.Dead = true;
                    Ctx.Projectiles.Kill(nail.Id);
                    continue;
                }

                var (u, v) = Basis(owner);

                nail.Payload += OrbitSpeed / Mathf.Max(0.5f, radius) * deltaTime;

                var centre = owner.Position + owner.Normal * 0.6f;
                var angle = nail.Payload;

                nail.Position = centre + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * radius;
                nail.Velocity = (-u * Mathf.Sin(angle) + v * Mathf.Cos(angle)) * OrbitSpeed;

                Ctx.Projectiles.Replace(nail);
            }
        }

        /// <summary>Плоскость орбиты — поперёк нормали камня, к которому прилип магнит.</summary>
        private static (Vector3 u, Vector3 v) Basis(in Shot magnet)
        {
            var normal = magnet.Normal.sqrMagnitude > 0.5f ? magnet.Normal.normalized : Vector3.up;
            var u = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up).normalized;

            return (u, Vector3.Cross(normal, u));
        }

        // ------------------------------------------------------------------ модель

        public override void TickView(float deltaTime)
        {
            // Ствол краснеет от нагрева — блоком свойств, не renderer.material (грабли №3).
            if (_barrelRenderer != null)
            {
                var heat = Mathf.Clamp01(_heat / N.Magazine);
                var glow = new Color(1.8f, 0.35f, 0.08f) * (heat * heat) + (_lock > 0f ? new Color(0.6f, 0.05f, 0f) : Color.clear);

                _barrelRenderer.GetPropertyBlock(_block, 3);
                _block.SetColor(EmissionId, glow);
                _barrelRenderer.SetPropertyBlock(_block, 3);
            }

            Motion.Reload = _lock > 0f ? 0.5f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(1f - _lock / N.Reload)) : 0f;
            Motion.ExtraEuler = new Vector3(0f, 0f, Mathf.Sin(_spin) * 0.5f);

            base.TickView(deltaTime);
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = -1,
            Gauge = Mathf.Clamp01(_heat / N.Magazine),
            GaugeIsHeat = true,
            Reload = _lock > 0f ? 1f - _lock / N.Reload : 0f,
            AltUnlocked = AltUnlocked,
            AltReady = AltUnlocked ? 1f - _magnetCooldown / Mathf.Max(0.01f, N[WeaponStat.AltCooldown]) : 0f,
            AltCharges = AltUnlocked ? Mathf.Max(0, Mathf.RoundToInt(N[WeaponStat.AltCharges]) - CountMagnets()) : 0
        };
    }
}
