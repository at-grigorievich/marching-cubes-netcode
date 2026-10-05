using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Цель-одиночка: особый паук или Матка (M3) — не особь толпы, а объект со своей логикой.
    /// Их десяток, и пушки проверяют их перебором после толпы — дешевле любой сетки.
    /// </summary>
    public interface ISoloTarget
    {
        bool IsAlive { get; }

        /// <summary>Центр тела и радиус попадания, мир.</summary>
        Vector3 Centre { get; }
        float HitRadius { get; }

        /// <summary>Насколько цель опасна — для рикошета монеты и рельсы: Матка > особый > элита.</summary>
        int Threat { get; }

        /// <summary>По ней всегда крит («глаза»): особые и Матки.</summary>
        bool CritAlways { get; }

        /// <summary>Урон в HP. Возвращает, сколько прошло (щит Матки — ноль).</summary>
        float TakeHit(float damage, HitKind kind, DamageSource source, Vector3 from);

        /// <summary>Толчок и оглушение — у кого они есть.</summary>
        void Stagger(Vector3 push, float seconds);
    }

    /// <summary>Реестр одиночек сцены. Регистрируются сами при появлении (M3).</summary>
    public static class SoloTargets
    {
        private static readonly List<ISoloTarget> List = new List<ISoloTarget>();

        public static IReadOnlyList<ISoloTarget> All => List;

        public static void Add(ISoloTarget target)
        {
            if (target != null && !List.Contains(target)) List.Add(target);
        }

        public static void Remove(ISoloTarget target) => List.Remove(target);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => List.Clear();
    }

    /// <summary>Отметка попадания у прицела.</summary>
    public struct HitMarker
    {
        public bool Kill;
        public bool Crit;
        public float Damage;
    }

    /// <summary>
    /// Обратная связь попадания: отдача, тряска, стоп-кадр, маркер у прицела. Числа у каждой
    /// пушки свои (её описание), путь один: так «вес» пушки настраивается, а не пишется заново.
    /// </summary>
    public sealed class HitFeedback
    {
        private readonly CatacombTestRig _rig;

        public event Action<HitMarker> Marked;

        public HitFeedback(CatacombTestRig rig) => _rig = rig;

        public void Kick(float degrees)
        {
            if (_rig != null) _rig.AddKick(degrees);
        }

        public void Shake(float strength)
        {
            if (_rig != null) _rig.AddShake(strength);
        }

        /// <summary>Стоп-кадр через владельца времени: он же ограничивает частоту.</summary>
        public void HitStop(float seconds) => GameClock.HitStop(seconds);

        public void Mark(in HitReport report)
        {
            if (!report.Any) return;

            Marked?.Invoke(new HitMarker { Kill = report.Killed > 0, Crit = report.Crits > 0, Damage = report.Dealt });
        }
    }

    /// <summary>
    /// Всё, что нужно пушке от сцены: толпа, Матка, одиночки, мир, эффекты, тело игрока, статы.
    /// Пушка ничего не ищет по сцене сама (грабли №61: поиск не видит объекты DontSave).
    /// </summary>
    public sealed class CombatContext
    {
        /// <summary>
        /// Какую долю площадного урона получает Матка. Граната бьёт её вдвое слабее, чем паука
        /// (прежние 1.5 попадания против 3): площадное оружие не должно решать босса само.
        /// Одним множителем у Матки, а не отдельным «уроном по Матке» в каждой из восьми пушек.
        /// </summary>
        public const float QueenAreaShare = 0.5f;

        public CatacombTestRig Rig;
        public Transform Eye;
        public SpiderCrowd Crowd;
        public CaveQueen Queen;
        public CatacombWorld World;
        public CaveExplosions Explosions;
        public CaveBlastMarks Marks;
        public ProjectileSystem Projectiles;
        public WeaponFx Fx;
        public WeaponAudio Audio;
        public HitFeedback Feedback;
        public PlayerStats Stats;
        public WeaponMaterials Materials;

        public IPlayerBody Body => Rig;

        /// <summary>
        /// Часы боя: секунды игрового времени, их ведёт арсенал. Не <c>Time.time</c>: прогоны
        /// стреляют в режиме редактирования, где оно стоит, и пушка после первого выстрела
        /// не выстрелила бы больше никогда.
        /// </summary>
        public float Time;

        /// <summary>Коллайдер самого игрока — лучи из глаз его не видят (грабли №64).</summary>
        public Collider PlayerCollider;

        private readonly RaycastHit[] _rayHits = new RaycastHit[8];

        /// <summary>
        /// Удар по всем: толпа, Матка, одиночки. Матке — доля площадного урона; крит по
        /// одиночкам, если пушка так умеет. Маркер у прицела ставится здесь же.
        /// </summary>
        public HitReport Strike(in HitSpec spec, List<Vector3> gibbed = null, bool mark = true)
        {
            var report = Crowd != null ? Crowd.Hit(spec, gibbed) : default;

            StrikeQueen(spec, ref report);
            StrikeSolos(spec, ref report);

            if (mark) Feedback?.Mark(report);

            return report;
        }

        private void StrikeQueen(in HitSpec spec, ref HitReport report)
        {
            if (Queen == null || !Queen.IsVulnerable || spec.Damage <= 0f) return;
            if (!Touches(spec, Queen.Centre, Queen.HitRadius)) return;

            var share = spec.Kind == HitKind.Explosion ? QueenAreaShare : 1f;
            var crit = spec.CritElites ? spec.CritScale : 1f;

            var dealt = Queen.TakeHit(spec.Damage * share * crit);

            report.Hits++;
            report.Dealt += dealt;
            if (crit > 1f) report.Crits++;
            if (!report.Any || report.Hits == 1) report.FirstHitPoint = Queen.Centre;
        }

        private void StrikeSolos(in HitSpec spec, ref HitReport report)
        {
            var solos = SoloTargets.All;

            for (var i = solos.Count - 1; i >= 0; i--)
            {
                var solo = solos[i];
                if (solo == null || !solo.IsAlive) continue;
                if (!Touches(spec, solo.Centre, solo.HitRadius)) continue;

                var crit = solo.CritAlways && spec.CritElites ? spec.CritScale : 1f;

                if (spec.Damage > 0f)
                {
                    var dealt = solo.TakeHit(spec.Damage * crit, spec.Kind, spec.Source, spec.Origin);

                    report.Hits++;
                    report.Dealt += dealt;
                    if (crit > 1f) report.Crits++;
                    if (!solo.IsAlive) report.Killed++;
                }

                if (spec.Knockback > 0f || spec.Stun > 0f)
                {
                    var away = (solo.Centre - spec.Origin).normalized;
                    solo.Stagger(away * spec.Knockback * 0.3f, spec.Stun);
                }
            }
        }

        /// <summary>Задевает ли форма удара шар цели. Сквозь стенку — не проверяется: одиночек мало,
        /// и звонящий луч уже обрезан породой.</summary>
        public static bool Touches(in HitSpec spec, Vector3 centre, float radius)
        {
            switch (spec.Shape)
            {
                case HitShape.Sphere:
                    return (centre - spec.Origin).sqrMagnitude <= (spec.Radius + radius) * (spec.Radius + radius);

                case HitShape.Cone:
                {
                    var v = centre - spec.Origin;
                    var along = Vector3.Dot(v, spec.Direction);
                    if (along < -radius || along > spec.Radius + radius) return false;

                    var lateral = (v - spec.Direction * along).magnitude;
                    var half = Mathf.Max(0f, along) * Mathf.Tan(spec.HalfAngle * Mathf.Deg2Rad) + radius;

                    return lateral <= half;
                }

                default:
                {
                    var v = centre - spec.Origin;
                    var along = Mathf.Clamp(Vector3.Dot(v, spec.Direction), 0f, spec.Length);

                    return (v - spec.Direction * along).magnitude <= spec.Radius + radius;
                }
            }
        }

        /// <summary>
        /// Луч до камня, мимо капсулы игрока и триггеров (паутина). Начат в пустоте хода —
        /// грабли №1 про лучи из толщи породы здесь не про него.
        /// </summary>
        public bool RayToRock(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit)
        {
            hit = default;

            var count = Physics.RaycastNonAlloc(origin, direction, _rayHits, maxDistance, ~0,
                QueryTriggerInteraction.Ignore);

            var best = float.MaxValue;

            for (var i = 0; i < count; i++)
            {
                if (PlayerCollider != null && _rayHits[i].collider == PlayerCollider) continue;
                if (_rayHits[i].distance >= best) continue;

                best = _rayHits[i].distance;
                hit = _rayHits[i];
            }

            return best < float.MaxValue;
        }

        /// <summary>Точка под перекрестием: камень по взгляду или точка на дальности.</summary>
        public Vector3 AimPoint(float range, out bool onRock, out RaycastHit hit)
        {
            onRock = RayToRock(Eye.position, Eye.forward, range, out hit);
            return onRock ? hit.point : Eye.position + Eye.forward * range;
        }

        /// <summary>Кто под прицелом сильнее всех опасен: Матка, особый, элита — для рикошета и рельсы.</summary>
        public bool TryFindThreat(Vector3 from, float range, out Vector3 point, out int crowdIndex)
        {
            point = default;
            crowdIndex = -1;

            var bestThreat = -1;
            var bestDistance = float.MaxValue;

            if (Queen != null && Queen.IsVulnerable)
            {
                var d = (Queen.Centre - from).magnitude;

                if (d <= range && !RayToRock(from, (Queen.Centre - from).normalized, d, out _))
                {
                    bestThreat = 100;
                    bestDistance = d;
                    point = Queen.Centre;
                }
            }

            foreach (var solo in SoloTargets.All)
            {
                if (solo == null || !solo.IsAlive) continue;

                var d = (solo.Centre - from).magnitude;
                if (d > range) continue;
                if (solo.Threat < bestThreat || (solo.Threat == bestThreat && d >= bestDistance)) continue;
                if (RayToRock(from, (solo.Centre - from).normalized, d, out _)) continue;

                bestThreat = solo.Threat;
                bestDistance = d;
                point = solo.Centre;
            }

            if (bestThreat >= 0) return true;

            return false;
        }
    }
}
