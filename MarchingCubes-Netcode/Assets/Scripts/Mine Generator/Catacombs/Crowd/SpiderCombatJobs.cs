using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Статусы особи: горение, замедление, оглушение, ток.
    ///
    /// Отдельным массивом, а не полями <see cref="SpiderState"/>: состояние читает и пишет
    /// целиком каждый джоб толпы, и на 1200 особях лишние байты в нём — это проход памяти
    /// в каждом из них. Статусы читают только движение (замедление, оглушение), отрисовка
    /// (оттенок) и свой джоб.
    /// </summary>
    public struct SpiderStatus
    {
        /// <summary>Сколько секунд ещё горит.</summary>
        public float Burn;

        /// <summary>Урон горения, HP в секунду.</summary>
        public float BurnDps;

        /// <summary>Чья пушка подожгла — ей и убийство (<see cref="DamageSource"/>).</summary>
        public int BurnSource;

        /// <summary>Сколько секунд ещё замедлена.</summary>
        public float Slow;

        /// <summary>Во сколько раз медленнее, пока замедлена (≤ 1).</summary>
        public float SlowScale;

        /// <summary>Сколько секунд оглушена: стоит, катится по инерции, не кусает.</summary>
        public float Stun;

        /// <summary>Сколько секунд по ней идёт ток — синий отсвет и судорога.</summary>
        public float Shock;
    }

    /// <summary>Форма поражения.</summary>
    public enum HitShape : byte
    {
        /// <summary>Сфера: взрыв, удар оземь, выдох огнемёта.</summary>
        Sphere,

        /// <summary>Конус от вершины по оси: дробовик, огнемёт, дуга молота.</summary>
        Cone,

        /// <summary>Капсула вдоль луча: пуля, рельса, гвоздь. Цели упорядочены по дальности.</summary>
        Capsule
    }

    /// <summary>Чем бьёт — от этого вид смерти: куски, кувырок, чёрный труп, судорога.</summary>
    public enum HitKind : byte
    {
        Explosion,
        Bullet,
        Fire,
        Shock,
        Melee
    }

    /// <summary>Что делать с телом, попавшим в форму: метка шага сбора.</summary>
    public static class SpiderMarks
    {
        public const int None = 0;
        public const int Killed = 1;
        public const int Gibbed = 2;
        public const int Burned = 3;
    }

    /// <summary>
    /// Сбор попаданий: кто в форме и с какой долей урона. Отдельно от применения урона,
    /// потому что лучу нужен порядок по дальности (пробитие N целей с убыванием урона),
    /// а он считается на главном потоке по горстке кандидатов.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderHitGatherJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<SpiderState> States;
        [ReadOnly] public NativeArray<SpiderTuning> Tuning;

        /// <summary>Метка заливки клеток: попадают только особи у клеток с текущей меткой.</summary>
        [ReadOnly] public NativeArray<int> ReachStamp;

        [WriteOnly] public NativeArray<float> Scale;
        [WriteOnly] public NativeArray<float> Order;

        public HitShape Shape;
        public float3 Origin;
        public float3 Direction;
        public float Radius;
        public float Length;
        public float CosAngle;
        public float FullShare;
        public float MinShare;

        public int UseMask;
        public int Stamp;
        public int3 Dim;
        public float CellSize;

        public void Execute(int index)
        {
            Scale[index] = 0f;
            Order[index] = 0f;

            var spider = States[index];

            if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) return;

            var body = Tuning[spider.Kind].BodyRadius * spider.Scale * 0.6f;
            var p = spider.Position;

            float share;
            float order;

            switch (Shape)
            {
                case HitShape.Sphere:
                {
                    var distance = math.length(p - Origin);
                    if (distance > Radius) return;

                    order = distance;
                    share = Falloff(distance / math.max(1e-4f, Radius));
                    break;
                }

                case HitShape.Cone:
                {
                    var v = p - Origin;
                    var along = math.dot(v, Direction);
                    if (along < -body || along > Radius + body) return;

                    // Конус с допуском на тело: паук на краю конуса задет лапами.
                    var lateral = math.length(v - Direction * along);
                    var sin = math.sqrt(math.max(0f, 1f - CosAngle * CosAngle));
                    var halfWidth = math.max(0f, along) * sin / math.max(1e-3f, CosAngle) + body;

                    if (lateral > halfWidth) return;

                    order = along;
                    share = Falloff(math.saturate(along / math.max(1e-4f, Radius)));
                    break;
                }

                default:
                {
                    var v = p - Origin;
                    var along = math.dot(v, Direction);
                    if (along < -body || along > Length + body) return;

                    var lateral = math.length(v - Direction * math.clamp(along, 0f, Length));
                    if (lateral > Radius + body) return;

                    order = along;
                    share = 1f;
                    break;
                }
            }

            if (UseMask != 0 && !Reached(p, spider.Up)) return;

            Scale[index] = math.max(1e-4f, share);
            Order[index] = order;
        }

        private float Falloff(float t)
        {
            if (t <= FullShare) return 1f;

            return math.lerp(1f, MinShare, math.saturate((t - FullShare) / math.max(1e-4f, 1f - FullShare)));
        }

        /// <summary>
        /// Связана ли особь с точкой удара ходами. По восьми клеткам вокруг, а не по одной:
        /// особь прижата к камню, и её собственная клетка сплошь и рядом в породе (грабли №11).
        /// </summary>
        private bool Reached(float3 position, float3 up) =>
            SpiderReach.Reached(ReachStamp, Stamp, Dim, CellSize, position, up);
    }

    /// <summary>
    /// Связана ли особь с отмеченными клетками. Общее у сбора попаданий и поиска целей.
    /// </summary>
    public static class SpiderReach
    {
        /// <summary>
        /// Смотрим восемь клеток вокруг точки, сдвинутой от камня по нормали особи, и только
        /// те, чей центр по её сторону камня. Иначе перегородка тоньше клетки протекала:
        /// восемь клеток вокруг особи захватывали клетки по ту сторону стенки, а они в заливке
        /// от взрыва. Прогон пушек поймал это: паук за стенкой в полъюнита терял 30 HP.
        /// </summary>
        public static bool Reached(NativeArray<int> stamp, int mark, int3 dim, float cellSize, float3 position, float3 up)
        {
            var probe = position + up * (cellSize * 0.35f);
            var baseCell = (int3)math.floor(probe / cellSize - 0.5f);

            for (var dz = 0; dz <= 1; dz++)
            for (var dy = 0; dy <= 1; dy++)
            for (var dx = 0; dx <= 1; dx++)
            {
                var c = baseCell + new int3(dx, dy, dz);

                if (math.any(c < 0) || math.any(c >= dim)) continue;
                if (stamp[(c.z * dim.y + c.y) * dim.x + c.x] != mark) continue;

                var centre = (new float3(c.x, c.y, c.z) + 0.5f) * cellSize;

                if (math.dot(centre - position, up) > -0.25f * cellSize) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Урон по собранным: смерть с видом по оружию, отброс и оглушение живым, статусы.
    /// Обобщение прежнего джоба урона сферой — тот же разлёт на куски у взрыва.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderHitApplyJob : IJobParallelFor
    {
        public NativeArray<SpiderState> States;
        public NativeArray<SpiderStatus> Status;

        [ReadOnly] public NativeArray<float> Scale;

        [WriteOnly] public NativeArray<int> Killed;
        [WriteOnly] public NativeArray<float> Dealt;
        [WriteOnly] public NativeArray<byte> Crit;

        public HitShape Shape;
        public HitKind Kind;
        public float3 Origin;
        public float3 Direction;
        public float Radius;

        public float Damage;
        public float CritChance;
        public float CritScale;
        public int CritElites;

        /// <summary>Отброс трупа в эпицентре, как у прежнего взрыва.</summary>
        public float Impulse;

        /// <summary>Отброс живых, юнитов в секунду.</summary>
        public float Knockback;

        public float Stun;
        public float GibShare;

        public float BurnSeconds;
        public float BurnDps;
        public int BurnSource;

        public float SlowSeconds;

        /// <summary>На сколько попадание срезает скорость (суммируется до <see cref="SlowMax"/>).</summary>
        public float SlowAdd;
        public float SlowMax;

        public float ShockSeconds;

        public uint Seed;

        public void Execute(int index)
        {
            Killed[index] = SpiderMarks.None;
            Dealt[index] = 0f;
            Crit[index] = 0;

            var share = Scale[index];
            if (share <= 0f) return;

            var spider = States[index];
            if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) return;

            var random = new Random(math.max(1u, Seed + (uint)index * 747796405u + 1u));

            var crit = (CritElites != 0 && spider.Elite != 0) || (CritChance > 0f && random.NextFloat() < CritChance);
            var damage = Damage * share * (crit ? CritScale : 1f);

            Crit[index] = (byte)(crit && damage > 0f ? 1 : 0);

            var away = AwayFrom(spider.Position);

            if (damage > 0f)
            {
                spider.Health -= damage;
                Dealt[index] = damage;
            }

            if (damage <= 0f || spider.Health > 0.01f)
            {
                // Живой: отброс по поверхности, оглушение, статусы.
                var status = Status[index];

                if (Knockback > 0f)
                {
                    var along = away - spider.Up * math.dot(away, spider.Up);
                    spider.Velocity += math.normalizesafe(along, away) * (Knockback * share);
                }

                if (Stun > 0f) status.Stun = math.max(status.Stun, Stun);

                if (BurnSeconds > 0f)
                {
                    status.Burn = math.max(status.Burn, BurnSeconds);
                    status.BurnDps = math.max(status.BurnDps, BurnDps);
                    status.BurnSource = BurnSource;
                }

                if (SlowSeconds > 0f)
                {
                    var scale = status.Slow > 0f ? status.SlowScale : 1f;

                    status.Slow = math.max(status.Slow, SlowSeconds);
                    status.SlowScale = math.max(1f - SlowMax, scale - SlowAdd);
                }

                if (ShockSeconds > 0f)
                {
                    status.Shock = math.max(status.Shock, ShockSeconds);
                    status.Stun = math.max(status.Stun, ShockSeconds);
                }

                // Засадник, задетый на своде, срывается: висеть под огнём он не будет.
                if (spider.Clip == (int)SpiderClip.Idle && (Stun > 0f || damage > 0f))
                {
                    spider.Clip = (int)SpiderClip.Walk;
                    spider.Phase = 0f;
                }

                Status[index] = status;
                States[index] = spider;
                return;
            }

            spider.Clip = (int)SpiderClip.Dead;
            spider.Phase = 0f;
            spider.Timer = 0f;
            spider.Ebb = 0;

            // Отброс трупа: у эпицентра полный, у края формы слабее — как у прежнего взрыва.
            var falloff = Shape == HitShape.Sphere ? math.saturate(share) : 0.6f;

            spider.Velocity = (away + random.NextFloat3Direction() * 0.35f) * (Impulse * falloff);
            spider.Spin = random.NextFloat3Direction() * (12f * falloff);

            var mark = SpiderMarks.Killed;
            spider.Gibbed = 0;

            switch (Kind)
            {
                case HitKind.Explosion:
                case HitKind.Melee:
                {
                    // У самого эпицентра — на куски, как раньше у гранаты.
                    var distanceShare = Shape == HitShape.Sphere
                        ? math.length(spider.Position - Origin) / math.max(1e-4f, Radius)
                        : 1f - share;

                    if (GibShare > 0f && distanceShare <= GibShare)
                    {
                        spider.Gibbed = 1;
                        spider.Spin = float3.zero;
                        spider.Velocity *= 0.35f;
                        mark = SpiderMarks.Gibbed;
                    }

                    break;
                }

                case HitKind.Bullet:
                    // Пуля — кувырок трупа назад по лучу, без кусков.
                    spider.Velocity = Direction * (Impulse * 0.7f) + spider.Up * 2f;
                    break;

                case HitKind.Fire:
                    // Огонь — труп чернеет. Умножением: светлая добавка уже делала толпу
                    // мультяшной (грабли №21, №22).
                    spider.Hue *= 0.18f;
                    spider.Spin *= 0.3f;
                    spider.Velocity *= 0.3f;
                    mark = SpiderMarks.Burned;
                    break;

                case HitKind.Shock:
                    // Ток — короткая судорога на месте, без отлёта.
                    spider.Velocity *= 0.15f;
                    spider.Spin = random.NextFloat3Direction() * 20f;
                    break;
            }

            Killed[index] = mark;
            States[index] = spider;
        }

        private float3 AwayFrom(float3 position)
        {
            switch (Shape)
            {
                case HitShape.Capsule:
                    return Direction;

                default:
                {
                    var away = position - Origin;
                    return math.lengthsq(away) > 1e-6f ? math.normalize(away) : Direction;
                }
            }
        }
    }

    /// <summary>
    /// Статусы за шаг: горение жжёт, остальное тикает. Один поток, а не параллельно: на WebGL
    /// воркеров нет всё равно, а счётчики горящих и сгоревших без атомиков считать проще так.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderStatusJob : IJob
    {
        public NativeArray<SpiderState> States;
        public NativeArray<SpiderStatus> Status;

        [WriteOnly] public NativeArray<int> Killed;

        /// <summary>0 — горящих, 1 — сгоревших за шаг, 2 — под любым статусом.</summary>
        public NativeArray<int> Counters;

        /// <summary>Горящие для распространения огня — до ёмкости списка.</summary>
        public NativeList<int> Burning;

        /// <summary>Урон горения по источникам, HP — для статистики пушек и таблицы DPS.</summary>
        public NativeArray<float> BurnDamage;

        public int BurningCap;
        public float DeltaTime;

        public void Execute()
        {
            var burning = 0;
            var burned = 0;
            var any = 0;

            Burning.Clear();

            for (var i = 0; i < States.Length; i++)
            {
                Killed[i] = SpiderMarks.None;

                var status = Status[i];

                if (status.Burn <= 0f && status.Slow <= 0f && status.Stun <= 0f && status.Shock <= 0f) continue;

                var spider = States[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead)
                {
                    Status[i] = default;
                    continue;
                }

                any++;

                status.Slow = math.max(0f, status.Slow - DeltaTime);
                status.Stun = math.max(0f, status.Stun - DeltaTime);
                status.Shock = math.max(0f, status.Shock - DeltaTime);

                if (status.Burn > 0f)
                {
                    status.Burn = math.max(0f, status.Burn - DeltaTime);

                    var burn = math.min(math.max(0f, spider.Health), status.BurnDps * DeltaTime);
                    spider.Health -= status.BurnDps * DeltaTime;

                    var source = math.clamp(status.BurnSource, 0, BurnDamage.Length - 1);
                    BurnDamage[source] += burn;
                    burning++;

                    if (Burning.Length < BurningCap) Burning.Add(i);

                    if (spider.Health <= 0.01f)
                    {
                        spider.Clip = (int)SpiderClip.Dead;
                        spider.Phase = 0f;
                        spider.Timer = 0f;
                        spider.Ebb = 0;
                        spider.Gibbed = 0;
                        spider.Hue *= 0.18f;
                        spider.Velocity *= 0.2f;

                        Killed[i] = SpiderMarks.Burned;
                        burned++;
                    }

                    States[i] = spider;
                }

                Status[i] = status;
            }

            Counters[0] = burning;
            Counters[1] = burned;
            Counters[2] = any;
        }
    }

    /// <summary>
    /// Заливка по ходам от точки удара на несколько шагов: какие клетки связаны с ней проходом.
    /// Особи у остальных клеток за стенкой — их взрыв не задевает. Та же заливка, что не даёт
    /// выводку Матки вылезти из-за стены, только под Burst и без словаря: метка клетки — номер
    /// запроса, и массив не надо чистить между взрывами.
    /// </summary>
    [BurstCompile]
    public struct SpiderReachJob : IJob
    {
        public SpiderFlowField.Topology Topology;

        public NativeArray<int> Stamp;
        public NativeArray<int> Queue;
        public NativeArray<byte> Steps;

        public int Start;
        public int Mark;
        public int MaxSteps;

        public void Execute()
        {
            if (Start < 0) return;

            var head = 0;
            var tail = 0;

            Stamp[Start] = Mark;
            Steps[Start] = 0;
            Queue[tail++] = Start;

            var dim = Topology.Dim;

            while (head < tail)
            {
                var current = Queue[head++];
                var next = Steps[current] + 1;

                if (next > MaxSteps) continue;

                var c = new int3(current % dim.x, current / dim.x % dim.y, current / (dim.x * dim.y));

                for (var n = 0; n < SpiderFlowField.NeighbourCount; n++)
                {
                    var index = Topology.Step(c, SpiderFlowField.Neighbour(n));

                    if (index < 0 || Stamp[index] == Mark) continue;
                    if (tail >= Queue.Length) return;

                    Stamp[index] = Mark;
                    Steps[index] = (byte)math.min(255, next);
                    Queue[tail++] = index;
                }
            }
        }
    }
}
