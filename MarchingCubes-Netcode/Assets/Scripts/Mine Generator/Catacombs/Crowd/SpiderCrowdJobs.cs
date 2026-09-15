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

        /// <summary>
        /// Полный поворот, а не угол вокруг вертикали.
        ///
        /// Угла хватало, пока особь ходила по полу. Паук ползает по стенам и потолку,
        /// и там «верх» у него свой — нормаль поверхности; одним рысканьем такое
        /// не выразить.
        /// </summary>
        public quaternion Rotation;

        /// <summary>Нормаль поверхности, за которую особь держится.</summary>
        public float3 Up;

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

        /// <summary>На сколько центр тела отстоит от поверхности. Примерно полтолщины особи.</summary>
        public float Hover;

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
    /// нормаль поверхности — плоскость, в которой всё это происходит.
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

            // Числа вида заданы для меша в натуральную величину, а особь отмасштабирована.
            // Без этого крупный паук расталкивается как мелкий, бьёт с дистанции мелкого
            // и скользит лапами: шаг остаётся коротким, а проходит он за него втрое больше.
            var radius = tuning.BodyRadius * spider.Scale;
            var hover = tuning.Hover * spider.Scale;
            var reach = tuning.AttackRange * spider.Scale;
            var stride = math.max(0.05f, tuning.StrideLength * spider.Scale);

            if (spider.Clip == (int)SpiderClip.Dead)
            {
                UpdateCorpse(ref spider, tuning);
                States[index] = spider;
                return;
            }

            Field.SampleAt(spider.Position, out var flow, out var normal, out var depth, out var onField);

            if (onField) spider.Up = normal;

            var toTarget = Target - spider.Position;
            var distance = math.length(toTarget);

            if (spider.Clip == (int)SpiderClip.Attack)
            {
                UpdateAttack(ref spider, tuning, toTarget, distance, normal, depth, hover, onField);
                States[index] = spider;
                return;
            }

            if (TargetValid != 0 && distance <= reach)
            {
                spider.Clip = (int)SpiderClip.Attack;
                spider.Phase = 0f;
                States[index] = spider;
                return;
            }

            var desired = float3.zero;

            if (onField) desired += flow;

            // Вблизи игрока поле потока вырождается: в клетке-источнике направление ноль,
            // и толпа, дойдя до неё, топталась бы вокруг, а не давила.
            if (TargetValid != 0 && distance < CloseRange) desired += math.normalizesafe(toTarget) * 1.5f;

            // Если особь оказалась вне размеченных клеток (уровень перестроили, взрыв
            // вынес стену), она всё равно должна двигаться к игроку, а не замирать.
            if (!onField && TargetValid != 0) desired += math.normalizesafe(toTarget);

            desired += Flock(index, ref spider, radius);

            // Всё движение идёт В ПЛОСКОСТИ ПОВЕРХНОСТИ: составляющая вдоль нормали
            // означала бы отрыв от камня или вход в него, а прижимом занимается
            // отдельный шаг, по замеренной глубине.
            var up = spider.Up;
            desired = math.normalizesafe(desired - up * math.dot(desired, up));

            var wanted = desired * (tuning.MoveSpeed * spider.SpeedScale);

            spider.Velocity = math.lerp(spider.Velocity, wanted, math.saturate(DeltaTime * Acceleration));

            Advance(ref spider, tuning, hover, stride, onField);

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
            float3 normal, float depth, float hover, bool onField)
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

            var up = spider.Up;
            var forward = math.normalizesafe(toTarget - up * math.dot(toTarget, up), math.forward(spider.Rotation));

            if (TargetValid != 0 && distance > 1e-3f)
            {
                spider.Rotation = Turn(spider.Rotation, forward, up, math.radians(tuning.TurnSpeed) * DeltaTime);
            }

            if (onField) Cling(ref spider, normal, depth, hover);
        }

        /// <summary>Расталкивание, выравнивание и сбивание в кучу — три классических правила стаи.</summary>
        private float3 Flock(int index, ref SpiderState spider, float radius)
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

                    var distanceSq = math.lengthsq(delta);
                    if (distanceSq < 1e-6f) continue;

                    var touch = data.w + radius;

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
            result += math.normalizesafe(cohesion / neighbours - spider.Position) * CohesionWeight;

            return result;
        }

        /// <summary>Шаг по поверхности, прижим к ней, поворот и фаза анимации от пройденного пути.</summary>
        private void Advance(ref SpiderState spider, SpiderTuning tuning, float hover, float stride, bool onField)
        {
            var step = spider.Velocity * DeltaTime;

            // Проверка прохода по осям, чтобы особь скользила вдоль препятствия,
            // а не вставала в него. Оси мировые, а не касательные: сетка мировая,
            // и проверять надо в её системе.
            if (!Field.CanStand(spider.Position + new float3(step.x, 0f, 0f)))
            {
                step.x = 0f;
                spider.Velocity.x *= 0.5f;
            }

            if (!Field.CanStand(spider.Position + new float3(0f, step.y, 0f)))
            {
                step.y = 0f;
                spider.Velocity.y *= 0.5f;
            }

            if (!Field.CanStand(spider.Position + new float3(0f, 0f, step.z)))
            {
                step.z = 0f;
                spider.Velocity.z *= 0.5f;
            }

            var previous = spider.Position;

            spider.Position += step;

            Field.SampleAt(spider.Position, out _, out var normal, out var depth, out var valid);

            if (valid)
            {
                spider.Up = normal;
                Cling(ref spider, normal, depth, hover);
            }
            else
            {
                // Шаг завёл туда, где поверхности рядом нет вовсе — откатываем целиком.
                //
                // Так бывает от диагонали: проверка прохода идёт по осям по отдельности,
                // и бывает, что по X можно и по Z можно, а вместе они уводят за угол
                // в породу. Просто вытолкнуть оттуда нельзя — вытаскивать не за что,
                // нормали в той точке не существует. Замер ловил ровно это: девять
                // процентов толпы стояло в камне, и каждый следующий прогон давал
                // те же самые 73 особи, сколько прижим ни усиливай.
                spider.Position = previous;
                spider.Velocity = float3.zero;
            }

            var speed = math.length(step) / math.max(1e-5f, DeltaTime);

            if (speed > 0.05f)
            {
                var up = spider.Up;
                var forward = math.normalizesafe(spider.Velocity - up * math.dot(spider.Velocity, up),
                    math.forward(spider.Rotation));

                spider.Rotation = Turn(spider.Rotation, forward, up, math.radians(tuning.TurnSpeed) * DeltaTime);
            }
            else
            {
                // Даже стоя особь должна довернуться «ногами к камню»: иначе, переползая
                // с пола на стену, она едет по ней боком.
                spider.Rotation = Turn(spider.Rotation, math.forward(spider.Rotation), spider.Up,
                    math.radians(tuning.TurnSpeed) * DeltaTime);
            }

            // Фаза ведётся пройденным путём, а не временем: иначе лапы скользят по камню,
            // и чем сильнее особь тормозит в давке, тем заметнее. Нижняя граница нужна
            // стоящим — замершая насмерть модель читается как сломанная анимация,
            // и она же заменяет выброшенный клип стояния.
            spider.Phase = math.frac(spider.Phase + math.max(speed, IdleStride) * DeltaTime / stride);
        }

        /// <summary>
        /// Прижимает особь к поверхности: держит центр тела на hover от камня.
        ///
        /// Не мгновенно, а с постоянной времени: скачок по нормали в момент смены клетки
        /// читается как рывок, а на переходе с пола на стену их было бы много подряд.
        /// </summary>
        private void Cling(ref SpiderState spider, float3 normal, float depth, float hover)
        {
            // Снаружи подтягиваемся к поверхности плавно: скачок по нормали в момент
            // смены клетки читается как рывок, а на переходе с пола на стену их было бы
            // много подряд.
            //
            // А вот ИЗНУТРИ выталкиваем сразу и целиком. Сглаживать тут нечего: пока
            // поправка размазана по кадрам, особь эти кадры стоит в камне, и в давке
            // у стены соседи успевают затолкать её глубже, чем прижим вытягивает.
            // Замер на этом и поймал: при плавном выталкивании в породе оказывалось
            // девять процентов толпы, при мгновенном — ноль.
            var rate = depth < 0f ? 1f : math.saturate(DeltaTime * 16f);

            spider.Position -= normal * ((depth - hover) * rate);
        }

        /// <summary>Доворот к цели не быстрее заданного.</summary>
        private static quaternion Turn(quaternion current, float3 forward, float3 up, float maxRadians)
        {
            var target = quaternion.LookRotationSafe(forward, up);

            // Угол между поворотами через скалярное произведение кватернионов: дешевле,
            // чем разбирать их на оси.
            var dot = math.abs(math.dot(current.value, target.value));
            var angle = 2f * math.acos(math.clamp(dot, -1f, 1f));

            if (angle <= maxRadians || angle < 1e-4f) return target;

            return math.slerp(current, target, maxRadians / angle);
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
    /// если хоть одна особь попала в кадр.
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
                if (!InFrustum(world, spider.Scale)) continue;

                var tuning = Tuning[Kind];

                var row = spider.Clip == (int)SpiderClip.Attack ? tuning.AttackRow
                    : spider.Clip == (int)SpiderClip.Dead ? tuning.DeadRow
                    : tuning.WalkRow;

                Matrices[written] = math.mul(LocalToWorld,
                    float4x4.TRS(spider.Position, spider.Rotation, spider.Scale));

                AnimState[written] = new float4(row.x, row.y, spider.Phase, row.z);
                Tints[written] = new float4(spider.Tint, spider.Tint, spider.Tint, 1f);

                written++;
            }

            Count[0] = written;
        }

        private bool InFrustum(float3 point, float scale)
        {
            var radius = CullRadius * scale;

            for (var i = 0; i < FrustumPlanes.Length; i++)
            {
                var plane = FrustumPlanes[i];

                if (math.dot(plane.xyz, point) + plane.w < -radius) return false;
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
