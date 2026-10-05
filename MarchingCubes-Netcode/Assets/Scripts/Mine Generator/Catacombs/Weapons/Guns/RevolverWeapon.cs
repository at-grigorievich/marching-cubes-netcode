using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// «Монета» — револьвер (Marksman из Ultrakill). Снайпер по одиночкам: элита, особые, Матка.
    /// Единственная пушка, вознаграждающая точность.
    ///
    /// Огонь: мгновенный луч, 45 HP, пробивает трёх (каждый следующий на четверть слабее),
    /// 2.5 выстрела в секунду, 6 в барабане, перезарядка 1.2 с. По элите, особым и Матке —
    /// всегда крит ×2: «глаза» условные, без попадания в точку — иначе на телефоне пушка мертва.
    ///
    /// Альт «Монетка» (ур. 3): подбросить монету (4 заряда, по одной за 2.5 с). Выстрел в монету —
    /// рикошет в самую опасную цель в 30 юнитах на прямой видимости с уроном ×2.5; несколько
    /// монет в воздухе — пуля прыгает с монеты на монету, ×1.5 за каждую. Стоп-кадр и звон.
    /// На таче монета сама ловит выстрел через 0.35 с: фантазия остаётся, точность не нужна.
    ///
    /// Эволюция «Сплит» (ур. 5 + Меткость 3): рикошет с монеты делится на три цели.
    /// </summary>
    public sealed class RevolverWeapon : Weapon
    {
        /// <summary>Радиус монеты для попадания пулей; с помощью прицеливания вдвое больше.</summary>
        private const float CoinRadius = 0.6f;

        private const float CoinChainBonus = 1.5f;

        private int _ammo;
        private float _nextShot;
        private float _reloadLeft;
        private float _coins;
        private float _autoCoinShot = -1f;

        private int _coinVisual = -1;
        private Transform _cylinder;
        private float _cylinderAngle;
        private float _cylinderTarget;
        private float _hammer;
        private Transform _hammerTransform;

        private readonly List<Shot> _owned = new List<Shot>();
        private readonly List<int> _targets = new List<int>();
        private readonly List<float> _shares = new List<float>();

        /// <summary>Сколько рикошетов с монет за забег — для прогонов и заданий.</summary>
        public int Ricochets { get; private set; }

        protected override void OnNumbersChanged()
        {
            _ammo = Mathf.Min(_ammo, N.Magazine);
        }

        protected override void Build(Transform view)
        {
            _ammo = N.Magazine;
            _coins = N[WeaponStat.AltCharges];

            var m = Ctx.Materials;

            var metal = m.Lit("Revolver Metal", new Color(0.3f, 0.3f, 0.32f), 0.55f, 0.6f);
            var grip = m.Lit("Revolver Wood", new Color(0.28f, 0.16f, 0.08f), 0f, 0.4f);
            var dark = m.Lit("Revolver Dark", new Color(0.03f, 0.03f, 0.03f), 0.3f, 0.2f);
            var sight = m.Lit("Revolver Sight", new Color(0.9f, 0.85f, 0.4f), 0f, 0.5f, new Color(0.8f, 0.7f, 0.2f));

            view.localScale = Vector3.one * 1.1f;

            m.Part(view, "Body", m.Own(GunModels.Revolver()), metal, grip, dark, sight);

            _cylinder = m.Part(view, "Cylinder", m.Own(GunModels.RevolverCylinder()), metal, grip, dark).transform;
            _cylinder.localPosition = new Vector3(0f, 0.035f, 0.02f);

            _hammerTransform = m.Part(view, "Hammer", m.Own(BuildHammer()), metal).transform;
            _hammerTransform.localPosition = new Vector3(0f, 0.065f, -0.04f);

            Motion.Offset = new Vector3(0.18f, -0.17f, 0.32f);

            if (Ctx.Projectiles != null)
            {
                var gold = m.Lit("Coin Gold", new Color(0.95f, 0.75f, 0.3f), 1f, 0.8f, new Color(0.5f, 0.35f, 0.05f));

                _coinVisual = Ctx.Projectiles.AddVisual(new ShotVisual
                {
                    Mesh = m.Own(GunModels.Coin()),
                    Materials = new[] { gold },
                    Scale = 1.4f,
                    AlignToVelocity = false,
                    Spin = 900f,
                    StreakHead = new Color(1f, 0.85f, 0.4f, 0.6f),
                    StreakTail = new Color(1f, 0.6f, 0.2f, 0f),
                    StreakLength = 0.5f,
                    StreakWidth = 0.03f
                });
            }
        }

        private static Mesh BuildHammer()
        {
            var kit = new MeshKit(1);
            kit.Box(0, Vector3.zero, new Vector3(0.01f, 0.025f, 0.012f), Quaternion.identity);
            return kit.Build("Revolver Hammer");
        }

        public override void Tick(in PlayerCommands commands, float deltaTime)
        {
            if (_reloadLeft > 0f) return;

            if (commands.Reload && _ammo < N.Magazine)
            {
                StartReload();
                return;
            }

            if (AltUnlocked && commands.AltPressed && _coins >= 1f) TossCoin();

            if (commands.FireHeld && Ctx.Time >= _nextShot)
            {
                if (_ammo > 0) Fire(Eye.position, Eye.forward);
                else if (commands.FirePressed) Ctx.Audio?.Play(SoundId.Empty, 0.6f);
            }
        }

        public override void TickBackground(float deltaTime)
        {
            if (_reloadLeft > 0f)
            {
                _reloadLeft -= deltaTime;

                if (_reloadLeft <= 0f)
                {
                    _reloadLeft = 0f;
                    _ammo = N.Magazine;
                }
            }

            if (_ammo == 0 && _reloadLeft <= 0f && Ctx.Time >= _nextShot) StartReload();

            // Монеты копятся всегда, даже в убранном револьвере.
            var max = N[WeaponStat.AltCharges];
            var recharge = Mathf.Max(0.1f, N[WeaponStat.AltCooldown]) /
                           Mathf.Max(0.1f, 1f + (Ctx.Stats != null ? Ctx.Stats.Get(Stat.CoinRecharge) : 0f));

            _coins = Mathf.Min(max, _coins + deltaTime / recharge);

            // Тач: монета сама ловит выстрел — фантазия остаётся, точность не требуется.
            if (_autoCoinShot > 0f)
            {
                _autoCoinShot -= deltaTime;

                if (_autoCoinShot <= 0f) ShootNearestCoin();
            }
        }

        private void StartReload()
        {
            if (_reloadLeft > 0f || _ammo >= N.Magazine) return;

            _reloadLeft = N.Reload;
            _cylinderTarget += 360f;
            Ctx.Audio?.Play(SoundId.Reload, 0.7f, 1.2f);
        }

        // ------------------------------------------------------------------ выстрел

        private void Fire(Vector3 origin, Vector3 direction)
        {
            _ammo--;
            _nextShot = Ctx.Time + 1f / N.Rate;
            _cylinderTarget += 60f;
            _hammer = 1f;
            Motion.Kick = 0.8f;

            var range = N.Range;
            var muzzle = View.TransformPoint(GunModels.RevolverMuzzle);

            var length = Ctx.RayToRock(origin, direction, range, out var rock) ? rock.distance : range;

            // Монета на линии огня — рикошет вместо обычного выстрела. Ближайшая к стрелку.
            if (AltUnlocked && TryHitCoin(origin, direction, length, out var coin))
            {
                Ricochet(coin, origin);
            }
            else
            {
                var spec = HitSpec.Ray(origin, direction, length, N.Area, N.Damage, Source);
                spec.MaxTargets = Mathf.Max(1, Mathf.RoundToInt(N[WeaponStat.Pierce]));
                spec.PierceFalloff = 0.25f;
                spec.CritElites = true;
                spec.Knockback = N[WeaponStat.Knockback];
                spec.Impulse = 9f;

                var report = Strike(spec);

                if (rock.collider != null && length < range) Ctx.Explosions?.Impact(rock.point, rock.normal, 6);

                if (report.Killed > 0) Ctx.Audio?.Play(SoundId.KillTick, 0.5f);
                else if (report.Any) Ctx.Audio?.Play(SoundId.HitTick, 0.5f);
            }

            Ctx.Fx?.Line(muzzle, origin + direction * length, 0.03f, new Color(1f, 0.9f, 0.6f, 0.9f),
                new Color(1f, 0.7f, 0.3f, 0.2f), 0.12f);

            Ctx.Explosions?.Light(muzzle + direction * 0.3f, 14f, 6f);
            Ctx.Audio?.Play(SoundId.RevolverFire, 0.9f, 1f, null, 5);

            Ctx.Feedback.Kick(1.8f);
            Ctx.Feedback.Shake(0.06f);
        }

        private bool TryHitCoin(Vector3 origin, Vector3 direction, float length, out Shot coin)
        {
            coin = default;
            if (Ctx.Projectiles == null) return false;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

            var radius = CoinRadius * (AimAssist ? 2f : 1f);
            var best = float.MaxValue;

            foreach (var shot in _owned)
            {
                var v = shot.Position - origin;
                var along = Vector3.Dot(v, direction);
                if (along < 0f || along > length || along >= best) continue;

                if ((v - direction * along).magnitude > radius) continue;

                best = along;
                coin = shot;
            }

            return best < float.MaxValue;
        }

        /// <summary>
        /// Рикошет с монеты: сначала по другим монетам в воздухе (×1.5 за каждую), потом
        /// в самую опасную цель на прямой видимости. Эволюция делит последний рикошет на три.
        /// </summary>
        private void Ricochet(Shot coin, Vector3 shooter)
        {
            Ctx.Projectiles.Kill(coin.Id);
            Ricochets++;

            var damage = N.Damage * N[WeaponStat.Extra1];
            var from = coin.Position;
            var reach = N[WeaponStat.Extra2];

            // Цепь по монетам: пуля прыгает на ближайшую, каждая усиливает.
            for (var guard = 0; guard < 8; guard++)
            {
                Ctx.Projectiles.CopyOwned(ShotOwner, _owned);

                var next = -1;
                var nextDistance = float.MaxValue;

                for (var i = 0; i < _owned.Count; i++)
                {
                    var d = (_owned[i].Position - from).magnitude;
                    if (d > reach || d >= nextDistance) continue;

                    next = i;
                    nextDistance = d;
                }

                if (next < 0) break;

                var other = _owned[next];
                Ctx.Projectiles.Kill(other.Id);
                Ctx.Fx?.Line(from, other.Position, 0.035f, new Color(1f, 0.95f, 0.6f), new Color(1f, 0.8f, 0.3f), 0.2f);

                from = other.Position;
                damage *= CoinChainBonus;
                Ricochets++;
            }

            var splits = Evolved ? Mathf.Max(1, Mathf.RoundToInt(N[WeaponStat.Projectiles])) : 1;
            var hitAny = false;

            CollectRicochetTargets(from, reach, splits);

            for (var k = 0; k < _candidates.Count; k++)
            {
                var (target, index) = _candidates[k];
                hitAny = true;

                var direction = (target - from).normalized;
                var distance = (target - from).magnitude;

                if (index >= 0)
                {
                    _targets.Clear();
                    _shares.Clear();
                    _targets.Add(index);
                    _shares.Add(1f);

                    var spec = HitSpec.Ray(from, direction, distance, 0.3f, damage, Source);
                    spec.CritElites = true;
                    spec.Impulse = 12f;
                    spec.CritScale = 2f;

                    var report = Ctx.Crowd.HitIndices(_targets, _shares, spec);
                    Kills += report.Killed;
                    DamageDealt += report.Dealt;
                    Ctx.Feedback.Mark(report);
                }
                else
                {
                    var spec = HitSpec.Ray(from, direction, distance + 1f, 0.5f, damage, Source);
                    spec.CritElites = true;
                    Strike(spec);
                }

                Ctx.Fx?.Line(from, target, 0.05f, new Color(1f, 0.95f, 0.7f), new Color(1f, 0.85f, 0.4f), 0.25f);
            }

            if (!hitAny)
            {
                // Бить некого — пуля уходит туда, куда смотрел стрелок, от монеты.
                var direction = (from - shooter).normalized;
                Ctx.Fx?.Line(from, from + direction * 20f, 0.03f, new Color(1f, 0.9f, 0.6f), Color.clear, 0.15f);
            }

            Ctx.Audio?.Play(SoundId.CoinPing, 1f, 1f, null, 6);
            Ctx.Feedback.HitStop(0.06f);
            Ctx.Feedback.Shake(0.15f);
        }

        private readonly List<(Vector3 Point, int Index)> _candidates = new List<(Vector3, int)>();

        /// <summary>
        /// Цели рикошета: Матка или особый (по опасности) первым, потом элита и ближайшие из толпы,
        /// в пределах дальности и на прямой видимости. Сплит берёт первые три.
        /// </summary>
        private void CollectRicochetTargets(Vector3 from, float reach, int count)
        {
            _candidates.Clear();

            if (Ctx.TryFindThreat(from, reach, out var threat, out _)) _candidates.Add((threat, -1));

            if (Ctx.Crowd == null || _candidates.Count >= count) return;

            Ctx.Crowd.FindNearest(from, reach, count + 6, _targets, default, 0f, true);

            foreach (var index in _targets)
            {
                if (_candidates.Count >= count) break;
                if (!Ctx.Crowd.TryGetTarget(index, out var position, out _, out _)) continue;

                var toTarget = position - from;
                if (Ctx.RayToRock(from, toTarget.normalized, Mathf.Max(0f, toTarget.magnitude - 0.3f), out _)) continue;

                _candidates.Add((position, index));
            }
        }

        private void TossCoin()
        {
            if (Ctx.Projectiles == null) return;

            _coins -= 1f;

            var eye = Eye;

            Ctx.Projectiles.Launch(new Shot
            {
                Owner = ShotOwner,
                Visual = _coinVisual,
                Flags = ShotFlags.None,
                Position = eye.position + eye.forward * 0.4f + eye.right * 0.15f - eye.up * 0.1f,
                Velocity = eye.forward * 4.5f + Vector3.up * 7.5f,
                Gravity = 12f,
                Radius = 0.04f,
                Life = 1.8f
            });

            Ctx.Audio?.Play(SoundId.CoinFlip, 0.7f);

            if (AimAssist) _autoCoinShot = 0.35f;
        }

        /// <summary>Тач: выстрел в ближайшую монету сам — из глаз в неё.</summary>
        private void ShootNearestCoin()
        {
            if (_ammo <= 0 || Ctx.Projectiles == null) return;

            Ctx.Projectiles.CopyOwned(ShotOwner, _owned);
            if (_owned.Count == 0) return;

            Fire(Eye.position, (_owned[0].Position - Eye.position).normalized);
        }

        public override void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            // Монета упала на камень или истекла — пропала.
            shot.Dead = true;
        }

        /// <summary>Помощь прицеливания (тач, M5): монета шире и ловит выстрел сама.</summary>
        private bool AimAssist => Ctx.Rig != null && Ctx.Rig.AimAssist;

        public override void TickView(float deltaTime)
        {
            var dt = Mathf.Max(deltaTime, 1e-4f);

            _cylinderAngle = Mathf.Lerp(_cylinderAngle, _cylinderTarget, dt * (_reloadLeft > 0f ? 8f : 25f));
            if (_cylinder != null) _cylinder.localRotation = Quaternion.Euler(0f, 0f, _cylinderAngle);

            _hammer = Mathf.Lerp(_hammer, 0f, dt * 12f);
            if (_hammerTransform != null) _hammerTransform.localRotation = Quaternion.Euler(-40f * _hammer, 0f, 0f);

            Motion.Reload = _reloadLeft > 0f ? Mathf.Sin(Mathf.PI * (1f - _reloadLeft / N.Reload)) : 0f;

            base.TickView(deltaTime);
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
            AltReady = AltUnlocked ? _coins - Mathf.Floor(_coins) : 0f,
            AltCharges = AltUnlocked ? Mathf.FloorToInt(_coins) : 0
        };
    }
}
