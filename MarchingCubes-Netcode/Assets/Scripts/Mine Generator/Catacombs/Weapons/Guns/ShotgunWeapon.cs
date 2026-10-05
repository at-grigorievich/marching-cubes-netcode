using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Ядро» — дробовик (Shotgun из Ultrakill, Doom). Роль — «отстань» вплотную: отброс главное,
    /// урон второе.
    ///
    /// Огонь: конус 9° на 12 юнитов, полный урон до 4 юнитов, дальше спад к 20%; 80 HP в упор,
    /// отброс 9 и оглушение 0.4 с; 1.1 выстрела в секунду, 8 патронов, заряжание по одному
    /// (0.45 с), выстрел прерывает заряжание. Убийства в упор — двойное временное здоровье.
    ///
    /// Альт «Выброс ядра» (ур. 3, откат 4 с): ядро летит дугой и рвётся о камень (радиус 4, 50 HP).
    /// Выстрел по ядру в полёте — детонация ×2 радиуса и ×1.5 урона со стоп-кадром: главный
    /// «стильный» приём пушки.
    ///
    /// Эволюция «Таран» (ур. 5 + Панцирь 3): выстрел сносит толпу на 4 юнита вглубь коридора.
    /// </summary>
    public sealed class ShotgunWeapon : Weapon
    {
        /// <summary>Доля дальности, до которой урон полный: 4 юнита из 12.</summary>
        private const float FullShare = 4f / 12f;

        private int _shells;
        private float _nextShot;
        private float _loadLeft;
        private float _coreCooldown;
        private float _pump;

        private Transform _pumpTransform;
        private int _coreVisual = -1;

        private readonly List<Shot> _owned = new List<Shot>();

        /// <summary>Сколько ядер подорвано выстрелом в полёте — для прогонов.</summary>
        public int CoresShot { get; private set; }

        protected override void Build(Transform view)
        {
            _shells = N.Magazine;

            var m = Ctx.Materials;

            var metal = m.Lit("Shotgun Metal", new Color(0.22f, 0.22f, 0.24f), 0.5f, 0.5f);
            var wood = m.Lit("Shotgun Wood", new Color(0.32f, 0.18f, 0.09f), 0f, 0.35f);
            var dark = m.Lit("Shotgun Dark", new Color(0.03f, 0.03f, 0.03f), 0.3f, 0.2f);
            var bead = m.Lit("Shotgun Bead", new Color(1f, 0.8f, 0.4f), 0f, 0.6f, new Color(0.7f, 0.5f, 0.1f));

            m.Part(view, "Body", m.Own(GunModels.Shotgun()), metal, wood, dark, bead);
            _pumpTransform = m.Part(view, "Pump", m.Own(GunModels.ShotgunPump()), metal, wood, dark).transform;

            Motion.Offset = new Vector3(0.2f, -0.2f, 0.26f);

            if (Ctx.Projectiles != null)
            {
                var hot = m.Lit("Core Hot", new Color(0.4f, 0.2f, 0.1f), 0.3f, 0.3f, new Color(1.6f, 0.6f, 0.15f));

                _coreVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = m.Own(GunModels.Core()),
                    Materials = new[] { hot },
                    Scale = 1.6f,
                    AlignToVelocity = true,
                    Spin = 360f,
                    StreakHead = new Color(1f, 0.6f, 0.2f, 0.9f),
                    StreakTail = new Color(0.6f, 0.2f, 0.05f, 0f),
                    StreakLength = 1.4f,
                    StreakWidth = 0.1f
                });
            }
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && _coreCooldown <= 0f) ThrowCore();

            if (commands.FireHeld && Ctx.Time >= _nextShot)
            {
                if (_shells > 0)
                {
                    // Выстрел прерывает заряжание: патрон в руке подождёт.
                    _loadLeft = 0f;
                    Fire();
                }
                else if (commands.FirePressed)
                {
                    Ctx.Audio?.Play(SoundId.Empty, 0.6f);
                }
            }
        }

        public override void TickBackground(float deltaTime)
        {
            _coreCooldown = Cooldown(_coreCooldown, deltaTime);

            // Заряжание по одному, само, когда не стреляют.
            if (_shells < N.Magazine && Ctx.Time >= _nextShot)
            {
                if (_loadLeft <= 0f) _loadLeft = N.Reload;

                _loadLeft -= deltaTime;

                if (_loadLeft <= 0f)
                {
                    _shells++;
                    _loadLeft = _shells < N.Magazine ? N.Reload : 0f;
                    if (IsEquipped) Ctx.Audio?.Play(SoundId.ShellLoad, 0.5f);
                }
            }
        }

        private void Fire()
        {
            _shells--;
            _nextShot = Ctx.Time + 1f / N.Rate;
            _pump = 1f;
            Motion.Kick = 1.3f;

            var eye = Eye;
            var muzzle = View.TransformPoint(GunModels.ShotgunMuzzle);
            var range = N.Range;
            var halfAngle = N.Area;

            // Выстрел по ядру в полёте — до урона по толпе: ядро ближе, и взрыв идёт первым.
            ShootCores(eye.position, eye.forward, range, halfAngle);

            var spec = HitSpec.Cone(eye.position, eye.forward, range, halfAngle, N.Damage, Source, HitKind.Bullet);
            spec.FullShare = FullShare;
            spec.MinShare = 0.2f;
            spec.Knockback = N[WeaponStat.Knockback] + (Evolved ? N[WeaponStat.Extra1] * 5f : 0f);
            spec.Stun = N[WeaponStat.Status];
            spec.Impulse = 18f;

            var report = Strike(spec);

            // Дробины — росчерками: по ним видно, куда ушёл конус, и где он упёрся в камень.
            var pellets = Mathf.Max(3, Mathf.RoundToInt(N[WeaponStat.Projectiles]));

            for (var i = 0; i < pellets; i++)
            {
                var spread = Random.insideUnitCircle * Mathf.Tan(halfAngle * Mathf.Deg2Rad);
                var direction = (eye.forward + eye.right * spread.x + eye.up * spread.y).normalized;

                var length = Ctx.RayToRock(eye.position, direction, range, out var rock) ? rock.distance : range;

                Ctx.Fx?.Line(muzzle, eye.position + direction * length, 0.018f, new Color(1f, 0.85f, 0.5f, 0.8f),
                    new Color(1f, 0.6f, 0.2f, 0f), 0.08f);

                if (rock.collider != null && i % 3 == 0) Ctx.Explosions?.Impact(rock.point, rock.normal, 3);
            }

            Ctx.Explosions?.Light(muzzle + eye.forward * 0.4f, 26f, 8f);
            Ctx.Audio?.Play(SoundId.ShotgunFire, 1f, 1f, null, 5);
            if (report.Any) Ctx.Audio?.Play(report.Killed > 0 ? SoundId.KillTick : SoundId.HitTick, 0.5f);

            Ctx.Feedback.Kick(5f);
            Ctx.Feedback.Shake(0.3f);

            if (Evolved) Ctx.Feedback.HitStop(0.03f);
        }

        private void ThrowCore()
        {
            if (Ctx.Projectiles == null) return;

            _coreCooldown = N[WeaponStat.AltCooldown];

            var eye = Eye;

            Ctx.Projectiles.Launch(new Shot
            {
                Owner = ShotOwner,
                Visual = _coreVisual,
                Flags = ShotFlags.None,
                Position = View.TransformPoint(GunModels.ShotgunMuzzle),
                Velocity = eye.forward * N[WeaponStat.Speed] + Vector3.up * 4f,
                Gravity = 12f,
                Radius = 0.12f,
                Life = 3f
            });

            Ctx.Audio?.Play(SoundId.LauncherFire, 0.7f, 1.3f);
            Ctx.Feedback.Kick(2f);
        }

        /// <summary>Ядро в конусе выстрела — детонация вдвое шире и в полтора раза сильнее.</summary>
        private void ShootCores(Vector3 origin, Vector3 axis, float range, float halfAngle)
        {
            if (Ctx.Projectiles == null) return;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            // Ядро мелкое — конус по нему шире выстрела: попасть должно быть можно не только снайперу.
            var cos = Mathf.Cos((halfAngle + 6f) * Mathf.Deg2Rad);

            foreach (var core in _owned)
            {
                var v = core.Position - origin;
                if (v.magnitude > range * 1.5f || Vector3.Dot(v.normalized, axis) < cos) continue;

                Ctx.Projectiles.Kill(core.Id);
                CoresShot++;

                Explode(core.Position, Vector3.up, 2f, 1.5f);
                Ctx.Feedback.HitStop(0.07f);
                Ctx.Audio?.Play(SoundId.CoinPing, 0.6f, 0.6f);
            }
        }

        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            shot.Dead = true;

            var normal = what == ShotEvent.HitRock ? hit.normal : Vector3.up;
            Explode(shot.Position + normal * 0.1f, normal, 1f, 1f);
        }

        private void Explode(Vector3 point, Vector3 normal, float radiusScale, float damageScale)
        {
            var radius = N[WeaponStat.AltArea] * radiusScale;

            if (Ctx.Marks != null && radiusScale > 1.5f) Ctx.Marks.Blast(point);

            var spec = HitSpec.Sphere(point, radius, N[WeaponStat.AltDamage] * damageScale, Source);
            spec.GibShare = Ctx.Crowd != null ? Ctx.Crowd.GibShare : 0.65f;
            spec.Knockback = 6f;

            Strike(spec);
            SprayGibs(point, normal);

            Ctx.Explosions?.Explode(point, normal, radius);
            Ctx.Audio?.Play(SoundId.Explosion, 0.8f * radiusScale, 1.1f, point, 4);

            var distance = (point - Eye.position).magnitude;
            Ctx.Feedback.Shake(Mathf.Clamp01(1.2f - distance / 22f) * 0.8f * radiusScale);
        }

        public override void TickView(float deltaTime)
        {
            var dt = Mathf.Max(deltaTime, 1e-4f);

            _pump = Mathf.Lerp(_pump, 0f, dt * 6f);

            // Помпа: назад и обратно — по ней видно, что выстрел был и что следующий готовится.
            var stroke = Mathf.Sin(Mathf.Clamp01(1f - _pump) * Mathf.PI) * (_pump > 0.02f ? 1f : 0f);
            if (_pumpTransform != null) _pumpTransform.localPosition = new Vector3(0f, 0f, -0.08f * stroke);

            Motion.Reload = _loadLeft > 0f && Ctx.Time >= _nextShot ? 0.25f : 0f;

            base.TickView(deltaTime);
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = _shells,
            Capacity = N.Magazine,
            Gauge = -1f,
            Reload = _loadLeft > 0f ? 1f - _loadLeft / N.Reload : 0f,
            AltUnlocked = AltUnlocked,
            AltReady = AltUnlocked ? 1f - _coreCooldown / Mathf.Max(0.01f, N[WeaponStat.AltCooldown]) : 0f,
            AltCharges = AltUnlocked && _coreCooldown <= 0f ? 1 : 0
        };
    }
}
