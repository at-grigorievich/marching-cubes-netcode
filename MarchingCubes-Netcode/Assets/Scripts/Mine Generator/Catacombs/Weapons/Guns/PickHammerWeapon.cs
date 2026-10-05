using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Забой» — кирка-молот, ближний бой в духе Vermintide 2, и возвращение кирки: копание,
    /// заявленная механика проекта, выключенная в стенде, становится частью пушки.
    ///
    /// Огонь: серия из трёх взмахов по 0.38 с, дуга 110°, досягаемость 3, урон 40/30/55,
    /// рассечение до шести целей (каждая следующая −15%), оглушение 0.3 с, отброс 5. Убийство —
    /// 1.5 временного здоровья. Стоп-кадр 40 мс при попадании по трём и больше.
    ///
    /// Удержание — удар оземь (замах 0.6 с): волна радиусом 4.5, 90 HP в центре, отброс 14 —
    /// и воронка в полу тем же трактом, что у гранаты. Не чаще раза в 1.2 с.
    ///
    /// Альт «Пробой» (ур. 3, откат 8 с): выпад на 4 юнита, прокапывающий тонкую стену до трёх
    /// юнитов породы — свой короткий ход из окружения. Первые 0.2 с — парирование: плевок
    /// Матки или особого паука летит обратно с тройным уроном, +10 здоровья.
    ///
    /// Эволюция «Землетряс» (ур. 5 + Живучесть 3): удар оземь раскалывает пол трещиной на 8 юнитов.
    ///
    /// Копание только ДОБАВЛЯЕТ связность (гарантия «тупиков 0» не ломается); пилоны и логово
    /// копанию не подлежат — сфера-запрет вокруг них.
    /// </summary>
    public sealed class PickHammerWeapon : Weapon
    {
        private static readonly float[] ComboDamage = { 1f, 0.75f, 1.375f };

        private const float HitMoment = 0.4f;
        private const float ComboWindow = 0.8f;
        private const float HoldForSlam = 0.25f;
        private const float SlamWindup = 0.6f;
        private const float SlamCooldown = 1.2f;
        private const float ParryWindow = 0.2f;
        private const float BreachDistance = 4f;
        private const float BreachDigRadius = 1.6f;
        private const float BreachMaxRock = 3f;
        private const float NoDigRadius = 6f;

        private int _combo;
        private float _swing = -1f;
        private bool _swingHit;
        private float _comboTimer;
        private float _held;
        private float _slamCharge = -1f;
        private float _slamCooldown;
        private float _breachCooldown;
        private float _parryLeft;
        private float _freeBreachTimer;

        private Transform _head;

        public int Slams { get; private set; }
        public int Breaches { get; private set; }
        public int Parries { get; private set; }

        /// <summary>Сколько юнитов породы прокопал пробой за забег — для прогона.</summary>
        public float RockDug { get; private set; }

        protected override void Build(Transform view)
        {
            var m = Ctx.Materials;

            var metal = m.Lit("Hammer Metal", new Color(0.32f, 0.3f, 0.28f), 0.6f, 0.45f);
            var wood = m.Lit("Hammer Wood", new Color(0.35f, 0.2f, 0.1f), 0f, 0.35f);
            var dark = m.Lit("Hammer Dark", new Color(0.05f, 0.05f, 0.05f), 0.4f, 0.3f);
            var wrap = m.Lit("Hammer Wrap", new Color(0.15f, 0.12f, 0.1f), 0f, 0.2f);

            _head = m.Part(view, "Hammer", m.Own(GunModels.Hammer()), metal, wood, dark, wrap).transform;

            Motion.Offset = new Vector3(0.28f, -0.42f, 0.38f);
            Motion.Sway = 0.8f;
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (AltUnlocked && commands.AltPressed && (_breachCooldown <= 0f || FreeBreachReady)) Breach();

            // Удар оземь: держать огонь — замах, отпустить или дождаться конца — удар.
            if (_slamCharge >= 0f)
            {
                _slamCharge += deltaTime;
                if (_slamCharge >= SlamWindup) Slam();
                return;
            }

            if (commands.FireHeld)
            {
                _held += deltaTime;

                if (_held >= HoldForSlam && _swing < 0f && _slamCooldown <= 0f)
                {
                    _slamCharge = 0f;
                    Ctx.Audio?.Play(SoundId.Swing, 0.5f, 0.6f);
                    return;
                }
            }
            else
            {
                // Короткое нажатие — взмах серии.
                if (_held > 0f && _held < HoldForSlam && _swing < 0f) StartSwing();
                _held = 0f;
            }
        }

        public override void TickBackground(float deltaTime)
        {
            _slamCooldown = Cooldown(_slamCooldown, deltaTime);
            _breachCooldown = Cooldown(_breachCooldown, deltaTime);
            _freeBreachTimer = Mathf.Max(0f, _freeBreachTimer - deltaTime);

            if (_parryLeft > 0f)
            {
                _parryLeft -= deltaTime;
                Parry();
            }

            if (_swing >= 0f)
            {
                _swing += deltaTime * N.Rate;

                if (!_swingHit && _swing >= HitMoment)
                {
                    _swingHit = true;
                    SwingHit();
                }

                if (_swing >= 1f)
                {
                    _swing = -1f;
                    _comboTimer = ComboWindow;
                }
            }
            else if (_comboTimer > 0f)
            {
                _comboTimer -= deltaTime;
                if (_comboTimer <= 0f) _combo = 0;
            }
        }

        /// <summary>Класс «Шахтёр»: пробой без отката раз в 30 с.</summary>
        private bool FreeBreachReady => Ctx.Stats != null && Ctx.Stats.Has(Stat.FreeBreach) && _freeBreachTimer <= 0f;

        // ------------------------------------------------------------------ серия

        private void StartSwing()
        {
            _swing = 0f;
            _swingHit = false;

            Ctx.Audio?.Play(SoundId.Swing, 0.7f, 1f + _combo * 0.08f);
        }

        private void SwingHit()
        {
            var eye = Eye;
            var damage = N.Damage * ComboDamage[_combo];

            var spec = HitSpec.Cone(eye.position - eye.forward * 0.3f, eye.forward, N.Range + 0.3f, N.Area, damage,
                Source, HitKind.Melee);
            spec.MaxTargets = Mathf.Max(1, Mathf.RoundToInt(N[WeaponStat.Pierce]));
            spec.PierceFalloff = 0.15f;
            spec.Knockback = N[WeaponStat.Knockback];
            spec.Stun = N[WeaponStat.Status];
            spec.Impulse = 10f;
            spec.GibShare = _combo == 2 ? 1f : 0f;
            spec.ThroughWalls = true;

            var report = Strike(spec);
            SprayGibs(eye.position + eye.forward * 1.5f, Vector3.up);

            if (report.Any)
            {
                Ctx.Audio?.Play(SoundId.Crunch, 0.9f, 1f - _combo * 0.05f, null, 5);
                Ctx.Feedback.Shake(0.15f + 0.05f * report.Hits);
                if (report.Hits >= 3) Ctx.Feedback.HitStop(0.04f);
            }
            else if (Ctx.RayToRock(eye.position, eye.forward, N.Range, out var rock))
            {
                // Кирка по камню — искры и пыль: промах по пустому ходу должен звучать иначе.
                Ctx.Explosions?.Impact(rock.point, rock.normal, 10);
                Ctx.Audio?.Play(SoundId.Crunch, 0.5f, 1.4f);
            }

            Ctx.Feedback.Kick(1.5f + _combo * 0.5f);
            _combo = (_combo + 1) % ComboDamage.Length;
        }

        // ------------------------------------------------------------------ удар оземь

        private void Slam()
        {
            _slamCharge = -1f;
            _slamCooldown = SlamCooldown;
            _held = 0f;
            Slams++;

            var body = Ctx.Body;
            body.GetBodySegment(out var low, out _, out var bodyRadius);

            var ground = Ctx.RayToRock(low, Vector3.down, 3f, out var floor) ? floor.point : low - Vector3.up * 0.9f;
            var radius = N[WeaponStat.Extra1];

            SlamAt(ground, Vector3.up, radius, N[WeaponStat.AltDamage], true);

            // Землетряс: трещина вперёд — цепочка ударов по полу, каждый со своей воронкой.
            var crack = N[WeaponStat.Extra2];

            if (Evolved && crack > 0f)
            {
                var forward = Vector3.ProjectOnPlane(Eye.forward, Vector3.up).normalized;

                for (var d = 2f; d <= crack; d += 2f)
                {
                    var probe = ground + forward * d + Vector3.up * 1.2f;
                    if (Ctx.World != null && Ctx.World.IsSolid(probe)) break;

                    var point = Ctx.RayToRock(probe, Vector3.down, 3f, out var hit) ? hit.point : probe - Vector3.up * 1.2f;

                    SlamAt(point, Vector3.up, radius * 0.55f, N[WeaponStat.AltDamage] * 0.6f, CanDig(point));
                }
            }

            Ctx.Feedback.Shake(0.8f);
            Ctx.Feedback.HitStop(0.06f);
            Ctx.Feedback.Kick(-3f);
            Ctx.Audio?.Play(SoundId.Slam, 1f, 1f, null, 6);
        }

        private void SlamAt(Vector3 point, Vector3 normal, float radius, float damage, bool dig)
        {
            if (dig && CanDig(point) && Ctx.Marks != null) Ctx.Marks.Blast(point + normal * 0.3f);

            var spec = HitSpec.Sphere(point + normal * 0.5f, radius, damage, Source, HitKind.Melee);
            spec.FullShare = 0.3f;
            spec.MinShare = 0.35f;
            spec.Knockback = 14f;
            spec.Stun = 0.5f;
            spec.GibShare = 0.4f;

            Strike(spec);
            SprayGibs(point, normal);

            Ctx.Explosions?.Dust(point, normal, radius);
        }

        // ------------------------------------------------------------------ пробой и парирование

        private void Breach()
        {
            if (FreeBreachReady && _breachCooldown > 0f) _freeBreachTimer = 30f;
            else _breachCooldown = N[WeaponStat.AltCooldown];

            _parryLeft = ParryWindow;
            Breaches++;

            var eye = Eye;
            var forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f) forward = eye.forward;

            // Прокопать то, что впереди: цепочка сфер, пока не кончится камень или три юнита породы.
            var origin = Ctx.Body.BodyCentre;
            var rock = 0f;

            for (var d = 0.8f; d <= BreachDistance + 0.5f && rock < BreachMaxRock; d += 0.8f)
            {
                var point = origin + forward * d;

                if (Ctx.World == null || !Ctx.World.IsSolid(point)) continue;
                if (!CanDig(point)) break;

                if (Ctx.Marks != null && Ctx.Marks.Dig(point, BreachDigRadius))
                {
                    rock += 0.8f;
                    Ctx.Explosions?.Dust(point, -forward, 2f);
                }
            }

            RockDug += rock;

            Ctx.Rig?.Lunge(forward, BreachDistance, 0.25f);

            var spec = HitSpec.Cone(origin, forward, BreachDistance + 1f, 40f, N.Damage * 1.5f, Source, HitKind.Melee);
            spec.Knockback = 12f;
            spec.Stun = 0.6f;
            spec.ThroughWalls = true;

            Strike(spec);

            Ctx.Audio?.Play(SoundId.Swing, 1f, 0.8f, null, 6);
            Ctx.Audio?.Play(rock > 0f ? SoundId.Slam : SoundId.Dash, 0.8f, 1.2f);
            Ctx.Feedback.Shake(0.4f);
        }

        /// <summary>Пилоны Матки и её логово копанию не подлежат: реквизит над ямой висел бы в воздухе.</summary>
        private bool CanDig(Vector3 point)
        {
            var queen = Ctx.Queen;
            if (queen == null) return true;

            if (queen.HasLair && (queen.LairPosition - point).sqrMagnitude < NoDigRadius * NoDigRadius) return false;

            foreach (var pylon in queen.PylonPositions)
            {
                if ((pylon - point).sqrMagnitude < NoDigRadius * NoDigRadius) return false;
            }

            return true;
        }

        private void Parry()
        {
            if (Ctx.Projectiles == null) return;

            var eye = Eye;
            var parried = Ctx.Projectiles.Parry(eye.position + eye.forward, 2.5f, eye.forward, ShotOwner, 1.5f);

            if (parried <= 0) return;

            Parries += parried;
            Ctx.Rig?.Vitals?.Heal(10f * parried);

            Ctx.Audio?.Play(SoundId.Parry, 1f, 1f, null, 7);
            Ctx.Feedback.HitStop(0.08f);
            Ctx.Feedback.Shake(0.3f);
        }

        /// <summary>Отбитый плевок: бьёт втрое сильнее того, что нёс.</summary>
        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            shot.Dead = true;

            var damage = Mathf.Max(10f, shot.Payload) * 3f;
            var spec = HitSpec.Sphere(shot.Position, 1.6f, damage, Source, HitKind.Explosion);
            spec.CritElites = true;

            Strike(spec);
            Ctx.Explosions?.Impact(shot.Position, Vector3.up, 8);
        }

        // ------------------------------------------------------------------ модель

        public override void TickView(float deltaTime)
        {
            if (_head != null)
            {
                // Покой — молот на плече; взмах — дуга сверху вниз, третий — с разворотом.
                float pitch;
                float yaw;

                if (_slamCharge >= 0f)
                {
                    var k = Mathf.Clamp01(_slamCharge / SlamWindup);
                    pitch = Mathf.Lerp(-20f, -110f, k);
                    yaw = 0f;
                }
                else if (_swing >= 0f)
                {
                    var k = Mathf.SmoothStep(0f, 1f, _swing);
                    var side = _combo % 2 == 0 ? 1f : -1f;

                    pitch = Mathf.Lerp(-90f, 70f, k);
                    yaw = side * Mathf.Lerp(30f, -30f, k);
                }
                else
                {
                    pitch = -25f;
                    yaw = 10f;
                }

                _head.localRotation = Quaternion.Euler(pitch, yaw, -15f);
            }

            Motion.ExtraPosition = _parryLeft > 0f ? new Vector3(-0.05f, 0.05f, 0.15f) : Vector3.zero;

            base.TickView(deltaTime);
        }

        public override WeaponHud Hud => new WeaponHud
        {
            Id = Id,
            Level = Level,
            Evolved = Evolved,
            Ammo = -1,
            Gauge = _slamCharge >= 0f ? Mathf.Clamp01(_slamCharge / SlamWindup) : -1f,
            Reload = _slamCooldown > 0f ? 1f - _slamCooldown / SlamCooldown : 0f,
            AltUnlocked = AltUnlocked,
            AltReady = AltUnlocked ? 1f - _breachCooldown / Mathf.Max(0.01f, N[WeaponStat.AltCooldown]) : 0f,
            AltCharges = AltUnlocked && (_breachCooldown <= 0f || FreeBreachReady) ? 1 : 0
        };
    }
}
