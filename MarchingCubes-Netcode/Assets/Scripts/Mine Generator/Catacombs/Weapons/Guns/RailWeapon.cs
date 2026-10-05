using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Прострел» — рельсотрон (Railcannon из Ultrakill). Прочистить галерею: пушка, ради
    /// которой в планировке есть длинные прогоны (медиана линии 14–16 юнитов, 36–39% точек
    /// с линией 20+, CLAUDE.md «Замеры»).
    ///
    /// Огонь: удержание — заряд 0.9 с (заряженную можно держать), отпустил — луч на 120 юнитов
    /// до камня, шириной 0.8, сквозь всех, 220 HP, отброс вдоль луча. Откат 6 с. По камню
    /// в конце — гарь без воронки. Самая «громкая» пушка: нарастающий вой, стоп-кадр 80 мс,
    /// белый луч гаснет 0.4 с, удар FOV, сильная тряска.
    ///
    /// Альт «Отвёртка» (ур. 3): болт втыкается в первую цель и сверлит 4 с по 60 HP в секунду,
    /// мимо площадного множителя Матки; откат общий. Против элиты и Матки.
    ///
    /// Перезаряд (ур. 5): держать заряд две секунды — урон ×1.5.
    /// Эволюция «Двойной разряд» (ур. 5 + Запас 3): два заряда.
    /// </summary>
    public sealed class RailWeapon : Weapon
    {
        private const float OverchargeTime = 2f;
        private const float DrillTick = 0.25f;

        private float _charge;
        private float _held;
        private float _cooldown;
        private int _charges = 1;

        private Renderer _body;
        private MaterialPropertyBlock _block;
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        // Отвёртка: в кого воткнута и сколько осталось.
        private float _drillLeft;
        private float _drillTick;
        private int _drillIndex = -1;
        private bool _drillQueen;
        private ISoloTarget _drillSolo;
        private Vector3 _drillPoint;

        private readonly List<int> _targets = new List<int>();
        private readonly List<float> _shares = new List<float>();

        public int Shots { get; private set; }

        protected override void OnNumbersChanged()
        {
            _charges = Mathf.Min(_charges, N.Magazine);
        }

        protected override void Build(Transform view)
        {
            _charges = N.Magazine;

            var m = Ctx.Materials;

            var metal = m.Lit("Rail Metal", new Color(0.25f, 0.27f, 0.3f), 0.6f, 0.6f);
            var grip = m.Lit("Rail Grip", new Color(0.1f, 0.1f, 0.11f), 0f, 0.3f);
            var dark = m.Lit("Rail Dark", new Color(0.03f, 0.03f, 0.03f), 0.3f, 0.2f);
            var coils = m.Lit("Rail Coils", new Color(0.45f, 0.35f, 0.2f), 0.9f, 0.6f, new Color(0.001f, 0.001f, 0.001f));

            _body = m.Part(view, "Body", m.Own(GunModels.Rail()), metal, grip, dark, coils);
            _block = new MaterialPropertyBlock();

            Motion.Offset = new Vector3(0.19f, -0.2f, 0.24f);
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && _charges > 0 && _drillLeft <= 0f)
            {
                FireBolt();
                return;
            }

            if (_charges <= 0)
            {
                _charge = 0f;
                return;
            }

            if (commands.FireHeld)
            {
                _charge = Mathf.Min(1f, _charge + deltaTime / Mathf.Max(0.05f, N[WeaponStat.Extra1]));
                if (_charge >= 1f) _held += deltaTime;

                Ctx.Audio?.Loop(SoundId.RailCharge, true, 0.35f + 0.4f * _charge, 0.6f + 1.2f * _charge);
                return;
            }

            Ctx.Audio?.Loop(SoundId.RailCharge, false);

            if (_charge >= 1f) Fire();

            _charge = 0f;
            _held = 0f;
        }

        public override void Holster()
        {
            base.Holster();

            _charge = 0f;
            _held = 0f;
            Ctx.Audio?.Loop(SoundId.RailCharge, false);
        }

        public override void TickBackground(float deltaTime)
        {
            if (_charges < N.Magazine)
            {
                _cooldown -= deltaTime;

                if (_cooldown <= 0f)
                {
                    _charges++;
                    _cooldown = _charges < N.Magazine ? N.Reload : 0f;
                }
            }

            UpdateDrill(deltaTime);
        }

        private void Spend()
        {
            _charges--;
            if (_cooldown <= 0f) _cooldown = N.Reload;
        }

        private void Fire()
        {
            Spend();
            Shots++;

            var eye = Eye;
            var range = N.Range;
            var length = Ctx.RayToRock(eye.position, eye.forward, range, out var rock) ? rock.distance : range;

            var overcharge = _held >= OverchargeTime && N[WeaponStat.Extra2] > 1f ? N[WeaponStat.Extra2] : 1f;

            var spec = HitSpec.Ray(eye.position, eye.forward, length, N.Area, N.Damage * overcharge, Source,
                HitKind.Explosion);
            spec.GibShare = 1f;
            spec.Knockback = N[WeaponStat.Knockback];
            spec.Impulse = 16f;
            spec.CritElites = false;

            var report = Strike(spec);
            SprayGibs(eye.position + eye.forward * length * 0.5f, Vector3.up);

            var muzzle = View.TransformPoint(GunModels.RailMuzzle);
            var end = eye.position + eye.forward * length;

            Ctx.Fx?.Line(muzzle, end, 0.22f * overcharge, new Color(0.9f, 0.97f, 1f, 1f), new Color(0.6f, 0.85f, 1f, 0.8f), 0.4f);
            Ctx.Fx?.Line(muzzle, end, 0.6f * overcharge, new Color(0.3f, 0.6f, 1f, 0.35f), new Color(0.2f, 0.4f, 1f, 0.15f), 0.25f);

            if (rock.collider != null && length < range)
            {
                Ctx.Marks?.Scorch(rock.point, 0.7f);
                Ctx.Explosions?.Impact(rock.point, rock.normal, 18);
                Ctx.Explosions?.Light(rock.point + rock.normal * 0.5f, 30f, 10f);
            }

            Ctx.Explosions?.Light(muzzle + eye.forward, 45f, 14f);
            Ctx.Audio?.Play(SoundId.RailFire, 1f, overcharge > 1f ? 0.85f : 1f, null, 6);

            Motion.Kick = 1.6f;
            Ctx.Feedback.Kick(6f);
            Ctx.Feedback.Shake(0.6f);
            Ctx.Feedback.HitStop(0.08f);
            Ctx.Rig?.AddFovPunch(9f);

            if (report.Killed > 0) Ctx.Audio?.Play(SoundId.KillTick, 0.6f);
        }

        // ------------------------------------------------------------------ отвёртка

        private void FireBolt()
        {
            Spend();

            var eye = Eye;
            var range = N.Range;
            var length = Ctx.RayToRock(eye.position, eye.forward, range, out var rock) ? rock.distance : range;

            _drillQueen = false;
            _drillSolo = null;
            _drillIndex = -1;

            // Первая цель по лучу: Матка, одиночка или особь толпы.
            var probe = HitSpec.Ray(eye.position, eye.forward, length, 0.6f, 0f, Source);

            if (Ctx.Queen != null && Ctx.Queen.IsVulnerable && CombatContext.Touches(probe, Ctx.Queen.Centre, Ctx.Queen.HitRadius))
            {
                _drillQueen = true;
                _drillPoint = Ctx.Queen.Centre;
            }
            else
            {
                foreach (var solo in SoloTargets.All)
                {
                    if (solo == null || !solo.IsAlive || !CombatContext.Touches(probe, solo.Centre, solo.HitRadius)) continue;

                    _drillSolo = solo;
                    _drillPoint = solo.Centre;
                    break;
                }

                if (_drillSolo == null && Ctx.Crowd != null)
                {
                    Ctx.Crowd.FindNearest(eye.position, length, 12, _targets, eye.forward, 3f);

                    foreach (var index in _targets)
                    {
                        if (!Ctx.Crowd.TryGetTarget(index, out var position, out _, out _)) continue;

                        _drillIndex = index;
                        _drillPoint = position;
                        break;
                    }
                }
            }

            var hitSomething = _drillQueen || _drillSolo != null || _drillIndex >= 0;

            _drillLeft = hitSomething ? N[WeaponStat.AltDuration] : 0f;
            _drillTick = 0f;

            var end = hitSomething ? _drillPoint : eye.position + eye.forward * length;

            Ctx.Fx?.Line(View.TransformPoint(GunModels.RailMuzzle), end, 0.06f, new Color(0.7f, 0.9f, 1f, 0.9f),
                new Color(0.4f, 0.7f, 1f, 0.5f), 0.2f);

            Ctx.Audio?.Play(SoundId.RevolverFire, 0.8f, 0.6f);
            Ctx.Feedback.Kick(2.5f);
        }

        private void UpdateDrill(float deltaTime)
        {
            if (_drillLeft <= 0f) return;

            _drillLeft -= deltaTime;
            _drillTick -= deltaTime;

            if (_drillTick > 0f) return;

            _drillTick = DrillTick;

            var damage = N[WeaponStat.AltDamage] * DrillTick;

            if (_drillQueen)
            {
                // Мимо площадного множителя Матки — ради этого отвёртка и есть.
                if (Ctx.Queen == null || !Ctx.Queen.IsVulnerable) { _drillLeft = 0f; return; }

                DamageDealt += Ctx.Queen.TakeHit(damage);
                _drillPoint = Ctx.Queen.Centre;
            }
            else if (_drillSolo != null)
            {
                if (!_drillSolo.IsAlive) { _drillLeft = 0f; return; }

                DamageDealt += _drillSolo.TakeHit(damage, HitKind.Bullet, Source, Eye.position);
                _drillPoint = _drillSolo.Centre;
            }
            else if (_drillIndex >= 0 && Ctx.Crowd != null)
            {
                if (!Ctx.Crowd.TryGetTarget(_drillIndex, out var position, out _, out _)) { _drillLeft = 0f; return; }

                _targets.Clear();
                _shares.Clear();
                _targets.Add(_drillIndex);
                _shares.Add(1f);

                var spec = HitSpec.Sphere(position, 0.5f, damage, Source, HitKind.Bullet);
                var report = Ctx.Crowd.HitIndices(_targets, _shares, spec);

                Kills += report.Killed;
                DamageDealt += report.Dealt;
                _drillPoint = position;
            }

            Ctx.Explosions?.Impact(_drillPoint, (Eye.position - _drillPoint).normalized, 3);
            Ctx.Audio?.Play(SoundId.Zap, 0.3f, 1.6f, _drillPoint, 2);
        }

        // ------------------------------------------------------------------ модель

        public override void TickView(float deltaTime)
        {
            if (_body != null)
            {
                // Катушки светятся зарядом — по ним видно, готов ли выстрел, не глядя в интерфейс.
                var ready = _charges > 0 ? 0.15f : 0f;
                var glow = new Color(0.4f, 0.75f, 1.6f) * (ready + _charge * _charge * 2.5f);

                _body.GetPropertyBlock(_block, 3);
                _block.SetColor(EmissionId, glow);
                _body.SetPropertyBlock(_block, 3);
            }

            var shiver = _charge >= 1f ? Mathf.Sin(Ctx.Time * 70f) * 0.002f : 0f;
            Motion.ExtraPosition = new Vector3(shiver, shiver, -0.02f * _charge);
            Motion.Reload = _charges <= 0 ? 0.35f : 0f;

            base.TickView(deltaTime);
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = _charges,
            Capacity = N.Magazine,
            Gauge = _charge,
            Reload = _charges < N.Magazine ? 1f - _cooldown / Mathf.Max(0.01f, N.Reload) : 0f,
            AltUnlocked = AltUnlocked,
            AltReady = _drillLeft > 0f ? _drillLeft / Mathf.Max(0.01f, N[WeaponStat.AltDuration]) : (_charges > 0 ? 1f : 0f),
            AltCharges = AltUnlocked ? _charges : 0
        };
    }
}
