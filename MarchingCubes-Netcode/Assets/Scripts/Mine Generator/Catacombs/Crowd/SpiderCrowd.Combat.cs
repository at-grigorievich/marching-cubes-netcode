using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Удар по толпе: форма, урон, отброс, статусы. Все поля — в мире и в HP.
    /// Одна структура на все пушки (M2): взрыв гранаты, дробь, луч рельсы, конус огня,
    /// дуга молота — отличаются числами, а путь урона один, с одним журналом убийств.
    /// </summary>
    public struct HitSpec
    {
        public HitShape Shape;
        public HitKind Kind;
        public DamageSource Source;

        /// <summary>Центр сферы, вершина конуса или начало луча.</summary>
        public Vector3 Origin;

        /// <summary>Ось конуса или направление луча (единичное).</summary>
        public Vector3 Direction;

        /// <summary>Радиус сферы, дальность конуса или толщина луча.</summary>
        public float Radius;

        /// <summary>Длина луча.</summary>
        public float Length;

        /// <summary>Половина угла конуса, градусы.</summary>
        public float HalfAngle;

        /// <summary>Урон в HP при полной доле.</summary>
        public float Damage;

        /// <summary>До этой доли радиуса (дальности) урон полный, дальше линейно спадает.</summary>
        public float FullShare;

        /// <summary>Доля урона на краю формы.</summary>
        public float MinShare;

        /// <summary>Луч: сколько целей пробить (0 — всех) и на сколько слабее каждая следующая.</summary>
        public int MaxTargets;
        public float PierceFalloff;

        public float CritChance;
        public float CritScale;

        /// <summary>По элите — всегда крит («глаза»): револьвер вознаграждает выбор цели.</summary>
        public bool CritElites;

        /// <summary>Отброс трупа в эпицентре, юнитов в секунду. 0 — по умолчанию толпы.</summary>
        public float Impulse;

        /// <summary>Отброс живых, юнитов в секунду, и оглушение, секунды.</summary>
        public float Knockback;
        public float Stun;

        /// <summary>Ближе этой доли радиуса убитого рвёт на куски (взрыв, удар оземь).</summary>
        public float GibShare;

        public float BurnSeconds;
        public float BurnDps;

        public float SlowSeconds;
        public float SlowAdd;
        public float SlowMax;

        public float ShockSeconds;

        /// <summary>Бить и за стенкой. По умолчанию нет: форма режется заливкой по ходам.</summary>
        public bool ThroughWalls;

        public static HitSpec Sphere(Vector3 centre, float radius, float damage, DamageSource source,
            HitKind kind = HitKind.Explosion) => new HitSpec
        {
            Shape = HitShape.Sphere,
            Kind = kind,
            Source = source,
            Origin = centre,
            Direction = Vector3.up,
            Radius = radius,
            Damage = damage,
            FullShare = 1f,
            MinShare = 1f,
            CritScale = 2f
        };

        public static HitSpec Cone(Vector3 apex, Vector3 axis, float range, float halfAngle, float damage,
            DamageSource source, HitKind kind) => new HitSpec
        {
            Shape = HitShape.Cone,
            Kind = kind,
            Source = source,
            Origin = apex,
            Direction = axis.normalized,
            Radius = range,
            HalfAngle = halfAngle,
            Damage = damage,
            FullShare = 1f,
            MinShare = 1f,
            CritScale = 2f
        };

        public static HitSpec Ray(Vector3 origin, Vector3 direction, float length, float width, float damage,
            DamageSource source, HitKind kind = HitKind.Bullet) => new HitSpec
        {
            Shape = HitShape.Capsule,
            Kind = kind,
            Source = source,
            Origin = origin,
            Direction = direction.normalized,
            Length = length,
            Radius = width,
            Damage = damage,
            FullShare = 1f,
            MinShare = 1f,
            CritScale = 2f
        };
    }

    /// <summary>Что сделал удар — для маркера попадания, временного здоровья, статистики пушек.</summary>
    public struct HitReport
    {
        public int Hits;
        public int Killed;
        public int Gibbed;
        public int Crits;
        public float Dealt;

        /// <summary>Ближайшее к началу формы попадание — куда ставить искру и маркер.</summary>
        public Vector3 FirstHitPoint;

        public bool Any => Hits > 0;

        public void Add(in HitReport other)
        {
            if (!Any && other.Any) FirstHitPoint = other.FirstHitPoint;

            Hits += other.Hits;
            Killed += other.Killed;
            Gibbed += other.Gibbed;
            Crits += other.Crits;
            Dealt += other.Dealt;
        }
    }

    public sealed partial class SpiderCrowd
    {
        private NativeArray<SpiderStatus> _status;
        private NativeArray<float> _hitScale;
        private NativeArray<float> _hitOrder;
        private NativeArray<float> _hitDealt;
        private NativeArray<byte> _hitCrit;
        private NativeArray<int> _statusCounters;
        private NativeList<int> _burningList;
        private NativeArray<float> _burnDamage;

        private NativeArray<int> _reachStamp;
        private NativeArray<int> _reachQueue;
        private NativeArray<byte> _reachSteps;
        private int _reachMark;

        private readonly List<float2> _rayCandidates = new List<float2>(64);

        private float _fireSpreadTimer;

        /// <summary>
        /// Ниже этого радиуса форма не режется заливкой по ходам: перегородка тоньше клетки
        /// сетки всё равно не ловится, а гвозди и огонь бьют десятки раз в секунду —
        /// заливка на каждый их удар стоила бы больше самого удара (план M2.2).
        /// </summary>
        public const float WallCheckRadius = 2f;

        /// <summary>Сколько горящих разносят огонь за раз — потолок цены распространения.</summary>
        private const int FireSpreadCap = 32;

        /// <summary>Сколько раз за жизнь толпы заливкой проверялась стенка — для прогона цены.</summary>
        public int WallChecks { get; private set; }

        /// <summary>Сколько особей сейчас под статусом и сколько горит.</summary>
        public int StatusedCount { get; private set; }
        public int BurningCount { get; private set; }

        private void AllocateCombat(int size)
        {
            _status = new NativeArray<SpiderStatus>(size, Allocator.Persistent);
            _hitScale = new NativeArray<float>(size, Allocator.Persistent);
            _hitOrder = new NativeArray<float>(size, Allocator.Persistent);
            _hitDealt = new NativeArray<float>(size, Allocator.Persistent);
            _hitCrit = new NativeArray<byte>(size, Allocator.Persistent);
            _statusCounters = new NativeArray<int>(3, Allocator.Persistent);
            _burningList = new NativeList<int>(FireSpreadCap, Allocator.Persistent);
            _burnDamage = new NativeArray<float>((int)DamageSource.Count, Allocator.Persistent);

            var cells = math.max(1, _flow.CellCount);

            _reachStamp = new NativeArray<int>(cells, Allocator.Persistent);
            _reachSteps = new NativeArray<byte>(cells, Allocator.Persistent);

            // Очередь заливки на радиус взрыва: тысячи клеток, а не вся сетка.
            _reachQueue = new NativeArray<int>(math.min(cells, 65536), Allocator.Persistent);
            _reachMark = 0;

            StatusedCount = 0;
            BurningCount = 0;
        }

        private void ReleaseCombat()
        {
            if (_status.IsCreated) _status.Dispose();
            if (_hitScale.IsCreated) _hitScale.Dispose();
            if (_hitOrder.IsCreated) _hitOrder.Dispose();
            if (_hitDealt.IsCreated) _hitDealt.Dispose();
            if (_hitCrit.IsCreated) _hitCrit.Dispose();
            if (_statusCounters.IsCreated) _statusCounters.Dispose();
            if (_burningList.IsCreated) _burningList.Dispose();
            if (_burnDamage.IsCreated) _burnDamage.Dispose();
            if (_reachStamp.IsCreated) _reachStamp.Dispose();
            if (_reachQueue.IsCreated) _reachQueue.Dispose();
            if (_reachSteps.IsCreated) _reachSteps.Dispose();
        }

        /// <summary>
        /// Удар по толпе любой формой. Убитые идут в журнал с источником, разорванные —
        /// в список под брызги. Не бьёт сквозь стенку (кроме <see cref="HitSpec.ThroughWalls"/>):
        /// сфера и конус режутся заливкой по ходам от точки удара, луч — физикой у звонящего.
        /// </summary>
        public HitReport Hit(in HitSpec spec, List<Vector3> gibbed = null)
        {
            gibbed?.Clear();

            var report = new HitReport();

            if (!_states.IsCreated || _runtime == null) return report;

            var origin = LocalOf(spec.Origin);
            var direction = (float3)world.transform.InverseTransformDirection(spec.Direction);
            direction = math.normalizesafe(direction, new float3(0f, 0f, 1f));

            var scale = math.max(1e-4f, world.transform.lossyScale.x);
            var radius = spec.Radius / scale;
            var length = spec.Length / scale;

            var useMask = !spec.ThroughWalls && spec.Shape != HitShape.Capsule && spec.Radius > WallCheckRadius
                          && MarkReach(origin, radius);

            new SpiderHitGatherJob
            {
                States = _states,
                Tuning = _tuning,
                ReachStamp = _reachStamp,
                Scale = _hitScale,
                Order = _hitOrder,
                Shape = spec.Shape,
                Origin = origin,
                Direction = direction,
                Radius = radius,
                Length = length,
                CosAngle = math.cos(math.radians(math.clamp(spec.HalfAngle, 0f, 89f))),
                FullShare = math.saturate(spec.FullShare),
                MinShare = math.saturate(spec.MinShare),
                UseMask = useMask ? 1 : 0,
                Stamp = _reachMark,
                Dim = _flow.Dim,
                CellSize = _flow.CellSize
            }.Schedule(_states.Length, 64).Complete();

            // Конус — дробь и пламя, летящие по прямой: за углом его нет, сколько бы шагов
            // по ходам ни было до цели. Заливка тут годится лишь как первый отсев.
            if (spec.Shape == HitShape.Cone && !spec.ThroughWalls) CullHidden(spec.Origin);

            // Луч пробивает первых N по дальности; конус кирки рассекает первых N — тех же правил.
            if (spec.MaxTargets > 0) SelectPierced(spec.MaxTargets, spec.PierceFalloff);

            return Apply(spec, origin, direction, radius, gibbed);
        }

        /// <summary>
        /// Удар по выбранным особям с долями урона — цепь молнии, рикошет монеты, болт рельсы.
        /// Кого бить, звонящий уже решил (<see cref="FindNearest"/>).
        /// </summary>
        public HitReport HitIndices(IReadOnlyList<int> indices, IReadOnlyList<float> shares, in HitSpec spec,
            List<Vector3> gibbed = null)
        {
            gibbed?.Clear();

            if (!_states.IsCreated || _runtime == null || indices.Count == 0) return default;

            for (var i = 0; i < _hitScale.Length; i++) _hitScale[i] = 0f;

            for (var k = 0; k < indices.Count; k++)
            {
                var index = indices[k];
                if (index < 0 || index >= _hitScale.Length) continue;

                _hitScale[index] = shares != null && k < shares.Count ? math.max(1e-4f, shares[k]) : 1f;
                _hitOrder[index] = k;
            }

            var origin = LocalOf(spec.Origin);
            var direction = math.normalizesafe((float3)world.transform.InverseTransformDirection(spec.Direction),
                new float3(0f, 0f, 1f));

            return Apply(spec, origin, direction, spec.Radius / math.max(1e-4f, world.transform.lossyScale.x), gibbed);
        }

        private readonly RaycastHit[] _losHits = new RaycastHit[8];

        /// <summary>Сколько целей конуса отсеяно прямой видимостью — для прогона цены.</summary>
        public int HiddenCulled { get; private set; }

        /// <summary>
        /// Отсев целей конуса, до которых от вершины нет прямой: луч физики из вершины (она в пустоте
        /// хода — грабли №1 не про него) к телу особи. Камень в последнем юните пути не считается:
        /// особь сидит на поверхности, и луч к её центру задевает камень под ней.
        ///
        /// Прогон пушек поймал без этого: дробовик в стенку в упор бил паука за перегородкой
        /// в юнит толщиной на все 80 HP — до него по ходам было меньше тринадцати шагов.
        /// </summary>
        private void CullHidden(Vector3 origin)
        {
            var ignore = _targetBody;

            for (var i = 0; i < _hitScale.Length; i++)
            {
                if (_hitScale[i] <= 0f) continue;

                var spider = _states[i];
                var target = WorldOf(spider.Position + spider.Up * 0.3f);
                var delta = target - origin;
                var length = delta.magnitude;

                if (length < 1e-3f) continue;

                var count = Physics.RaycastNonAlloc(origin, delta / length, _losHits, length, ~0,
                    QueryTriggerInteraction.Ignore);

                for (var k = 0; k < count; k++)
                {
                    if (ignore != null && _losHits[k].collider == ignore) continue;
                    if (_losHits[k].distance >= length - 1f) continue;

                    _hitScale[i] = 0f;
                    HiddenCulled++;
                    break;
                }
            }
        }

        /// <summary>Луч пробивает первых N по дальности, каждый следующий слабее.</summary>
        private void SelectPierced(int maxTargets, float falloff)
        {
            _rayCandidates.Clear();

            for (var i = 0; i < _hitScale.Length; i++)
            {
                if (_hitScale[i] > 0f) _rayCandidates.Add(new float2(_hitOrder[i], i));
            }

            _rayCandidates.Sort((a, b) => a.x.CompareTo(b.x));

            var share = 1f;

            for (var k = 0; k < _rayCandidates.Count; k++)
            {
                var index = (int)_rayCandidates[k].y;

                if (k >= maxTargets)
                {
                    _hitScale[index] = 0f;
                    continue;
                }

                _hitScale[index] = math.max(1e-4f, share);
                share *= math.saturate(1f - falloff);
            }
        }

        private HitReport Apply(in HitSpec spec, float3 origin, float3 direction, float radius, List<Vector3> gibbed)
        {
            var report = new HitReport();
            var around = spec.Kind == HitKind.Explosion ? CountNear(origin, nearRadius) : 0;

            new SpiderHitApplyJob
            {
                States = _states,
                Status = _status,
                Scale = _hitScale,
                Killed = _killed,
                Dealt = _hitDealt,
                Crit = _hitCrit,
                Shape = spec.Shape,
                Kind = spec.Kind,
                Origin = origin,
                Direction = direction,
                Radius = radius,
                Damage = math.max(0f, spec.Damage),
                CritChance = math.saturate(spec.CritChance),
                CritScale = spec.CritScale > 0f ? spec.CritScale : 2f,
                CritElites = spec.CritElites ? 1 : 0,
                Impulse = spec.Impulse > 0f ? spec.Impulse : deathImpulse,
                Knockback = spec.Knockback,
                Stun = spec.Stun,
                GibShare = spec.GibShare,
                BurnSeconds = spec.BurnSeconds,
                BurnDps = spec.BurnDps,
                BurnSource = (int)spec.Source,
                SlowSeconds = spec.SlowSeconds,
                SlowAdd = spec.SlowAdd,
                SlowMax = spec.SlowMax,
                ShockSeconds = spec.ShockSeconds,
                Seed = (uint)math.max(1, Environment.TickCount ^ (_serial * 7919))
            }.Schedule(_states.Length, 64).Complete();

            var bestOrder = float.MaxValue;

            for (var i = 0; i < _killed.Length; i++)
            {
                if (_hitScale[i] <= 0f) continue;

                var dealt = _hitDealt[i];
                var mark = _killed[i];

                if (dealt > 0f || mark != SpiderMarks.None || spec.Stun > 0f || spec.Knockback > 0f ||
                    spec.SlowSeconds > 0f || spec.BurnSeconds > 0f || spec.ShockSeconds > 0f)
                {
                    report.Hits++;
                    report.Dealt += dealt;
                    if (_hitCrit[i] != 0) report.Crits++;

                    if (_hitOrder[i] < bestOrder)
                    {
                        bestOrder = _hitOrder[i];
                        report.FirstHitPoint = WorldOf(_states[i].Position);
                    }
                }

                if (mark == SpiderMarks.None) continue;

                report.Killed++;

                var spider = _states[i];
                var torn = mark == SpiderMarks.Gibbed;

                if (torn)
                {
                    report.Gibbed++;
                    spider.GibDrop = MeasureDrop(spider);
                    _states[i] = spider;
                }

                var at = WorldOf(spider.Position);
                if (torn) gibbed?.Add(at);

                RecordKill(spider, at, torn, spec.Source);
            }

            // Пролом в толпе должен постоять — но только если это ПРОЛОМ (см. комментарий
            // у holdKillCount): заметная доля тех, кто был вокруг взрыва.
            if (spec.Kind == HitKind.Explosion && report.Killed >= holdKillCount && report.Killed >= around * holdShare)
            {
                _spawnHold = spawnHoldAfterKill;
                HoldsTriggered++;
            }

            return report;
        }

        /// <summary>
        /// Метит клетки, связанные ходами с точкой удара, на радиус формы плюс клетку.
        /// </summary>
        /// <returns>Удалась ли заливка. Нет клетки рядом — форма не режется (лучше задеть
        /// лишнего, чем промахнуться из-за точки удара в толще породы).</returns>
        private bool MarkReach(float3 localOrigin, float radius)
        {
            if (!_flow.IsCreated || !_reachStamp.IsCreated) return false;

            var start = FindOpenStart(localOrigin);
            if (start < 0) return false;

            _reachMark++;
            WallChecks++;

            new SpiderReachJob
            {
                Topology = _flow.GetTopology(),
                Stamp = _reachStamp,
                Queue = _reachQueue,
                Steps = _reachSteps,
                Start = start,
                Mark = _reachMark,
                MaxSteps = (int)math.ceil(radius / math.max(0.1f, _flow.CellSize)) + 1
            }.Schedule().Complete();

            return true;
        }

        private readonly List<float2> _startCandidates = new List<float2>(64);

        /// <summary>
        /// Клетка, с которой начинать заливку: ближайшая проходимая, до центра которой от точки
        /// удара прямая не проходит через камень. Просто ближайшая не годится: у стенки тоньше
        /// клетки ближайшей бывает клетка соседнего хода, и заливка оттуда «связывала» с ударом
        /// всех за перегородкой — прогон пушек поймал это на взрыве у стены.
        /// </summary>
        private int FindOpenStart(float3 origin)
        {
            var centre = _flow.CellOf(origin);

            for (var ring = 0; ring <= 3; ring++)
            {
                _startCandidates.Clear();

                for (var dz = -ring; dz <= ring; dz++)
                for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (ring > 0 && math.abs(dx) != ring && math.abs(dy) != ring && math.abs(dz) != ring) continue;

                    var c = centre + new int3(dx, dy, dz);
                    if (!_flow.InRange(c)) continue;

                    var index = _flow.CellIndex(c);
                    if (_flow.Walkable[index] == 0) continue;

                    _startCandidates.Add(new float2(math.lengthsq(_flow.CellCentre(index) - origin), index));
                }

                _startCandidates.Sort((a, b) => a.x.CompareTo(b.x));

                foreach (var candidate in _startCandidates)
                {
                    var index = (int)candidate.y;
                    if (ClearLine(origin, _flow.CellCentre(index))) return index;
                }
            }

            return -1;
        }

        /// <summary>Прямая между точками без камня — по полю плотности, шагом в четверть юнита.</summary>
        private bool ClearLine(float3 from, float3 to)
        {
            if (world == null) return true;

            var delta = to - from;
            var length = math.length(delta);
            var steps = math.max(1, (int)math.ceil(length / 0.25f));

            for (var k = 1; k < steps; k++)
            {
                if (world.IsSolid(WorldOf(from + delta * (k / (float)steps)))) return false;
            }

            return true;
        }

        /// <summary>
        /// До <paramref name="count"/> ближайших живых в сфере (и, если задано, в конусе) —
        /// захват молнии, рикошет монеты, помощь прицеливания. Сквозь стенку не видит: та же
        /// заливка, что режет взрыв.
        /// </summary>
        public int FindNearest(Vector3 centre, float radius, int count, List<int> into, Vector3 coneAxis = default,
            float coneHalfAngle = 0f, bool elitesFirst = false, IReadOnlyList<int> exclude = null, bool wallCheck = true)
        {
            into.Clear();

            if (!_states.IsCreated || count <= 0) return 0;

            var local = LocalOf(centre);
            var scale = math.max(1e-4f, world.transform.lossyScale.x);
            var reach = radius / scale;
            var reachSq = reach * reach;

            var useMask = wallCheck && radius > WallCheckRadius && MarkReach(local, reach);

            var axis = coneHalfAngle > 0f
                ? math.normalizesafe((float3)world.transform.InverseTransformDirection(coneAxis))
                : float3.zero;
            var cos = math.cos(math.radians(math.clamp(coneHalfAngle, 0f, 89f)));

            _rayCandidates.Clear();

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];
                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead || spider.Ebb == 2) continue;

                var delta = spider.Position - local;
                var distanceSq = math.lengthsq(delta);
                if (distanceSq > reachSq) continue;

                if (coneHalfAngle > 0f && math.dot(math.normalizesafe(delta), axis) < cos) continue;
                if (exclude != null && Contains(exclude, i)) continue;
                if (useMask && !SpiderReach.Reached(_reachStamp, _reachMark, _flow.Dim, _flow.CellSize,
                        spider.Position, spider.Up)) continue;

                // Элита вперёд — монета и рельса ищут самую опасную цель.
                var key = elitesFirst && spider.Elite != 0 ? distanceSq - 1e6f : distanceSq;

                _rayCandidates.Add(new float2(key, i));
            }

            _rayCandidates.Sort((a, b) => a.x.CompareTo(b.x));

            for (var k = 0; k < _rayCandidates.Count && into.Count < count; k++) into.Add((int)_rayCandidates[k].y);

            return into.Count;
        }

        private static bool Contains(IReadOnlyList<int> list, int value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == value) return true;
            }

            return false;
        }

        /// <summary>Особь по номеру: жива ли, где, элита ли, радиус тела в мире.</summary>
        public bool TryGetTarget(int index, out Vector3 worldPosition, out bool elite, out float bodyRadius)
        {
            worldPosition = default;
            elite = false;
            bodyRadius = 0f;

            if (!_states.IsCreated || index < 0 || index >= _states.Length) return false;

            var spider = _states[index];
            if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) return false;

            worldPosition = WorldOf(spider.Position);
            elite = spider.Elite != 0;
            bodyRadius = _tuning[spider.Kind].BodyRadius * spider.Scale * math.max(1e-4f, world.transform.lossyScale.x);

            return true;
        }

        /// <summary>Сколько HP сожгло горение от источника за жизнь толпы — статистика пушек.</summary>
        public float BurnDamageBy(DamageSource source) =>
            _burnDamage.IsCreated && (int)source < _burnDamage.Length ? _burnDamage[(int)source] : 0f;

        /// <summary>Статусы за шаг: горение жжёт, сгоревшие идут в журнал, огонь перекидывается.</summary>
        private void TickStatuses(float deltaTime)
        {
            if (!_status.IsCreated) return;

            new SpiderStatusJob
            {
                States = _states,
                Status = _status,
                Killed = _killed,
                Counters = _statusCounters,
                Burning = _burningList,
                BurnDamage = _burnDamage,
                BurningCap = FireSpreadCap,
                DeltaTime = deltaTime
            }.Schedule().Complete();

            BurningCount = _statusCounters[0];
            StatusedCount = _statusCounters[2];

            if (_statusCounters[1] > 0)
            {
                for (var i = 0; i < _killed.Length; i++)
                {
                    if (_killed[i] != SpiderMarks.Burned) continue;

                    var spider = _states[i];
                    var source = (DamageSource)math.clamp(_status[i].BurnSource, 0, (int)DamageSource.Count - 1);

                    RecordKill(spider, WorldOf(spider.Position), false, source);
                    _status[i] = default;
                }
            }

            _fireSpreadTimer -= deltaTime;

            if (BurningCount > 0 && _fireSpreadTimer <= 0f)
            {
                _fireSpreadTimer = 0.25f;
                SpreadFire(0.25f);
            }
        }

        /// <summary>
        /// Огонь перекидывается на соседей в полутора юнитах, четверть в секунду: горящие бегут
        /// дальше в толпу и разносят его. Раз в четверть секунды, не больше 32 горящих — цена
        /// ограничена сверху, сколько бы ни горело.
        /// </summary>
        private void SpreadFire(float interval)
        {
            const float reach = 1.5f;
            var chance = 0.25f * interval;
            var cellSize = math.max(0.1f, neighbourRadius);

            for (var k = 0; k < _burningList.Length; k++)
            {
                var index = _burningList[k];
                var burner = _status[index];
                if (burner.Burn <= 0f) continue;

                var position = _states[index].Position;
                var cell = SpiderHash.Cell(position, cellSize);

                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                {
                    if (!_hash.TryGetFirstValue(SpiderHash.Key(cell + new int3(x, y, z)), out var other, out var it)) continue;

                    do
                    {
                        if (other == index) continue;

                        var neighbour = _neighbours[other];
                        if (neighbour.w <= 0f) continue;

                        var gap = reach + neighbour.w * 0.5f;
                        if (math.lengthsq(neighbour.xyz - position) > gap * gap) continue;

                        var status = _status[other];
                        if (status.Burn > 0f || _random.NextFloat() > chance) continue;

                        status.Burn = burner.Burn * 0.8f + 0.5f;
                        status.BurnDps = burner.BurnDps;
                        status.BurnSource = burner.BurnSource;
                        _status[other] = status;
                    }
                    while (_hash.TryGetNextValue(out other, ref it));
                }
            }
        }

        /// <summary>
        /// Сколько шагов по ходам от игрока до точки — указатель логова (M1.9): «~80 м» по пути,
        /// а не по прямой (грабли №42). За пределом заливки — <see cref="SpiderFlowField.Unreachable"/>,
        /// без проходимой клетки рядом — −1.
        /// </summary>
        public int PathStepsTo(Vector3 worldPoint) => _flow.IsCreated ? StepsFromTarget(LocalOf(worldPoint)) : -1;

        /// <summary>Размер клетки сетки в мире — шаг в юнитах для указателя.</summary>
        public float StepLength => _flow.IsCreated ? _flow.CellSize * math.max(1e-4f, world.transform.lossyScale.x) : 1f;

        // ------------------------------------------------------------------ прогоны

        /// <summary>
        /// Посадить особь на камень у точки — раскладка прогона пушек (WeaponCheck): цели
        /// в прямом ходе, за перегородкой, в конусе и вне его. Живучесть — в попаданиях.
        /// </summary>
        /// <returns>Номер слота или −1, если рядом нет проходимой клетки.</returns>
        public int SpawnAt(Vector3 worldPoint, int kindIndex, float healthHits)
        {
            if (!_states.IsCreated || _runtime == null || !_flow.IsCreated) return -1;

            var cell = _flow.FindNearestWalkable(LocalOf(worldPoint), 3);
            if (cell < 0) return -1;

            var slot = FindFreeSlot();
            if (slot < 0) return -1;

            kindIndex = math.clamp(kindIndex, 0, _runtime.Length - 1);

            Place(slot, cell, kindIndex, 1f, CombatUnits.Hits(healthHits), 1f, new float3(1f), false, 0.5f, false);
            Alive++;

            return slot;
        }

        /// <summary>Убрать всех без журнала и трупов — чистая раскладка прогона.</summary>
        public void ClearAll()
        {
            if (!_states.IsCreated) return;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];
                spider.Active = 0;
                _states[i] = spider;

                _neighbours[i] = new float4(spider.Position, 0f);
                _status[i] = default;
            }

            _kills.Clear();
            Alive = 0;

            if (_hash.IsCreated) _hash.Clear();
        }

        /// <summary>
        /// Шаг без движения: статусы (горение жжёт), снимок соседей и пространственная сетка —
        /// взрыватель снаряда по близости ищет пауков по ней. Прогон пушек держит раскладку
        /// на месте, иначе толпа побежала бы к игроку и сбила замер формы.
        /// </summary>
        public void StepStatic(float deltaTime)
        {
            if (!_states.IsCreated) return;

            if (deltaTime > 0f) TickStatuses(deltaTime);

            new SpiderPublishJob
            {
                States = _states,
                Tuning = _tuning,
                Neighbours = _neighbours,
                Velocities = _velocities,
                Heights = _heights,
                Climbs = _climbs,
                Tiers = _tiers
            }.Schedule(_states.Length, 64).Complete();

            _hash.Clear();

            new SpiderHashJob
            {
                Neighbours = _neighbours,
                Hash = _hash.AsParallelWriter(),
                CellSize = math.max(0.1f, neighbourRadius)
            }.Schedule(_states.Length, 64).Complete();

            CountAlive();
        }

        /// <summary>Сколько HP у особи в слоте; −1 — мертва или слот пуст.</summary>
        public float HealthAt(int slot)
        {
            if (!_states.IsCreated || slot < 0 || slot >= _states.Length) return -1f;

            var spider = _states[slot];
            return spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead ? -1f : spider.Health;
        }

        /// <summary>Где особь в слоте, мир.</summary>
        public Vector3 PositionAt(int slot) => _states.IsCreated && slot >= 0 && slot < _states.Length
            ? WorldOf(_states[slot].Position)
            : Vector3.zero;

        /// <summary>Статусы особи — для прогона: горит ли, замедлена ли, оглушена ли.</summary>
        public bool TryGetStatus(int index, out SpiderStatus status)
        {
            status = default;

            if (!_status.IsCreated || index < 0 || index >= _status.Length) return false;

            status = _status[index];
            return true;
        }

        /// <summary>Сколько живых особей рядом с точкой под данным статусом — для прогона.</summary>
        public int CountStatused(Vector3 worldPoint, float radius, Func<SpiderStatus, bool> predicate)
        {
            if (!_status.IsCreated) return 0;

            var local = LocalOf(worldPoint);
            var radiusSq = radius * radius;
            var count = 0;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];
                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;
                if (math.lengthsq(spider.Position - local) > radiusSq) continue;

                if (predicate(_status[i])) count++;
            }

            return count;
        }
    }
}
