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

        /// <summary>
        /// Кувырок трупа, радианы в секунду по трём осям.
        ///
        /// Мёртвая особь больше не переключает клип и не ложится на месте: она летит
        /// от взрыва и кувыркается. Физики здесь нет, только интегрирование — на восьми
        /// сотнях особей PhysX не нужен и не потянет.
        /// </summary>
        public float3 Spin;

        /// <summary>
        /// Устойчивое случайное число особи, 0..1. Не меняется за её жизнь.
        ///
        /// Нужно, чтобы делить толпу на подмножества СТАБИЛЬНО. Например, сдерживание
        /// ближнего круга: если решать каждый кадр заново, кого пустить к игроку, толпа
        /// начнёт мигать — одни и те же особи то рвутся вперёд, то отступают.
        /// </summary>
        public float Rank;

        /// <summary>
        /// Сколько секунд этой особи ещё бежать от вспышки.
        ///
        /// Паника — свойство ОСОБИ, а не точки пространства, и это не мелочь.
        /// Пока она проверялась сферой вокруг генератора, край сферы работал стенкой:
        /// особь выбегала за него, тут же переставала паниковать и разворачивалась
        /// обратно к игроку. Замер показывал это в чистом виде — в сфере оставалось
        /// столько же, сколько было (1180 до вспышки, 1165 через десять секунд),
        /// менялось только распределение внутри: из ближнего круга в кольцо.
        /// </summary>
        public float Flee;

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

        /// <summary>+1, если модель смотрит в плюс Z, и -1, если в минус. См. SpiderKind.FacesMinusZ.</summary>
        public float FacingSign;

        public float WalkLength;
        public float AttackLength;
        public float DeadLength;
        public float IdleLength;

        /// <summary>С какого расстояния засада срывается.</summary>
        public float LurkTrigger;

        /// <summary>Где в текстуре анимации лежит клип: x — первая строка, y — кадров, z — кольцевой ли.</summary>
        public float3 WalkRow;
        public float3 AttackRow;
        public float3 DeadRow;
        public float3 IdleRow;
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

        /// <summary>
        /// Низ и верх ОСИ капсулы игрока по высоте, в тех же координатах.
        ///
        /// Толпа целится в тело, а не в точку трансформа: у стенда трансформ стоит
        /// на уровне ГЛАЗ, и паук на полу мерил расстояние по диагонали до макушки —
        /// больше юнита из замеренного расстояния были чистой высотой. Точка
        /// прицеливания теперь едет по этому отрезку вслед за высотой особи, поэтому
        /// паук на полу меряет почти по горизонтали, паук на своде — до макушки,
        /// и <see cref="SpiderTuning.AttackRange"/> означает ровно расстояние
        /// между телами, а не гипотенузу.
        /// </summary>
        public float TargetLow;

        public float TargetHigh;

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

        /// <summary>
        /// Сколько особей сейчас в ближнем круге и сколько туда пускать.
        ///
        /// Ограничение нужно не ради стоимости, а ради картинки: без него вся орда
        /// сжимается в кольцо вокруг игрока и упирается в него сплошной стеной хитина,
        /// в которой не различить ни отдельной твари, ни уровня за ней. Замер это и
        /// показал — в коридоре в кадре было 690 особей из 800, а породы не видно вовсе.
        /// Лишние держатся дальше и ждут своей очереди.
        /// </summary>
        public int NearCount;

        public int NearCap;

        /// <summary>Радиус ближнего круга, юниты.</summary>
        public float NearRadius;

        /// <summary>Затухание кувырка трупа, доля в секунду.</summary>
        public float CorpseDrag;

        /// <summary>
        /// Паника: центр сферы, из которой толпа разбегается, и её радиус в квадрате.
        ///
        /// Ставится вспышкой света в районе. Направление берётся ОБРАТНОЕ полю потока,
        /// а не «прочь от центра»: поле знает, где в породе есть проход, а прямое
        /// направление от центра упирается в первую же стену, и толпа вжималась бы
        /// в неё, вместо того чтобы разбегаться по ходам.
        /// </summary>
        public float3 PanicCentre;
        public float PanicRadiusSq;
        public int PanicActive;

        /// <summary>
        /// Сколько секунд паники ещё осталось. Столько же и бежать той особи,
        /// которую вспышка накрыла: её таймер идёт вместе с общим, поэтому
        /// выбежавшая за край сферы бежит до конца, а не разворачивается на границе.
        /// </summary>
        public float PanicLeft;

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

            if (onField)
            {
                spider.Up = math.normalizesafe(
                    math.lerp(spider.Up, normal, math.saturate(DeltaTime * 8f)), normal);
            }
            else if (Field.TryFindSurface(spider.Position, hover, out var anchor))
            {
                // Особь вынесло за край размеченной полосы. Это не «она где-то не там»,
                // это приговор: держаться не за что, и Advance откатывает ей КАЖДЫЙ шаг,
                // сколько бы кадров ни прошло. Поэтому не идём дальше по поведению,
                // а подтягиваем её обратно к камню — и только потом она снова живёт.
                //
                // Тянем, а не переставляем: скачок на два юнита в кадре читается
                // как телепорт, а особь в давке видна вплотную.
                var back = anchor - spider.Position;
                var reachStep = tuning.MoveSpeed * spider.Scale * 2f * DeltaTime;

                spider.Position += math.normalizesafe(back) * math.min(math.length(back), reachStep);
                spider.Velocity = float3.zero;
                spider.Clip = (int)SpiderClip.Walk;
                spider.Phase = math.frac(spider.Phase + IdleStride * DeltaTime / stride);

                States[index] = spider;
                return;
            }

            // Прицеливаемся в ближайшую точку оси капсулы, а не в трансформ игрока.
            var aim = new float3(Target.x, math.clamp(spider.Position.y, TargetLow, TargetHigh), Target.z);

            var toTarget = aim - spider.Position;
            var distance = math.length(toTarget);

            // Вспышка накрывает особь ОДИН раз — дальше она бежит по своему таймеру,
            // где бы ни оказалась. Сфера здесь только отбирает, кого накрыло.
            if (PanicActive != 0 && spider.Flee <= 0f &&
                math.lengthsq(spider.Position - PanicCentre) <= PanicRadiusSq)
            {
                spider.Flee = PanicLeft;
            }

            // Паника: свет в районе загорелся, и особь убегает. Раньше засады и удара,
            // потому что перепуганный паук не сидит в засаде и не атакует — иначе
            // застывшие на своде засадники остались бы висеть посреди вспышки.
            if (spider.Flee > 0f)
            {
                spider.Flee -= DeltaTime;

                Flee(ref spider, tuning, flow, onField, hover, stride);

                States[index] = spider;
                return;
            }

            if (UpdateLurk(ref spider, tuning, distance))
            {
                States[index] = spider;
                return;
            }

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

            // Сдерживание ближнего круга: когда у игрока уже толпится больше, чем нужно
            // для картинки, часть особей тормозит и ждёт дальше. Порог по Rank, а не
            // по случайному числу каждый кадр: иначе одни и те же особи то рвутся вперёд,
            // то отступают, и толпа мерцает.
            var crowded = NearCap > 0 && NearCount > NearCap &&
                          spider.Rank > (float)NearCap / NearCount &&
                          distance < NearRadius;

            var desired = float3.zero;

            if (crowded)
            {
                // Не разворачиваем и не отгоняем — просто перестаём тянуть вперёд.
                // Разворот читался бы как испуг, а орда не пугается.
                spider.Velocity = math.lerp(spider.Velocity, float3.zero, math.saturate(DeltaTime * 4f));

                desired += Flock(index, ref spider, radius);
                desired = math.normalizesafe(desired - spider.Up * math.dot(desired, spider.Up));

                spider.Velocity = math.lerp(spider.Velocity, desired * (tuning.MoveSpeed * 0.25f),
                    math.saturate(DeltaTime * Acceleration));

                Advance(ref spider, tuning, hover, stride, onField);

                States[index] = spider;
                return;
            }

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

        /// <summary>
        /// Бегство от вспышки света.
        ///
        /// Направление — обратное полю потока, то есть ровно прочь от игрока по проходимым
        /// ходам. Клип остаётся беговым: отдельная анимация паники пака не даёт, а бегущий
        /// задом наперёд паук читался бы как ошибка ориентации — поэтому особь именно
        /// РАЗВОРАЧИВАЕТСЯ и убегает, а не пятится.
        ///
        /// Расталкивание на время паники выключено намеренно: в давке оно гасит скорость
        /// впятеро, а вся ценность момента в том, что толпа брызжет в стороны быстро.
        /// Прижим к поверхности при этом остаётся — иначе бегущие отрывались бы от камня.
        /// </summary>
        private void Flee(ref SpiderState spider, SpiderTuning tuning, float3 flow, bool onField,
            float hover, float stride)
        {
            var away = onField ? -flow : float3.zero;

            // Вне размеченных клеток поля нет, и убегать не по чему: расходимся от центра
            // паники напрямую. Это тот же запасной путь, что и у обычного движения.
            if (math.lengthsq(away) < 1e-6f) away = spider.Position - PanicCentre;

            var up = spider.Up;
            var tangent = away - up * math.dot(away, up);

            // «Прочь от игрока» и «по поверхности» — разные вещи, и для особи НА СВОДЕ
            // над игроком они противоположны: прочь это прямо вверх, в камень, а от
            // направления после проекции на потолок остаётся ноль. Такая особь висела
            // над игроком всю панику с ненулевой скоростью и нулевым сдвигом — она
            // и составляла тот осадок, который игрок видел вместо разбегания.
            //
            // Спрашиваем тогда у самой заливки, куда с этой клетки есть ход подальше
            // от игрока: поле знает про породу, а вычитание нормали — нет.
            if (math.lengthsq(tangent) < 0.04f && Field.TryEscape(spider.Position, up, out var escape))
            {
                tangent = escape;
            }

            away = math.normalizesafe(tangent);

            // Скорость выше обычной: разбегание должно читаться как рывок, а не как
            // разворот и уход шагом. Множитель поднят с 1.6 до 2.2 по той же причине,
            // по которой удлинена сама паника, — за пять секунд на прежней скорости
            // район покидали единицы.
            spider.Velocity = math.lerp(spider.Velocity, away * (tuning.MoveSpeed * spider.SpeedScale * 2.2f),
                math.saturate(DeltaTime * Acceleration));

            spider.Clip = (int)SpiderClip.Walk;

            Advance(ref spider, tuning, hover, stride, onField);
        }

        /// <summary>
        /// Труп: летит от взрыва, кувыркается, гаснет и уходит в камень.
        ///
        /// Раньше он просто менял клип и оставался лежать там же. Замер этого не ловил
        /// вовсе, а кадр показал главное: взрыв убивал сто восемнадцать особей из восьми
        /// сотен, и увидеть это было НЕВОЗМОЖНО — трупы лежали вперемешку с живыми
        /// в той же позе. Убийство было событием без единого следа.
        /// </summary>
        private void UpdateCorpse(ref SpiderState spider, SpiderTuning tuning)
        {
            spider.Timer += DeltaTime;

            if (spider.Phase < 1f)
            {
                spider.Phase = math.min(1f, spider.Phase + DeltaTime / math.max(0.05f, tuning.DeadLength));
            }

            // Полёт с затуханием. Гравитации нет намеренно: труп летит по камню, а не
            // в пустоте, и прижим к поверхности всё равно вернёт его на стену — падать
            // ему некуда, а видимая часть эффекта это именно отброс, а не падение.
            var drag = math.saturate(DeltaTime * CorpseDrag);

            spider.Velocity = math.lerp(spider.Velocity, float3.zero, drag);
            spider.Spin = math.lerp(spider.Spin, float3.zero, drag);

            spider.Position += spider.Velocity * DeltaTime;

            var spin = math.length(spider.Spin);

            if (spin > 1e-4f)
            {
                spider.Rotation = math.mul(
                    quaternion.AxisAngle(spider.Spin / spin, spin * DeltaTime), spider.Rotation);
            }

            // Держим на поверхности, чтобы отброшенный труп не улетел в породу.
            Field.SampleAt(spider.Position, out _, out var normal, out var depth, out var valid);

            if (valid && depth < 0f) spider.Position -= normal * depth;

            if (spider.Timer >= tuning.CorpseLinger) spider.Active = 0;
        }

        /// <summary>
        /// Засада: особь висит неподвижно, пока игрок не подойдёт, и срывается.
        ///
        /// Это единственное состояние, в котором особь НЕ движется к игроку, и ради него
        /// вернули выброшенный было клип стояния: бег на нижней границе скорости засаду
        /// не изображает — висящая на своде тварь, перебирающая лапами, читается как глюк.
        /// </summary>
        private bool UpdateLurk(ref SpiderState spider, SpiderTuning tuning, float distance)
        {
            if (spider.Clip != (int)SpiderClip.Idle) return false;

            spider.Phase = math.frac(spider.Phase + DeltaTime / math.max(0.05f, tuning.IdleLength));
            spider.Velocity = float3.zero;

            if (TargetValid == 0 || distance > tuning.LurkTrigger) return true;

            // Сорвалась. Толчок ОТ поверхности, а не к игроку: с потолка тварь должна
            // упасть, а не спикировать — падение читается как засада, а доводка
            // до игрока дальше сделается обычным полем потока.
            spider.Clip = (int)SpiderClip.Walk;
            spider.Phase = 0f;
            spider.Velocity = -spider.Up * (tuning.MoveSpeed * 1.5f);

            return false;
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

            if (TargetValid != 0 && distance > 1e-3f)
            {
                spider.Rotation = Turn(spider.Rotation, toTarget, spider.Up, tuning.FacingSign,
                    math.radians(tuning.TurnSpeed) * DeltaTime);
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

            // Сначала пробуем шаг ЦЕЛИКОМ, и только если он не проходит — по осям.
            //
            // Порядок здесь не косметический. Разбор по осям запрещает диагональ там,
            // где она разрешена: на наклонной стенке шаг вдоль X в одиночку уходит
            // в породу (подниматься надо одновременно), а шаг вдоль Y в одиночку —
            // в пустоту, откуда прижим тут же возвращает. Обе оси по отдельности
            // «нельзя», вместе — можно. Замер ловил это как застывшую толпу
            // с НЕНУЛЕВОЙ скоростью: 129 особей стояли у игрока все десять секунд
            // паники при скорости 2.4 и сдвиге 0.05 юнита в секунду.
            //
            // Дороже это не стало: когда шаг проходит целиком (а это обычный случай),
            // считается одна проверка вместо трёх.
            if (!Field.CanStand(spider.Position + step))
            {
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
            }

            var previous = spider.Position;

            spider.Position += step;

            Field.SampleAt(spider.Position, out _, out var normal, out var depth, out var valid);

            if (valid)
            {
                // Нормаль для ПОВОРОТА сглаживается, а для прижима берётся сырая.
                //
                // Разделение не косметическое. Сырая нормаль скачет на границах клеток —
                // сетка по юниту, а в коридоре пол и стена стоят под прямым углом, —
                // и если вести по ней поворот, особь дёргается каждый раз, когда
                // переступает границу. Прижим же обязан идти по сырой: сглаженная
                // отстаёт от поверхности и утапливает особь в углах.
                spider.Up = math.normalizesafe(
                    math.lerp(spider.Up, normal, math.saturate(DeltaTime * 8f)), normal);

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

            // Стоящая особь всё равно доворачивается «ногами к камню»: иначе, переползая
            // с пола на стену, она едет по ней боком. Направление при этом берётся
            // прежнее — Turn сам приведёт его к новой касательной плоскости.
            var heading = speed > 0.05f ? spider.Velocity : math.forward(spider.Rotation) * tuning.FacingSign;

            spider.Rotation = Turn(spider.Rotation, heading, spider.Up, tuning.FacingSign,
                math.radians(tuning.TurnSpeed) * DeltaTime);

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

        /// <summary>
        /// Доворот к направлению движения, не быстрее заданного.
        ///
        /// Направление обязательно приводится к касательной плоскости, и вырожденные
        /// случаи разбираются явно. Без этого особь крутится как заведённая: стоит
        /// направлению оказаться почти вдоль нормали — а на стене это происходит
        /// постоянно, — как LookRotation начинает выдавать произвольный поворот,
        /// и каждый кадр новый.
        /// </summary>
        private static quaternion Turn(quaternion current, float3 direction, float3 up, float facing,
            float maxRadians)
        {
            var tangent = direction - up * math.dot(direction, up);

            if (math.lengthsq(tangent) < 1e-6f)
            {
                // Направление выродилось — держим прежнее, спроецированное на ту же плоскость.
                tangent = math.forward(current) * facing;
                tangent -= up * math.dot(tangent, up);
            }

            if (math.lengthsq(tangent) < 1e-6f)
            {
                // И оно выродилось: любой касательный вектор лучше, чем мусор.
                tangent = math.cross(up, math.abs(up.y) > 0.9f ? math.right() : math.up());
            }

            // facing разворачивает модель, а не движение: у пака Spiders голова смотрит
            // в минус Z, и без этого толпа бежит на игрока задом.
            var target = quaternion.LookRotationSafe(math.normalize(tangent) * facing, up);

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

            // Ветвление, а не тернарник. Тернарник Burst превращает в select, то есть
            // считает ОБЕ стороны, — а значит лезет в Tuning[spider.Kind] и для пустых
            // слотов тоже. Пока пустой слот хранит нули, это сходит с рук, но стоит
            // там оказаться мусору, и джоб падает по выходу за границу массива.
            if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead)
            {
                Neighbours[index] = new float4(spider.Position, 0f);
                Velocities[index] = float3.zero;
                return;
            }

            Neighbours[index] = new float4(spider.Position,
                Tuning[spider.Kind].BodyRadius * spider.Scale);

            Velocities[index] = spider.Velocity;
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

                var row = spider.Clip switch
                {
                    (int)SpiderClip.Attack => tuning.AttackRow,
                    (int)SpiderClip.Dead => tuning.DeadRow,
                    (int)SpiderClip.Idle => tuning.IdleRow,
                    _ => tuning.WalkRow
                };

                var scale = spider.Scale;
                var tint = spider.Tint;

                if (spider.Clip == (int)SpiderClip.Dead)
                {
                    // Труп гаснет и усаживается к концу срока лежания.
                    //
                    // Не прозрачностью: материал непрозрачный, и переводить всю толпу
                    // в прозрачную очередь ради последней секунды жизни трупа значит
                    // платить сортировкой и перерисовкой за каждого живого паука.
                    // Усадка с потемнением читается так же, а стоит нуля.
                    var life = math.saturate(spider.Timer / math.max(0.05f, tuning.CorpseLinger));
                    var fade = math.saturate((life - 0.6f) / 0.4f);

                    scale *= 1f - fade * 0.85f;
                    tint *= 1f - fade * 0.7f;
                }

                Matrices[written] = math.mul(LocalToWorld,
                    float4x4.TRS(spider.Position, spider.Rotation, scale));

                AnimState[written] = new float4(row.x, row.y, spider.Phase, row.z);
                Tints[written] = new float4(tint, tint, tint, 1f);

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

        /// <summary>Сила отброса в эпицентре, юнитов в секунду. У края сферы спадает до нуля.</summary>
        public float Impulse;

        /// <summary>Сид для разброса кувырка. Одинаковый кувырок у всех читается как ошибка.</summary>
        public uint Seed;

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

            var away = spider.Position - Center;
            var distanceSq = math.lengthsq(away);

            if (distanceSq > RadiusSq) return;

            spider.Health -= Damage;

            if (spider.Health > 0)
            {
                States[index] = spider;
                return;
            }

            spider.Clip = (int)SpiderClip.Dead;
            spider.Phase = 0f;
            spider.Timer = 0f;

            // Отброс: у эпицентра полный, у края сферы нулевой. Без него смерть сотни
            // особей не видна в кадре вовсе — они просто остаются лежать там же,
            // вперемешку с живыми.
            var falloff = 1f - math.sqrt(distanceSq / math.max(1e-4f, RadiusSq));

            var direction = math.normalizesafe(away, spider.Up);

            // Сид обязан быть ненулевым: Unity.Mathematics.Random на нуле бросает
            // исключение, а в джобе под Burst это валит весь проход целиком.
            var random = new Random(math.max(1u, Seed + (uint)index * 747796405u + 1u));

            spider.Velocity = (direction + random.NextFloat3Direction() * 0.35f) * (Impulse * falloff);

            // Кувырок тем сильнее, чем ближе к эпицентру: у края особь оседает,
            // у центра её крутит.
            spider.Spin = random.NextFloat3Direction() * (12f * falloff);

            Killed[index] = 1;

            States[index] = spider;
        }
    }
}
