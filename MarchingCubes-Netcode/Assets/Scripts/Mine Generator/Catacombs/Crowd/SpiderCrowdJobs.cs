using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Состояние одной особи. Обычная структура в нативном массиве, а не GameObject:
    /// три сотни объектов с MonoBehaviour это три сотни вызовов Update через границу
    /// управляемого кода, и на WebGL это видно в профайлере раньше, чем отрисовка.
    /// </summary>
    public struct SpiderState
    {
        public float3 Position;
        public float3 Velocity;

        /// <summary>Поворот вокруг вертикали, радианы.</summary>
        public float Yaw;

        /// <summary>Фаза текущего клипа, 0..1.</summary>
        public float Phase;

        public float Scale;

        /// <summary>Множитель скорости этой особи. Без разброса толпа идёт стеной.</summary>
        public float SpeedScale;

        /// <summary>Множитель яркости этой особи.</summary>
        public float Tint;

        /// <summary>Сколько лежит труп после конца клипа смерти.</summary>
        public float Timer;

        public int Kind;

        /// <summary>Значение <see cref="SpiderClip"/>.</summary>
        public int Clip;

        public int Health;

        /// <summary>0 — слот свободен и может быть занят новой особью.</summary>
        public int Active;
    }

    /// <summary>Числа вида, в форме, пригодной для джоба: без ссылок и без строк.</summary>
    public struct SpiderTuning
    {
        public float MoveSpeed;
        public float TurnSpeed;
        public float BodyRadius;
        public float AttackRange;
        public float StrideLength;
        public float CorpseLinger;

        public float WalkLength;
        public float AttackLength;
        public float DeadLength;

        /// <summary>Где в текстуре анимации лежит клип: x — первая строка, y — кадров, z — кольцевой ли.</summary>
        public float3 WalkRow;
        public float3 AttackRow;
        public float3 DeadRow;
    }

    /// <summary>Общий для джобов ключ пространственной сетки.</summary>
    public static class SpiderHash
    {
        public static int3 Cell(float3 position, float cellSize) => (int3)math.floor(position / cellSize);

        /// <summary>
        /// Три больших простых числа и xor — классический хеш по клетке. Ключ не обязан
        /// быть уникальным: коллизия означает лишний перебор, а не ошибку.
        /// </summary>
        public static int Key(int3 cell) =>
            (cell.x * 73856093) ^ (cell.y * 19349663) ^ (cell.z * 83492791);
    }

    /// <summary>
    /// Раскладывает особей по пространственной сетке.
    ///
    /// Без сетки расталкивание это каждый с каждым, то есть на трёх сотнях особей
    /// сорок пять тысяч проверок в кадр — на WebGL это весь бюджет кадра целиком.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderHashJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> Neighbours;
        public NativeParallelMultiHashMap<int, int>.ParallelWriter Hash;
        public float CellSize;

        public void Execute(int index)
        {
            // w — радиус тела; ноль означает, что особи в этом слоте нет или она мертва,
            // и расталкивать об неё некого.
            if (Neighbours[index].w <= 0f) return;

            Hash.Add(SpiderHash.Key(SpiderHash.Cell(Neighbours[index].xyz, CellSize)), index);
        }
    }

    /// <summary>
    /// Одна итерация поведения толпы: поле потока даёт направление, стая — форму,
    /// поле отжима от стен — границы.
    ///
    /// Позиции соседей читаются из отдельного массива, снятого в конце ПРОШЛОГО кадра,
    /// а не из States. Это снимает вопрос о порядке: джоб пишет только свой элемент
    /// States и ни разу не читает чужой, поэтому он честно параллельный, а кадровая
    /// задержка в позиции соседа в кадре не видна.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderMoveJob : IJobParallelFor
    {
        public NativeArray<SpiderState> States;

        [ReadOnly] public NativeArray<float4> Neighbours;
        [ReadOnly] public NativeArray<float3> Velocities;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Hash;
        [ReadOnly] public NativeArray<SpiderTuning> Tuning;

        public SpiderFlowField.FlowSampler Field;

        /// <summary>Игрок в локальных координатах генерации.</summary>
        public float3 Target;

        public int TargetValid;
        public float DeltaTime;

        public float HashCellSize;
        public float NeighbourRadius;

        public float SeparationWeight;
        public float AlignmentWeight;
        public float CohesionWeight;
        public float WallWeight;

        /// <summary>Во сколько раз в секунду скорость подтягивается к желаемой.</summary>
        public float Acceleration;

        /// <summary>Ближе этого особь идёт прямо на игрока, минуя поле потока.</summary>
        public float CloseRange;

        /// <summary>Сколько соседей разбирать максимум. Потолок стоимости в давке.</summary>
        public int MaxNeighbours;

        /// <summary>Как быстро перебирают лапами стоящие особи — чтобы не выглядели чучелами.</summary>
        public float IdleStride;

        public void Execute(int index)
        {
            var spider = States[index];

            if (spider.Active == 0) return;

            var tuning = Tuning[spider.Kind];

            if (spider.Clip == (int)SpiderClip.Dead)
            {
                UpdateCorpse(ref spider, tuning);
                States[index] = spider;
                return;
            }

            Field.SampleAt(spider.Position, out var flow, out var wallPush, out var floorY, out var onField);

            var toTarget = Target - spider.Position;
            toTarget.y = 0f;

            var distance = math.length(toTarget);

            if (spider.Clip == (int)SpiderClip.Attack)
            {
                UpdateAttack(ref spider, tuning, toTarget, distance, floorY, onField);
                States[index] = spider;
                return;
            }

            if (TargetValid != 0 && distance <= tuning.AttackRange)
            {
                spider.Clip = (int)SpiderClip.Attack;
                spider.Phase = 0f;
                States[index] = spider;
                return;
            }

            var desired = float3.zero;

            if (onField) desired += flow;

            // Вблизи игрока поле потока вырождается: в клетке-источнике направление ноль,
            // и толпа, дойдя до неё, начинала бы топтаться вокруг игрока, а не давить его.
            if (TargetValid != 0 && distance < CloseRange) desired += math.normalizesafe(toTarget) * 1.5f;

            // Если особь оказалась вне размеченных клеток (уровень перестроили, взрыв
            // вынес пол), она всё равно должна двигаться к игроку, а не замирать столбом.
            if (!onField && TargetValid != 0) desired += math.normalizesafe(toTarget);

            desired += Flock(index, ref spider, tuning);
            desired += wallPush * WallWeight;

            desired.y = 0f;
            desired = math.normalizesafe(desired);

            var wanted = desired * (tuning.MoveSpeed * spider.SpeedScale);

            spider.Velocity = math.lerp(spider.Velocity, wanted, math.saturate(DeltaTime * Acceleration));
            spider.Velocity.y = 0f;

            Advance(ref spider, tuning, floorY, onField);

            States[index] = spider;
        }

        private void UpdateCorpse(ref SpiderState spider, SpiderTuning tuning)
        {
            if (spider.Phase < 1f)
            {
                spider.Phase = math.min(1f, spider.Phase + DeltaTime / math.max(0.05f, tuning.DeadLength));
                return;
            }

            spider.Timer += DeltaTime;

            // Слот освобождается, а не объект уничтожается: массив особей выделен один раз
            // на всю игру, и место мёртвой особи займёт следующая волна.
            if (spider.Timer >= tuning.CorpseLinger) spider.Active = 0;
        }

        private void UpdateAttack(ref SpiderState spider, SpiderTuning tuning, float3 toTarget, float distance,
            float floorY, bool onField)
        {
            spider.Phase += DeltaTime / math.max(0.05f, tuning.AttackLength);

            if (spider.Phase >= 1f)
            {
                spider.Clip = (int)SpiderClip.Walk;
                spider.Phase = 0f;
            }

            // Бьёт с места, но доворачивается: удар в сторону читается как промах движка,
            // а не как промах паука.
            spider.Velocity = math.lerp(spider.Velocity, float3.zero, math.saturate(DeltaTime * 12f));

            if (TargetValid != 0 && distance > 1e-3f)
            {
                spider.Yaw = TurnToward(spider.Yaw, math.atan2(toTarget.x, toTarget.z),
                    math.radians(tuning.TurnSpeed) * DeltaTime);
            }

            if (onField) spider.Position.y = math.lerp(spider.Position.y, floorY, math.saturate(DeltaTime * 12f));
        }

        /// <summary>Расталкивание, выравнивание и сбивание в кучу — три классических правила стаи.</summary>
        private float3 Flock(int index, ref SpiderState spider, SpiderTuning tuning)
        {
            var separation = float3.zero;
            var alignment = float3.zero;
            var cohesion = float3.zero;

            var neighbours = 0;
            var examined = 0;

            var radiusSq = NeighbourRadius * NeighbourRadius;
            var cell = SpiderHash.Cell(spider.Position, HashCellSize);

            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var key = SpiderHash.Key(cell + new int3(dx, dy, dz));

                if (!Hash.TryGetFirstValue(key, out var other, out var iterator)) continue;

                do
                {
                    if (other == index) continue;
                    if (++examined > MaxNeighbours) goto done;

                    var data = Neighbours[other];

                    var delta = spider.Position - data.xyz;
                    delta.y = 0f;

                    var distanceSq = math.lengthsq(delta);
                    if (distanceSq < 1e-6f) continue;

                    var touch = data.w + tuning.BodyRadius;

                    if (distanceSq < touch * touch)
                    {
                        var d = math.sqrt(distanceSq);

                        // Сила растёт линейно к нулю расстояния и обнуляется на касании:
                        // обратный квадрат на плотной толпе выстреливает особей из давки.
                        separation += delta / d * (1f - d / touch);
                    }

                    if (distanceSq >= radiusSq) continue;

                    alignment += Velocities[other];
                    cohesion += data.xyz;
                    neighbours++;
                }
                while (Hash.TryGetNextValue(out other, ref iterator));
            }

            done:

            var result = separation * SeparationWeight;

            if (neighbours == 0) return result;

            result += math.normalizesafe(alignment / neighbours) * AlignmentWeight;

            var toCentre = cohesion / neighbours - spider.Position;
            toCentre.y = 0f;

            result += math.normalizesafe(toCentre) * CohesionWeight;

            return result;
        }

        /// <summary>Шаг с проверкой по осям, посадкой на пол и фазой анимации от пройденного пути.</summary>
        private void Advance(ref SpiderState spider, SpiderTuning tuning, float floorY, bool onField)
        {
            var step = spider.Velocity * DeltaTime;
            step.y = 0f;

            // Оси проверяются по отдельности, чтобы особь скользила вдоль стены, а не
            // вставала в неё. Это тот же приём, что у отладочной камеры в стенде.
            if (!Field.CanStand(spider.Position + new float3(step.x, 0f, 0f)))
            {
                step.x = 0f;
                spider.Velocity.x *= 0.2f;
            }

            if (!Field.CanStand(spider.Position + new float3(0f, 0f, step.z)))
            {
                step.z = 0f;
                spider.Velocity.z *= 0.2f;
            }

            spider.Position += step;

            if (onField)
            {
                Field.SampleAt(spider.Position, out _, out _, out var nextFloor, out var nextValid);
                if (nextValid) floorY = nextFloor;

                // Посадка не мгновенная: на пандусе мгновенная даёт рывок по высоте
                // ровно в момент смены клетки, и толпа идёт вверх ступеньками.
                spider.Position.y = math.lerp(spider.Position.y, floorY, math.saturate(DeltaTime * 12f));
            }

            var speed = math.length(step) / math.max(1e-5f, DeltaTime);

            if (speed > 0.05f)
            {
                spider.Yaw = TurnToward(spider.Yaw, math.atan2(spider.Velocity.x, spider.Velocity.z),
                    math.radians(tuning.TurnSpeed) * DeltaTime);
            }

            // Фаза ведётся пройденным путём, а не временем: иначе лапы скользят по полу,
            // и чем сильнее особь тормозит в давке, тем заметнее. Нижняя граница нужна
            // стоящим — замершая насмерть модель читается как сломанная анимация,
            // и она же заменяет здесь выброшенный клип стояния.
            var stride = math.max(0.05f, tuning.StrideLength);

            spider.Phase = math.frac(spider.Phase + math.max(speed, IdleStride) * DeltaTime / stride);
        }

        /// <summary>Доворот к цели не быстрее заданного, по кратчайшей стороне.</summary>
        private static float TurnToward(float current, float target, float maxDelta)
        {
            var delta = target - current;

            // В диапазон -pi..pi, иначе доворот с 179 на -179 градусов идёт длинной стороной.
            delta -= math.floor((delta + math.PI) / (2f * math.PI)) * (2f * math.PI);

            return current + math.clamp(delta, -maxDelta, maxDelta);
        }
    }

    /// <summary>
    /// Снимает позиции и скорости для следующего кадра.
    ///
    /// Отдельным проходом, а не внутри движения: движение пишет States параллельно,
    /// и читать оттуда соседей в том же кадре было бы гонкой.
    /// </summary>
    [BurstCompile]
    public struct SpiderPublishJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<SpiderState> States;
        [ReadOnly] public NativeArray<SpiderTuning> Tuning;

        [WriteOnly] public NativeArray<float4> Neighbours;
        [WriteOnly] public NativeArray<float3> Velocities;

        public void Execute(int index)
        {
            var spider = States[index];

            var counts = spider.Active != 0 && spider.Clip != (int)SpiderClip.Dead;

            Neighbours[index] = new float4(spider.Position,
                counts ? Tuning[spider.Kind].BodyRadius * spider.Scale : 0f);

            Velocities[index] = counts ? spider.Velocity : float3.zero;
        }
    }

    /// <summary>
    /// Собирает матрицы и состояния анимации одного вида в плотные массивы под инстансинг.
    ///
    /// Отсев по пирамиде видимости здесь, а не в Unity: RenderMeshInstanced отсекает
    /// партию целиком по общим границам, и толпа за спиной игрока рисовалась бы вся,
    /// если хоть одна особь попала в кадр. Проверка на особь стоит шесть скалярных
    /// произведений и убирает из отрисовки обычно больше половины уровня.
    /// </summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderRenderJob : IJob
    {
        [ReadOnly] public NativeArray<SpiderState> States;
        [ReadOnly] public NativeArray<SpiderTuning> Tuning;
        [ReadOnly] public NativeArray<float4> FrustumPlanes;

        [WriteOnly] public NativeArray<float4x4> Matrices;
        [WriteOnly] public NativeArray<float4> AnimState;
        [WriteOnly] public NativeArray<float4> Tints;

        /// <summary>Одна ячейка: сколько особей попало в буферы.</summary>
        public NativeArray<int> Count;

        public int Kind;
        public float4x4 LocalToWorld;

        /// <summary>Радиус описанной сферы особи в мире — для отсева по пирамиде.</summary>
        public float CullRadius;

        public float3 CameraWorld;
        public float MaxDistanceSq;

        public void Execute()
        {
            var written = 0;
            var capacity = Matrices.Length;

            for (var i = 0; i < States.Length && written < capacity; i++)
            {
                var spider = States[i];

                if (spider.Active == 0 || spider.Kind != Kind) continue;

                var world = math.transform(LocalToWorld, spider.Position);

                if (math.lengthsq(world - CameraWorld) > MaxDistanceSq) continue;
                if (!InFrustum(world)) continue;

                var tuning = Tuning[Kind];

                var row = spider.Clip == (int)SpiderClip.Attack ? tuning.AttackRow
                    : spider.Clip == (int)SpiderClip.Dead ? tuning.DeadRow
                    : tuning.WalkRow;

                Matrices[written] = math.mul(LocalToWorld,
                    float4x4.TRS(spider.Position, quaternion.RotateY(spider.Yaw), spider.Scale));

                AnimState[written] = new float4(row.x, row.y, spider.Phase, row.z);
                Tints[written] = new float4(spider.Tint, spider.Tint, spider.Tint, 1f);

                written++;
            }

            Count[0] = written;
        }

        private bool InFrustum(float3 point)
        {
            for (var i = 0; i < FrustumPlanes.Length; i++)
            {
                var plane = FrustumPlanes[i];

                if (math.dot(plane.xyz, point) + plane.w < -CullRadius) return false;
            }

            return true;
        }
    }

    /// <summary>Помечает мёртвыми всех, кто попал в сферу. Зовётся взрывом гранаты.</summary>
    [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
    public struct SpiderDamageJob : IJobParallelFor
    {
        public NativeArray<SpiderState> States;

        /// <summary>Центр в локальных координатах генерации.</summary>
        public float3 Center;

        public float RadiusSq;
        public int Damage;

        /// <summary>
        /// Отметки убитых, по одной ячейке на особь. Массив, а не счётчик: джоб
        /// параллельный, и общий счётчик потребовал бы атомарного сложения ради числа,
        /// которое всё равно нужно раз в выстрел. Сложить отметки дешевле.
        /// </summary>
        [WriteOnly] public NativeArray<int> Killed;

        public void Execute(int index)
        {
            var spider = States[index];

            if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) return;
            if (math.lengthsq(spider.Position - Center) > RadiusSq) return;

            spider.Health -= Damage;

            if (spider.Health <= 0)
            {
                spider.Clip = (int)SpiderClip.Dead;
                spider.Phase = 0f;
                spider.Timer = 0f;
                spider.Velocity = float3.zero;

                Killed[index] = 1;
            }

            States[index] = spider;
        }
    }
}
