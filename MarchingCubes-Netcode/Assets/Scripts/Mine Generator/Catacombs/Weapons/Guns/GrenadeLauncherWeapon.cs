using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Барабан» — многозарядный гранатомёт. Переехал из CaveGrenadeLauncher без изменения
    /// поведения: 6 гранат, выстрел раз в 0.32 с, перезарядка 2.1 с (пустой барабан —
    /// сам), граната 30 юнитов в секунду почти прямо, дуга под перекрестие, взрыватель
    /// по близости 0.9, радиус 5, урон 30 HP, воронка и гарь.
    ///
    /// Роль — отход по коридору: орда бежит по тоннелю, игрок пятится и кладёт гранаты в гущу.
    ///
    /// Альт «Липучка» (ур. 3): граната прилипает к камню и ждёт; нажатие альта, когда липучек
    /// уже предел (или удержание альта), взрывает все. Ловушка на отходе: заминировал развилку,
    /// отошёл, подорвал под толпой.
    ///
    /// Эволюция «Кассета» (ур. 5 + Охват 3): каждый взрыв разбрасывает пять прыгающих бомбочек.
    /// </summary>
    public sealed class GrenadeLauncherWeapon : Weapon
    {
        private const int KindGrenade = 0;
        private const int KindSticky = 1;
        private const int KindBomblet = 2;

        /// <summary>Сколько секунд держать альт, чтобы подорвать липучки, не дожидаясь предела.</summary>
        private const float HoldToDetonate = 0.35f;

        private int _ammo;
        private float _nextShot;
        private float _reloadLeft;
        private float _altHeld;
        private bool _altConsumed;

        private Transform _drum;
        private float _drumAngle;
        private float _drumTarget;
        private Renderer _flash;
        private Transform _flashTransform;
        private float _flashLeft;

        private int _grenadeVisual = -1;
        private int _stickyVisual = -1;
        private int _bombletVisual = -1;

        private readonly List<Shot> _owned = new List<Shot>();

        public int Ammo => _ammo;
        public bool IsReloading => _reloadLeft > 0f;

        /// <summary>Сколько гранат выпущено — для прогонов.</summary>
        public int Fired { get; private set; }

        protected override void OnNumbersChanged()
        {
            if (_ammo > N.Magazine) _ammo = N.Magazine;
        }

        protected override void Build(Transform view)
        {
            _ammo = N.Magazine;

            var m = Ctx.Materials;

            var metal = m.Lit("Launcher Metal", new Color(0.26f, 0.26f, 0.28f), 0.45f, 0.55f);
            var polymer = m.Lit("Launcher Polymer", new Color(0.3f, 0.3f, 0.21f), 0f, 0.35f);
            var dark = m.Lit("Launcher Dark", new Color(0.025f, 0.025f, 0.025f), 0.3f, 0.2f);
            var brass = m.Lit("Launcher Brass", new Color(0.74f, 0.54f, 0.22f), 1f, 0.62f);
            var grenadeBody = m.Lit("Grenade Body", new Color(0.25f, 0.28f, 0.14f), 0.2f, 0.35f);
            var stickyBody = m.Lit("Sticky Body", new Color(0.55f, 0.18f, 0.08f), 0.2f, 0.4f, new Color(0.6f, 0.1f, 0.02f));

            // Чуть мельче натуральной величины: в натуральную барабан у правого нижнего угла
            // занимал четверть кадра.
            view.localScale = Vector3.one * 0.88f;

            m.Part(view, "Body", m.Own(LauncherModel.BuildBody()), metal, polymer, dark);

            _drum = m.Part(view, "Drum", m.Own(LauncherModel.BuildDrum()), metal, brass, dark).transform;

            // Точка коллиматора: светится сама, видна игроку сзади сквозь рамку.
            var dotMaterial = m.Glow("Sight Dot", m.SoftDot, new Color(1f, 0.15f, 0.1f, 1f));
            var dot = m.Part(view, "Sight Dot", m.Own(LauncherModel.BuildQuad()), dotMaterial);
            dot.transform.localPosition = LauncherModel.SightDot;
            dot.transform.localScale = Vector3.one * 0.014f;

            // Вспышка у дула: язык пламени вдоль ствола и звезда поперёк.
            var flashMaterial = m.Glow("Muzzle Flash", m.Paint("Muzzle Flash", 64, (u, v, r) =>
            {
                var along = (v + 1f) * 0.5f;
                var width = Mathf.Lerp(0.9f, 0.1f, along);
                var body = Mathf.Clamp01(1f - Mathf.Abs(u) / width) * Mathf.Clamp01(1f - along);
                var star = Mathf.Clamp01(1f - r) * (0.5f + 0.5f * Mathf.Abs(Mathf.Cos(Mathf.Atan2(v, u) * 3f)));
                var a = Mathf.Clamp01(Mathf.Max(body, star * star));

                return new Color(1f, 0.85f + 0.15f * a, 0.6f + 0.4f * a, a);
            }), new Color(1f, 0.72f, 0.38f, 1f));

            _flash = m.Part(view, "Muzzle Flash", m.Own(LauncherModel.BuildMuzzleFlash()), flashMaterial);
            _flashTransform = _flash.transform;
            _flashTransform.localPosition = LauncherModel.Muzzle;
            _flash.enabled = false;

            var grenadeMesh = m.Own(LauncherModel.BuildGrenade());

            if (Ctx.Projectiles != null)
            {
                // След: горячий у гранаты, остывает и тает — по нему видно, куда ушёл выстрел.
                _grenadeVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = grenadeMesh,
                    Materials = new[] { grenadeBody, brass },
                    Scale = 1.6f,
                    StreakHead = new Color(1f, 0.75f, 0.4f, 0.9f),
                    StreakTail = new Color(0.6f, 0.25f, 0.1f, 0f),
                    StreakLength = 2.2f,
                    StreakWidth = 0.09f
                });

                _stickyVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = grenadeMesh,
                    Materials = new[] { stickyBody, brass },
                    Scale = 1.6f,
                    AlignToVelocity = true,
                    StreakHead = new Color(1f, 0.35f, 0.15f, 0.9f),
                    StreakTail = new Color(0.6f, 0.1f, 0.05f, 0f),
                    StreakLength = 1.6f,
                    StreakWidth = 0.08f
                });

                _bombletVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = grenadeMesh,
                    Materials = new[] { grenadeBody, brass },
                    Scale = 0.9f,
                    StreakHead = new Color(1f, 0.6f, 0.3f, 0.8f),
                    StreakTail = new Color(0.5f, 0.2f, 0.1f, 0f),
                    StreakLength = 0.8f,
                    StreakWidth = 0.05f
                });
            }
        }

        // ------------------------------------------------------------------ огонь

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (_reloadLeft > 0f) return;

            if (commands.Reload && _ammo < N.Magazine)
            {
                StartReload();
                return;
            }

            if (AltUnlocked) UpdateAlt(commands, deltaTime);

            if (commands.FireHeld && Ctx.Time >= _nextShot)
            {
                if (_ammo > 0) Fire(KindGrenade);
                else if (commands.FirePressed) Ctx.Audio?.Play(SoundId.Empty, 0.6f);
            }
        }

        public override void TickBackground(float deltaTime)
        {
            if (_reloadLeft > 0f)
            {
                _reloadLeft -= deltaTime;

                // Барабан наполняется ближе к концу — когда гранатомёт уже возвращается в руку.
                if (_reloadLeft <= N.Reload * 0.3f) _ammo = N.Magazine;
                if (_reloadLeft <= 0f) _reloadLeft = 0f;
            }

            // Пустой барабан перезаряжается сам: считать выстрелы посреди роя игроку незачем.
            if (_ammo == 0 && _reloadLeft <= 0f && Ctx.Time >= _nextShot) StartReload();
        }

        private void UpdateAlt(in PlayerCommands commands, float deltaTime)
        {
            if (commands.AltHeld)
            {
                _altHeld += deltaTime;

                if (!_altConsumed && _altHeld >= HoldToDetonate && CountStickies() > 0)
                {
                    DetonateStickies();
                    _altConsumed = true;
                }

                return;
            }

            // Отпустил коротким нажатием: липучек предел — подрыв, иначе — новая липучка.
            if (_altHeld > 0f && !_altConsumed)
            {
                if (CountStickies() >= Mathf.RoundToInt(N[WeaponStat.AltCharges])) DetonateStickies();
                else if (_ammo > 0 && Ctx.Time >= _nextShot) Fire(KindSticky);
            }

            _altHeld = 0f;
            _altConsumed = false;
        }

        private void Fire(int kind)
        {
            _ammo--;
            Fired++;
            _nextShot = Ctx.Time + 1f / N.Rate;
            Motion.Kick = 1f;
            _drumTarget += 60f;

            var eye = Eye;
            var muzzle = View.TransformPoint(LauncherModel.Muzzle);

            var aim = Ctx.AimPoint(80f, out var onRock, out _);
            if (!onRock) aim = eye.position + eye.forward * 50f;

            // Ствол у самой стены может оказаться в породе — тогда граната выходит из глаз.
            var start = Physics.Linecast(eye.position, muzzle, out _, ~0, QueryTriggerInteraction.Ignore)
                ? eye.position + eye.forward * 0.25f
                : muzzle;

            var toAim = aim - start;
            var distance = Mathf.Max(0.5f, toAim.magnitude);
            var speed = N[WeaponStat.Speed];
            var gravity = N[WeaponStat.Extra1];

            // Дуга под перекрестие: подброс ровно на столько, сколько граната просядет за время
            // полёта до точки прицела. Игрок целится перекрестием, а не поправками на дальность.
            var flight = Mathf.Min(distance / speed, 1.2f);
            var velocity = toAim / distance * speed + Vector3.up * (0.5f * gravity * flight);

            if (Ctx.Projectiles != null)
            {
                var sticky = kind == KindSticky;

                Ctx.Projectiles.Launch(new Shot
                {
                    Owner = ShotOwner,
                    Visual = sticky ? _stickyVisual : _grenadeVisual,
                    Flags = sticky ? ShotFlags.None : ShotFlags.NearSpiders | ShotFlags.NearBosses,
                    Position = start,
                    Velocity = velocity,
                    Gravity = gravity,
                    Radius = 0.06f,
                    Proximity = N[WeaponStat.Extra2],
                    Life = sticky ? 60f : N[WeaponStat.Range],
                    Tag = kind
                });
            }

            _flashLeft = 0.055f;
            _flashTransform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            _flashTransform.localScale = Vector3.one * Random.Range(0.13f, 0.19f);

            Ctx.Explosions?.MuzzleLight(muzzle + eye.forward * 0.3f);
            Ctx.Audio?.Play(SoundId.LauncherFire, 0.9f, kind == KindSticky ? 1.15f : 1f, null, 5);

            Ctx.Feedback.Kick(2.4f);
            Ctx.Feedback.Shake(0.12f);
        }

        private void StartReload()
        {
            if (_reloadLeft > 0f || _ammo >= N.Magazine) return;

            _reloadLeft = N.Reload;
            _drumTarget += 720f;

            Ctx.Audio?.Play(SoundId.Reload, 0.7f);
        }

        private int CountStickies()
        {
            if (Ctx.Projectiles == null) return 0;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            var count = 0;
            foreach (var shot in _owned) if (shot.Tag == KindSticky) count++;

            return count;
        }

        private void DetonateStickies()
        {
            if (Ctx.Projectiles == null) return;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            foreach (var shot in _owned)
            {
                if (shot.Tag != KindSticky) continue;

                Ctx.Projectiles.Kill(shot.Id);
                Detonate(shot.Position + shot.Normal * 0.12f, shot.IsStuck ? shot.Normal : Vector3.up);
            }
        }

        // ------------------------------------------------------------------ снаряды

        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            switch (shot.Tag)
            {
                case KindSticky:
                    // Липучка не рвётся сама: ни о камень, ни у пауков — она ждёт подрыва.
                    if (what == ShotEvent.HitRock)
                    {
                        shot.Flags |= ShotFlags.Stuck;
                        shot.Velocity = Vector3.zero;
                        shot.Normal = hit.normal;
                    }
                    else if (what == ShotEvent.Expired)
                    {
                        Detonate(shot.Position, Vector3.up);
                    }

                    return;

                case KindBomblet:
                    // Бомбочка кассеты: отскакивает раз, на втором касании рвётся.
                    if (what == ShotEvent.HitRock && shot.Payload > 0f)
                    {
                        shot.Payload = 0f;
                        shot.Velocity = Vector3.Reflect(shot.Velocity, hit.normal) * 0.5f;
                        shot.Position = hit.point + hit.normal * 0.15f;
                        return;
                    }

                    shot.Dead = true;
                    Blast(Ctx, shot.Position + (what == ShotEvent.HitRock ? hit.normal * 0.12f : Vector3.zero),
                        what == ShotEvent.HitRock ? hit.normal : Vector3.up, N.Area * 0.45f, N.Damage * 0.4f,
                        Source, Gibs, false, this);
                    return;
            }

            shot.Dead = true;

            if (what == ShotEvent.HitRock) Detonate(hit.point + hit.normal * 0.12f, hit.normal);
            else Detonate(shot.Position, -shot.Velocity.normalized);
        }

        /// <summary>
        /// Взрыв гранаты в точке: воронка, паутина, пауки, Матка, одиночки, эффекты, тряска.
        /// Публичный — им же пользуется отладочный взрыв стенда на клавишу B (через арсенал).
        /// </summary>
        public int Detonate(Vector3 point, Vector3 normal)
        {
            var report = Blast(Ctx, point, normal, N.Area, N.Damage, Source, Gibs, true, this);

            // Кассета: пять прыгающих бомбочек во все стороны от камня.
            if (Evolved && Ctx.Projectiles != null && N[WeaponStat.Projectiles] > 0f)
            {
                var count = Mathf.RoundToInt(N[WeaponStat.Projectiles]);

                for (var i = 0; i < count; i++)
                {
                    var direction = (normal + Random.insideUnitSphere * 0.9f).normalized;

                    Ctx.Projectiles.Launch(new Shot
                    {
                        Owner = ShotOwner,
                        Visual = _bombletVisual,
                        Flags = ShotFlags.NearSpiders,
                        Position = point + normal * 0.3f,
                        Velocity = direction * Random.Range(6f, 10f) + Vector3.up * 3f,
                        Gravity = 18f,
                        Radius = 0.05f,
                        Proximity = 0.6f,
                        Life = Random.Range(0.8f, 1.3f),
                        Payload = 1f,
                        Tag = KindBomblet
                    });
                }
            }

            return report.Killed;
        }

        /// <summary>
        /// Сам взрыв, без гранаты: тот же путь для гранаты, бомбочки кассеты и отладочной B.
        /// </summary>
        public static HitReport Blast(CombatContext ctx, Vector3 point, Vector3 normal, float radius, float damage,
            DamageSource source, List<Vector3> gibs, bool crater, Weapon owner)
        {
            // Воронка — первой: разорванные меряют высоту падения кусков по плотности,
            // и куски должны лечь уже на дно ямы, а не повиснуть над ним.
            if (crater && ctx.Marks != null) ctx.Marks.Blast(point);

            CaveWebs.TearAt(point, radius);

            var spec = HitSpec.Sphere(point, radius, damage, source);
            spec.GibShare = ctx.Crowd != null ? ctx.Crowd.GibShare : 0.65f;

            var report = owner != null ? ((GrenadeLauncherWeapon)owner).StrikeAs(spec) : ctx.Strike(spec, gibs);

            if (ctx.Explosions != null)
            {
                ctx.Explosions.Explode(point, normal, radius);

                // Брызг не больше четырёх десятков на взрыв: по плотной куче рвёт и сотню.
                var count = Mathf.Min(gibs.Count, 40);

                for (var i = 0; i < count; i++)
                {
                    var away = (gibs[i] - point).normalized + normal * 0.6f;
                    ctx.Explosions.Gore(gibs[i], away);
                }
            }

            ctx.Audio?.Play(SoundId.Explosion, Mathf.Clamp(radius / 5f, 0.4f, 1.2f), Random.Range(0.92f, 1.05f), point, 4);

            // Тряска по удалённости: у самого игрока — сильно, на дистанции боя — толчок.
            if (ctx.Eye != null)
            {
                var distance = (point - ctx.Eye.position).magnitude;
                ctx.Feedback.Shake(Mathf.Clamp01(1.2f - distance / 22f) * 1.1f * Mathf.Clamp(radius / 5f, 0.4f, 1.2f));
            }

            return report;
        }

        private HitReport StrikeAs(in HitSpec spec) => Strike(spec);

        // ------------------------------------------------------------------ модель

        public override void TickView(float deltaTime)
        {
            var dt = Mathf.Max(deltaTime, 1e-4f);

            _drumAngle = Mathf.Lerp(_drumAngle, _drumTarget, dt * (_reloadLeft > 0f ? 5f : 22f));
            if (_drum != null) _drum.localRotation = Quaternion.Euler(0f, 0f, _drumAngle);

            // Перезарядка: гранатомёт уходит вниз и заваливается набок, барабан крутится.
            Motion.Reload = _reloadLeft > 0f ? Mathf.Sin(Mathf.PI * (1f - _reloadLeft / N.Reload)) : 0f;

            base.TickView(deltaTime);

            _flashLeft -= dt;
            if (_flash != null) _flash.enabled = _flashLeft > 0f;
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = _ammo,
            Capacity = N.Magazine,
            Gauge = -1f,
            Reload = _reloadLeft > 0f ? 1f - _reloadLeft / N.Reload : 0f,
            AltUnlocked = AltUnlocked,
            AltReady = 1f,
            AltCharges = AltUnlocked ? Mathf.Max(0, Mathf.RoundToInt(N[WeaponStat.AltCharges]) - CountStickies()) : 0
        };
    }
}
