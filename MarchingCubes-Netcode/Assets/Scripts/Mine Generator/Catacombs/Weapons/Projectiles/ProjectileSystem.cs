using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>Что случилось со снарядом.</summary>
    public enum ShotEvent
    {
        /// <summary>Ударился о камень (в hit — точка и нормаль).</summary>
        HitRock,

        /// <summary>Пролетел ближе взрывателя к пауку толпы.</summary>
        NearSpider,

        /// <summary>Пролетел ближе взрывателя к Матке или одиночке.</summary>
        NearBoss,

        /// <summary>Попал в игрока (снаряды врагов).</summary>
        HitPlayer,

        /// <summary>Кончилось время полёта.</summary>
        Expired
    }

    [Flags]
    public enum ShotFlags
    {
        None = 0,

        /// <summary>Взрыватель по близости к паукам толпы.</summary>
        NearSpiders = 1,

        /// <summary>Взрыватель по близости к Матке и одиночкам.</summary>
        NearBosses = 2,

        /// <summary>Снаряд врага: бьёт игрока, отражается парированием (кирка, M2).</summary>
        Hostile = 4,

        /// <summary>Сквозь камень (плевок с дугой через угол у врагов — нет; служебное).</summary>
        Ghost = 8,

        /// <summary>Застрял в камне и лежит (липучка, магнит, громоотвод) — не летит, не падает.</summary>
        Stuck = 16
    }

    /// <summary>
    /// Снаряд. Структура в списке, а не объект на снаряд: гвоздей в воздухе — десятки, и объект
    /// с компонентом на каждый — это сотни GameObject в секунду на платформе без воркеров.
    /// </summary>
    public struct Shot
    {
        public int Id;
        public int Owner;
        public int Visual;
        public ShotFlags Flags;

        public Vector3 Position;
        public Vector3 Velocity;

        /// <summary>Тяжесть, юнитов в секунду за секунду.</summary>
        public float Gravity;

        /// <summary>Радиус для удара о камень.</summary>
        public float Radius;

        /// <summary>Взрыватель по близости, юниты.</summary>
        public float Proximity;

        public float Age;
        public float Life;

        /// <summary>Своё число пушки: заряд, урон, номер монеты…</summary>
        public float Payload;

        /// <summary>Своё целое пушки: вид снаряда у её хозяина.</summary>
        public int Tag;

        /// <summary>Связь с другим снарядом по номеру (гвоздь на орбите — его магнит).</summary>
        public int Link;

        /// <summary>Сколько ещё ударов выдержит (пила, гвоздь на орбите).</summary>
        public int Hits;

        /// <summary>Нормаль камня, в котором застрял.</summary>
        public Vector3 Normal;

        /// <summary>Хозяин решил: снаряд кончился.</summary>
        public bool Dead;

        public bool IsStuck => (Flags & ShotFlags.Stuck) != 0;
    }

    /// <summary>Кто владеет снарядами: пушка, Матка, особый паук.</summary>
    public interface IShotHandler
    {
        void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit);
    }

    /// <summary>Как снаряд выглядит: меш (по подмешу на материал) и росчерк за ним.</summary>
    public sealed class ShotVisual
    {
        public Mesh Mesh;
        public Material[] Materials;
        public float Scale = 1f;

        /// <summary>Повернуть меш носом по скорости (граната, гвоздь); иначе — как лежит.</summary>
        public bool AlignToVelocity = true;

        /// <summary>Вращение вокруг оси полёта, градусов в секунду (монета).</summary>
        public float Spin;

        public Color StreakHead = Color.clear;
        public Color StreakTail = Color.clear;
        public float StreakLength;
        public float StreakWidth = 0.05f;
    }

    /// <summary>
    /// Общий симулятор снарядов: гранаты, гвозди, ядро, монеты, плевки врагов. Полёт под
    /// тяжестью, удар о камень сферой, взрыватель по близости к толпе и боссам, отрисовка
    /// инстансингом — по вызову на вид снаряда, а не объект на снаряд.
    ///
    /// Что делать при событии, решает хозяин (<see cref="IShotHandler"/>): взорваться, застрять,
    /// отскочить. Не решил (<see cref="Shot.Dead"/> не выставлен) — при ударе о камень снаряд
    /// останавливается, при истечении — исчезает.
    /// </summary>
    public sealed class ProjectileSystem : MonoBehaviour
    {
        private const int InstanceBatch = 128;

        private readonly List<Shot> _shots = new List<Shot>(128);
        private readonly List<IShotHandler> _owners = new List<IShotHandler>();
        private readonly List<ShotVisual> _visuals = new List<ShotVisual>();

        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private readonly Matrix4x4[] _matrices = new Matrix4x4[InstanceBatch];

        private int _nextId;

        public SpiderCrowd Crowd;
        public CaveQueen Queen;
        public IPlayerBody Player;
        public Collider PlayerCollider;
        public WeaponFx Fx;

        public int Live => _shots.Count;

        /// <summary>Сколько снарядов выпущено за жизнь системы — для прогонов.</summary>
        public int Fired { get; private set; }

        public int Register(IShotHandler owner)
        {
            _owners.Add(owner);
            return _owners.Count - 1;
        }

        public int AddVisual(ShotVisual visual)
        {
            _visuals.Add(visual);
            return _visuals.Count - 1;
        }

        public int Launch(Shot shot)
        {
            shot.Id = ++_nextId;
            shot.Age = 0f;
            shot.Dead = false;

            _shots.Add(shot);
            Fired++;

            return shot.Id;
        }

        /// <summary>Снаряды хозяина — читать и править (магнит считает гвозди, липучки ищутся для подрыва).</summary>
        public int CopyOwned(int owner, List<Shot> into)
        {
            into.Clear();

            foreach (var shot in _shots)
            {
                if (shot.Owner == owner) into.Add(shot);
            }

            return into.Count;
        }

        /// <summary>Переписать снаряд по номеру (после правки скопированного).</summary>
        public bool Replace(in Shot shot)
        {
            for (var i = 0; i < _shots.Count; i++)
            {
                if (_shots[i].Id != shot.Id) continue;

                _shots[i] = shot;
                return true;
            }

            return false;
        }

        /// <summary>Снаряд по номеру, если ещё летит.</summary>
        public bool TryGet(int id, out Shot shot)
        {
            foreach (var candidate in _shots)
            {
                if (candidate.Id != id) continue;

                shot = candidate;
                return true;
            }

            shot = default;
            return false;
        }

        public void Kill(int id)
        {
            for (var i = 0; i < _shots.Count; i++)
            {
                if (_shots[i].Id != id) continue;

                _shots.RemoveAt(i);
                return;
            }
        }

        public void Clear() => _shots.Clear();

        /// <summary>
        /// Отразить вражеские снаряды в сфере — парирование кирки. Летят обратно по направлению
        /// <paramref name="direction"/>, хозяином становится парировавший.
        /// </summary>
        public int Parry(Vector3 centre, float radius, Vector3 direction, int newOwner, float speedScale)
        {
            var count = 0;

            for (var i = 0; i < _shots.Count; i++)
            {
                var shot = _shots[i];

                if ((shot.Flags & ShotFlags.Hostile) == 0) continue;
                if ((shot.Position - centre).sqrMagnitude > radius * radius) continue;

                shot.Flags = (shot.Flags & ~ShotFlags.Hostile) | ShotFlags.NearSpiders | ShotFlags.NearBosses;
                shot.Owner = newOwner;
                shot.Velocity = direction.normalized * Mathf.Max(10f, shot.Velocity.magnitude * speedScale);
                shot.Gravity = 0f;
                shot.Age = 0f;
                shot.Life = Mathf.Max(shot.Life, 3f);
                shot.Proximity = Mathf.Max(shot.Proximity, 1f);

                _shots[i] = shot;
                count++;
            }

            return count;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
            Draw();
        }

        /// <summary>Полёт и события. Отдельно от Update — прогоны гоняют сами.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            for (var i = _shots.Count - 1; i >= 0; i--)
            {
                var shot = _shots[i];
                shot.Age += deltaTime;

                if (!shot.IsStuck) Fly(ref shot, deltaTime);

                if (!shot.Dead && shot.Age >= shot.Life) Notify(ref shot, ShotEvent.Expired, default, true);

                if (shot.Dead) _shots.RemoveAt(i);
                else if (i < _shots.Count) _shots[i] = shot;
            }
        }

        private void Fly(ref Shot shot, float deltaTime)
        {
            var from = shot.Position;
            shot.Velocity += Vector3.down * (shot.Gravity * deltaTime);

            var step = shot.Velocity * deltaTime;
            var length = step.magnitude;

            // Удар о камень. Луч начат в пустоте, где летит снаряд — грабли №1 не про него.
            if ((shot.Flags & ShotFlags.Ghost) == 0 && length > 1e-5f && CastRock(from, shot.Radius, step / length, length,
                    out var hit))
            {
                shot.Position = hit.point + hit.normal * (shot.Radius + 0.02f);
                shot.Normal = hit.normal;

                Notify(ref shot, ShotEvent.HitRock, hit, false);

                // Хозяин не решил — снаряд застревает там, где ударился.
                if (!shot.Dead && !shot.IsStuck && shot.Velocity.sqrMagnitude > 0f &&
                    Vector3.Dot(shot.Velocity, hit.normal) < 0f)
                {
                    shot.Flags |= ShotFlags.Stuck;
                    shot.Velocity = Vector3.zero;
                }

                return;
            }

            shot.Position = from + step;

            if ((shot.Flags & ShotFlags.Hostile) != 0)
            {
                if (Player != null && TouchesPlayer(shot.Position, shot.Radius))
                {
                    Notify(ref shot, ShotEvent.HitPlayer, default, true);
                }

                return;
            }

            // Взрыватель — вдоль всего шага, а не в его конце: гвоздь на 60 юнитах в секунду
            // проходит за кадр 1–2 юнита, и паук между двумя проверками пролетался насквозь.
            // Прогон пушек это и поймал: в упор гвоздомёт не убивал никого.
            if ((shot.Flags & ShotFlags.NearSpiders) != 0 && Crowd != null && shot.Age > 0.03f)
            {
                var samples = Mathf.Max(1, Mathf.CeilToInt(length / 0.5f));

                for (var k = 1; k <= samples; k++)
                {
                    var probe = from + step * (k / (float)samples);
                    if (!Crowd.TouchesSpider(probe, shot.Proximity)) continue;

                    shot.Position = probe;
                    Notify(ref shot, ShotEvent.NearSpider, default, false);
                    break;
                }

                if (shot.Dead) return;
            }

            if ((shot.Flags & ShotFlags.NearBosses) != 0 && NearBoss(shot.Position, shot.Proximity))
            {
                Notify(ref shot, ShotEvent.NearBoss, default, false);
            }
        }

        private bool NearBoss(Vector3 position, float proximity)
        {
            if (Queen != null && Queen.IsVulnerable &&
                (position - Queen.Centre).magnitude < Queen.HitRadius + proximity)
            {
                return true;
            }

            foreach (var solo in SoloTargets.All)
            {
                if (solo != null && solo.IsAlive && (position - solo.Centre).magnitude < solo.HitRadius + proximity)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TouchesPlayer(Vector3 position, float radius)
        {
            Player.GetBodySegment(out var low, out var high, out var bodyRadius);

            var axis = high - low;
            var t = Mathf.Clamp01(Vector3.Dot(position - low, axis) / Mathf.Max(1e-6f, axis.sqrMagnitude));

            return (position - (low + axis * t)).magnitude <= bodyRadius + radius + 0.2f;
        }

        private bool CastRock(Vector3 from, float radius, Vector3 direction, float length, out RaycastHit best)
        {
            best = default;

            var count = Physics.SphereCastNonAlloc(from, Mathf.Max(0.02f, radius), direction, _hits, length, ~0,
                QueryTriggerInteraction.Ignore);

            var nearest = float.MaxValue;

            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];

                // Нулевая дистанция — стартовое перекрытие (снаряд у самого камня): такой хит
                // не скажет, куда ударился. Капсула игрока снарядам не преграда.
                if (hit.distance <= 0f || hit.distance >= nearest) continue;
                if (PlayerCollider != null && hit.collider == PlayerCollider) continue;

                nearest = hit.distance;
                best = hit;
            }

            return nearest < float.MaxValue;
        }

        private void Notify(ref Shot shot, ShotEvent what, in RaycastHit hit, bool killByDefault)
        {
            var owner = shot.Owner >= 0 && shot.Owner < _owners.Count ? _owners[shot.Owner] : null;

            owner?.OnShot(ref shot, what, hit);

            if (killByDefault && !shot.Dead && what != ShotEvent.HitPlayer) shot.Dead = true;
            if (what == ShotEvent.HitPlayer) shot.Dead = true;
            if (what == ShotEvent.Expired) shot.Dead = true;
        }

        // ------------------------------------------------------------------ отрисовка

        private void Draw()
        {
            for (var v = 0; v < _visuals.Count; v++)
            {
                var visual = _visuals[v];
                var count = 0;

                foreach (var shot in _shots)
                {
                    if (shot.Visual != v) continue;

                    if (visual.StreakLength > 0f && Fx != null && !shot.IsStuck)
                    {
                        var speed = shot.Velocity.magnitude;
                        var tail = shot.Position - shot.Velocity / Mathf.Max(1e-4f, speed) *
                            Mathf.Min(visual.StreakLength, speed * Mathf.Max(0.01f, shot.Age));

                        Fx.Streak(tail, shot.Position, visual.StreakWidth, visual.StreakTail, visual.StreakHead);
                    }

                    if (visual.Mesh == null || count >= InstanceBatch) continue;

                    var rotation = visual.AlignToVelocity && shot.Velocity.sqrMagnitude > 1e-4f
                        ? Quaternion.LookRotation(shot.Velocity)
                        : shot.IsStuck && shot.Normal.sqrMagnitude > 0.5f
                            ? Quaternion.LookRotation(shot.Normal)
                            : Quaternion.identity;

                    if (visual.Spin != 0f) rotation *= Quaternion.Euler(0f, 0f, shot.Age * visual.Spin);

                    _matrices[count++] = Matrix4x4.TRS(shot.Position, rotation, Vector3.one * visual.Scale);
                }

                if (count == 0 || visual.Mesh == null) continue;

                for (var sub = 0; sub < visual.Mesh.subMeshCount && sub < visual.Materials.Length; sub++)
                {
                    var parameters = new RenderParams(visual.Materials[sub])
                    {
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                        layer = gameObject.layer,
                        worldBounds = new Bounds(transform.position, Vector3.one * 2000f)
                    };

                    Graphics.RenderMeshInstanced(parameters, visual.Mesh, sub, _matrices, count);
                }
            }
        }
    }
}
