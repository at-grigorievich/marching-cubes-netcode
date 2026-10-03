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

        /// <summary>
        /// Окраска поверх альбедо. Белая у рядовых; у элиты и у финального роя своя,
        /// чтобы их было видно в толпе не только по размеру.
        ///
        /// Умножением, а не добавкой: светлая добавка уже однажды сделала толпу
        /// мультяшной (грабли №21, №22), а умножение только сдвигает тон.
        /// </summary>
        public float3 Hue;

        /// <summary>
        /// Порядковый номер появления. Слот переиспользуется, как только труп исчез, —
        /// и ссылка «вот эта элита», хранящая только слот, через три секунды после
        /// её смерти указывала бы на случайного новорождённого паука.
        /// </summary>
        public int Serial;

        /// <summary>Элита: не уходит отливом и не прячется в засаду.</summary>
        public int Elite;

        /// <summary>
        /// Высота над камнем вдоль нормали: особь лезет по спинам соседей или по игроку.
        /// <see cref="Position"/> хранится уже поднятой — так её видят соседи и отрисовка,
        /// а поведение снимает подъём и считает «на камне».
        /// </summary>
        public float Climb;

        /// <summary>
        /// Ярус: 0 — на камне, иначе ярус того, на чьей спине лежит, плюс один. Ограничивает
        /// высоту кучи числом ярусов, а не высотой: см. SpiderMoveJob.PileTiers.
        /// </summary>
        public int Tier;

        /// <summary>
        /// Скорость по высоте над камнем: плюс — лезет, минус — падает или сходит. Падение
        /// ускоряется, подъём идёт по крутизне чужой спины на ходу — см. SpiderMoveJob.UpdateClimb.
        /// </summary>
        public float ClimbVel;

        /// <summary>
        /// Наклон тела носом вверх, радианы. Только для отрисовки: лезущий на чужую спину
        /// задирает голову, спускающийся опускает. Без наклона подъём читался как всплытие.
        /// </summary>
        public float Pitch;

        /// <summary>
        /// Отлив: волна спала, и лишние отступают в темноту, а не стоят кольцом вокруг
        /// игрока. 1 — отходит (бежит, пока тикает <see cref="Flee"/>, но шагом, а не
        /// в панике); 2 — уходит в щель: стоит, оседает и гаснет, отсчитывая
        /// <see cref="Timer"/>, после чего толпа освобождает слот.
        /// </summary>
        public int Ebb;

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
        /// Разорван взрывом на куски: 1 — да. Куски разлетаются в шейдере (CaveCrowd, Shatter)
        /// от возраста трупа — <see cref="Timer"/>; кувырка целиком у разорванного нет, а лежит
        /// он <see cref="SpiderHash.GibSeconds"/>, а не срок трупа своего вида.
        /// </summary>
        public int Gibbed;

        /// <summary>
        /// Сколько юнитов по мировой вертикали от разорванного до камня под ним. Снимается
        /// при разрыве (<see cref="SpiderCrowd.DamageAt(UnityEngine.Vector3, float, int, System.Collections.Generic.List{UnityEngine.Vector3})"/>)
        /// по полю плотности: куски падают вниз по миру и ложатся на пол, а не на ту
        /// поверхность, на которой сидел паук, — иначе куски паука со свода «падали» бы
        /// на свод и висели там.
        /// </summary>
        public float GibDrop;

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

        /// <summary>
        /// Сколько здоровья осталось, в HP (<see cref="CombatUnits"/>). Дробное: урон пушек
        /// с множителями и урон со временем в целые попадания не укладываются.
        /// </summary>
        public float Health;

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

        /// <summary>
        /// Высота особи в позе бега, в единицах меша: от точки касания до верха тела
        /// (<see cref="SpiderKind.PoseHeight"/>). По ней решается, на какой высоте верхний
        /// ложится на нижнего и кто с кем в одном ярусе.
        /// </summary>
        public float Height;

        /// <summary>
        /// Доля радиуса, которую занимает само тело — без лап. Тела друг в друга не входят;
        /// лапы переплетаются, и это правильно.
        /// </summary>
        public const float CoreShare = 0.5f;

        /// <summary>
        /// На какой доле высоты нижнего стоят лапы верхнего: чуть ниже верха тела, так что
        /// брюхо верхнего (у пака оно почти у самых лап) ложится ему на спину.
        /// </summary>
        public const float BackShare = 0.8f;

        /// <summary>
        /// Выше этого над камнем особь — наездник: держится на чужих спинах, и на стоящем
        /// на камне вровень — тоже. Общий порог для джоба и для замеров.
        ///
        /// Был 0.3, пока высота бралась из раздутых габаритов. По настоящей позе спина
        /// у мелких видов 0.37, и лежащий на каракурте едва переходил порог: чуть просел —
        /// и перестал держаться.
        /// </summary>
        public const float RideLevel = 0.2f;

        /// <summary>
        /// Насколько сосед может стоять выше или ниже нашего камня — своим камнем, — чтобы считаться
        /// на той же поверхности. Дальше он на другой: на стене над нами или на своде.
        /// </summary>
        public const float LevelTolerance = 0.5f;

        /// <summary>
        /// Во сколько раз опора может быть мельче наездника по радиусу с лапами. Мельче — лапы
        /// наездника дальше её спины и лап, стоять им не на чем, и они свисают в пустоту.
        ///
        /// Разбор поимённо кадра, на котором паук «висел в воздухе» перед камерой: тарантул
        /// бил с подъёма 0.9, стоя на little_spider, — по горбу всё сходилось, но опоры вдвое
        /// мельче его под телом не видно, а его лапы висели над камнем. Мелкие лезут по крупным,
        /// крупные — только по крупным.
        /// </summary>
        public const float SturdyShare = 0.85f;

        /// <summary>
        /// Кусок, в который уходит точка позы при разлёте на куски: 0 — головогрудь (голова
        /// у пака в минус Z), 1 — брюшко, 2-9 — восемь секторов вокруг тела, по лапе на сектор.
        /// Шейдер (CaveCrowd, Shatter) по этому номеру ставит центр куска.
        /// </summary>
        public static int GibChunk(float3 rest, float core)
        {
            if (math.length(rest.xz) < core) return rest.z < 0f ? 0 : 1;

            var turn = math.frac(math.atan2(rest.x, rest.z) / (2f * math.PI) + 1f);

            return 2 + math.clamp((int)math.floor(turn * 8f), 0, 7);
        }

        /// <summary>Докуда от центра соседа тянется плато его горба — см. <see cref="Hump"/>.</summary>
        public static float HumpTop(float theirRadius, float ourRadius) => (theirRadius + ourRadius * 0.5f) * CoreShare;

        /// <summary>
        /// Горб чужой спины: на какой высоте над камнем стоит особь, чей центр в <paramref name="planar"/>
        /// от центра соседа. Пока наше тело хоть наполовину над его телом — на спине; дальше спина
        /// спадает к камню и сходит на нет там, где до его тела дотягиваются только кончики наших лап.
        ///
        /// Раньше опора была ступенькой: коснулся — полная высота спины, отошёл — камень. Наездник
        /// над кончиками чужих лап держался на полной высоте, как над телом, а лезущий поднимался
        /// на неё стоя, перед соседом. В кадре и то, и другое — паук, висящий в воздухе. Разбор
        /// поимённо у игрока: из 77 наездников на теле соседа лежали 27, над его лапами и за их
        /// краем — 48. Горб привязывает высоту к тому, над чем особь стоит.
        /// </summary>
        /// <param name="slope">Крутизна горба в этой точке: подъём на юнит хода к центру соседа.</param>
        public static float Hump(float planar, float theirRadius, float theirHeight, float ourRadius,
            out float slope)
        {
            // Плато — пока наше ядро хоть наполовину над его ядром: с узким плато крупный наездник
            // над мелким проседал в него телом.
            var top = HumpTop(theirRadius, ourRadius);
            var foot = theirRadius * CoreShare + ourRadius;
            var run = math.max(0.05f, foot - top);
            var back = theirHeight * BackShare;

            slope = planar > top && planar < foot ? back / run : 0f;

            return back * math.saturate((foot - planar) / run);
        }

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
        /// <summary>За сколько секунд уходящая отливом особь гаснет в щели.</summary>
        public const float SinkSeconds = 1.2f;

        /// <summary>
        /// Сколько живут куски разорванного паука. Последние 30% съёживаются (см. CaveCrowd, Shatter):
        /// этот срок шейдер получает свойством _GibLife.
        /// </summary>
        public const float GibSeconds = 3f;

        /// <summary>Шаг, которым высота падения кусков упакована в целую часть, юнита.</summary>
        public const float GibDropStep = 1f / 16f;

        /// <summary>
        /// Своё число разорванной особи для разброса кусков, 0..1.
        /// </summary>
        public static float GibSeed(int serial) => math.frac(serial * 0.6180339f) * 0.95f;

        /// <summary>
        /// Высота падения и своё число разорванной особи одним числом — для четвёртого канала
        /// состояния анимации. Свободных каналов в инстансе нет, а новый стоил бы вектора
        /// на особь в буфере, где их и так впритык.
        ///
        /// Число отрицательное: у клипа смерти в этом канале флаг «кольцевой» (больше 0.5),
        /// и он обязан остаться выключенным. Модуль минус один — целая часть это высота
        /// в шагах <see cref="GibDropStep"/>, дробная — своё число. Разбирает CaveCrowd, Shatter.
        /// </summary>
        public static float PackGib(float drop, int serial) =>
            -(1f + math.floor(math.clamp(drop, 0f, 60f) / GibDropStep) + GibSeed(serial));

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

        /// <summary>Высота соседа в мире (с аркой лап) — на какой высоте на него ложиться.</summary>
        [ReadOnly] public NativeArray<float> Heights;

        /// <summary>
        /// Подъём соседа над камнем. Опора — только тот, кто стоит на камне: наездник наездника
        /// не держит, ярусов два. Раньше «на камне ли сосед» угадывалось по его высоте над нашим
        /// камнем, и соседа на бугре пола на полметра выше это правило записывало в наездники.
        /// </summary>
        [ReadOnly] public NativeArray<float> Climbs;

        /// <summary>Ярус соседа, <see cref="SpiderState.Tier"/>.</summary>
        [ReadOnly] public NativeArray<int> Tiers;

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

        /// <summary>
        /// Выше этого особь по спинам соседей не лезет, юниты. Около трёх-четырёх ярусов при спинах
        /// 0.37-0.68; у низкого свода куча срезается ещё и по месту — см. UpdateClimb.
        /// </summary>
        public float MaxClimb;

        /// <summary>
        /// С какой скоростью особь ставит передние лапы на спину, в которую упёрлась, юниты
        /// в секунду. Дальше подъём идёт по крутизне горба на ходу — см. <see cref="UpdateClimb"/>.
        /// </summary>
        public float ClimbRate;

        /// <summary>
        /// Как быстро догоняет спину под собой тот, кого она поднимает: доля разницы в секунду.
        /// Со скоростью лазания верхний ярус отставал от поднявшегося нижнего и оказывался у него внутри.
        /// </summary>
        private const float CarryResponse = 20f;

        /// <summary>С какой долей отставания в секунду лезущая догоняет горб под собой.</summary>
        private const float CatchUp = 6f;

        /// <summary>
        /// Сколько ярусов в куче, считая стоящих на камне. На того, кто уже на верхнем, не лезут.
        ///
        /// Число ярусов, а не высота: жёсткий потолок по высоте сам сажал пауков друг в друга.
        /// Нижний, которого поднимало снизу, уходил выше потолка, верхний за ним не мог и оставался
        /// у него внутри — так сидела большая часть «глубоких» пар при потолке 1.5 и 2.4.
        /// </summary>
        public int PileTiers;

        /// <summary>Ускорение падения, когда опоры не стало, юниты в секунду за секунду.</summary>
        public float Gravity;

        /// <summary>
        /// Насколько сильно упёршаяся особь берёт вбок — в обход затора, на стену и на свод.
        /// </summary>
        public float DetourWeight;

        /// <summary>
        /// Ближе этого к игроку особь идёт на него прямо по своей поверхности, а не по полю
        /// потока. Поле стекает к клетке игрока на полу, и стенные у самого игрока спускались
        /// бы на пол — толпа у игрока лежала бы на полу, а стены пустовали.
        /// </summary>
        public float WallRange;

        /// <summary>
        /// Личное пространство игрока: ближе этого к камере особь не бывает. Вдоль оси
        /// тела держит дистанция удара — ближе её особь уже влезла в игрока.
        /// </summary>
        public float EyeRadius;

        /// <summary>С какой скоростью личное пространство расталкивает влезших, юниты в секунду.</summary>
        public float ShoveSpeed;

        /// <summary>Ближе этого к игроку по горизонтали на спины не лезут: под ногами у него камень.</summary>
        private const float BareRadius = 1.5f;




        /// <summary>
        /// Касание с запасом: сосед ближе этой доли суммы ядер уже держит. Запас нужен, чтобы
        /// контакт не мигал: раздвинутая на ноль особь всё ещё «касается» и не шагает обратно.
        /// </summary>
        private const float ContactMargin = 1.15f;

        /// <summary>Какую долю перекрытия ядер особь раздвигает за кадр со своей стороны.</summary>
        private const float ShoveShare = 0.2f;

        /// <summary>Какая доля толпы — стенолазы: тянется на стены и вверх по ним, к своду.</summary>
        public float ClimberShare;

        /// <summary>Насколько сильно стенолаз тянется вверх по стене.</summary>
        public float ClimberPull;

        /// <summary>
        /// Сколько секунд засада ждёт игрока в среднем. Не дождалась — срывается сама.
        /// </summary>
        public float LurkPatience;

        /// <summary>
        /// Ближе этого к игроку особь лезет на соседей; дальше — только держится на тех,
        /// на ком уже сидит.
        ///
        /// Куча нужна перед игроком, а не в заторах посреди уровня. Там она не давит,
        /// а застревает: нижние стоят в заторе, верхние держатся на них, и пирамида
        /// стоит сама. Разбор к концу стадии: 200 особей у потолка высоты в двадцати
        /// юнитах от игрока, 172 из дальних — на своде, сближение вдвое медленнее,
        /// чем без куч.
        /// </summary>
        public float PileRadius;

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
                // Убитый на куче падает с неё: иначе трупы висели бы в воздухе
                // на высоте тех, кто под ними успел разбежаться.
                Drop(ref spider);
                UpdateCorpse(ref spider, tuning);
                States[index] = spider;
                return;
            }

            // Уходит в щель: стоит на месте и гаснет. Слот освободит толпа, когда
            // таймер дойдёт до конца, — см. SpiderCrowd.UpdateEbb.
            if (spider.Ebb == 2)
            {
                Drop(ref spider);
                spider.Timer += DeltaTime;
                spider.Velocity = float3.zero;
                States[index] = spider;
                return;
            }

            // Высота в толпе. Всё поведение — поле потока, шаг, прижим — считается
            // «на камне», как и раньше; подъём снимается перед ним и кладётся обратно
            // после. Поднятую позицию видят только соседи и отрисовка.
            //
            // Поле нельзя спрашивать с поднятой позиции: в коридоре высотой три
            // с половиной особь, забравшаяся на соседа, оказалась бы ближе к своду,
            // чем к полу, и приняла бы свод за свою поверхность.
            var lifted = spider.Position;
            spider.Position -= spider.Up * spider.Climb;

            var shoved = KeepOut(ref spider, lifted, reach);

            var climbTarget = Behave(index, ref spider, tuning, lifted, radius, hover, reach, stride,
                out var progress, out var incline);

            // Влезший в личное пространство с кучи падает: лежать на голове у игрока нельзя.
            if (shoved) climbTarget = 0f;

            climbTarget = math.min(climbTarget, CraterCap(lifted));

            UpdateClimb(ref spider, climbTarget, radius, progress, incline);

            // Опоры нет и особь на камне — нулевой ярус; пока опора есть, ярус выставило
            // поведение: ярус опоры плюс один.
            if (climbTarget <= 0f && spider.Climb <= SpiderTuning.RideLevel) spider.Tier = 0;

            // Наклон тела по склону: вверх по чужой спине — носом вверх, вниз — носом вниз.
            // Падение наклоняет слабее, иначе сорвавшийся нырял бы головой.
            var run = math.max(math.length(spider.Velocity), 0.5f);
            var pitch = math.clamp(math.atan2(spider.ClimbVel, run), -0.5f, 1.0f);

            spider.Pitch = math.lerp(spider.Pitch, pitch, math.saturate(DeltaTime * 10f));

            spider.Position += spider.Up * spider.Climb;
            States[index] = spider;
        }

        /// <summary>
        /// Личное пространство игрока. Особь, оказавшаяся ближе своей дистанции удара к оси
        /// его тела или ближе <see cref="EyeRadius"/> к камере, сдвигается прочь по своей
        /// поверхности.
        ///
        /// Без этого, стоило зайти в толпу, особи оставались на местах — внутри игрока
        /// и вплотную к камере, — и экран занимали огромные лапы в несколько слоёв.
        /// Пользователь назвал это «куча-мала». Теперь толпа расступается: заходишь —
        /// она раздаётся по сторонам и на стены, как вода вокруг идущего.
        ///
        /// Сдвиг по поверхности, а не прямо от игрока: иначе особь на полу выталкивало бы
        /// вверх, в воздух. И только туда, где можно стоять, — сквозь камень не выталкиваем.
        /// </summary>
        /// <returns>Была ли особь в личном пространстве.</returns>
        private bool KeepOut(ref SpiderState spider, float3 lifted, float reach)
        {
            if (TargetValid == 0) return false;

            var axis = new float3(Target.x, math.clamp(lifted.y, TargetLow, TargetHigh), Target.z);

            var fromAxis = lifted - axis;
            var fromEye = lifted - Target;

            var axisDistance = math.length(fromAxis);
            var eyeDistance = math.length(fromEye);

            var axisIntrusion = reach * 0.85f - axisDistance;
            var eyeIntrusion = EyeRadius - eyeDistance;
            var intrusion = math.max(axisIntrusion, eyeIntrusion);

            if (intrusion <= 0f) return false;

            var away = eyeIntrusion > axisIntrusion ? fromEye : fromAxis;

            // Ровно на оси направления нет — берём любое вбок по поверхности.
            if (math.lengthsq(away) < 1e-6f) away = math.cross(spider.Up, new float3(0.3f, 0.1f, 1f));

            var along = away - spider.Up * math.dot(away, spider.Up);

            if (math.lengthsq(along) < 1e-6f) along = math.cross(spider.Up, math.normalizesafe(away + 0.3f));

            var step = math.normalizesafe(along) * math.min(intrusion, ShoveSpeed * DeltaTime);

            if (Field.CanStand(spider.Position + step)) spider.Position += step;

            return true;
        }

        /// <summary>
        /// Насколько высоко куча может подняться здесь: под самыми ногами игрока — нисколько,
        /// дальше — до <see cref="MaxClimb"/>.
        ///
        /// Была воронка: у игрока куча ограничивалась 1.2 юнита и росла к пяти юнитам до полной.
        /// Её заводили, когда ярусов было сколько угодно и куча у ног росла до уровня глаз.
        /// Ярусов тогда было два, а 1.2 оказалось ниже спины у шести видов из десяти: на бьющего
        /// тарантула за ним лезть было некуда, лезущий упирался в потолок воронки и висел
        /// перед спиной в воздухе. Пользователь просил обратного — чтобы задние лезли
        /// на бьющих: «было бы ещё страшнее».
        /// </summary>
        private float CraterCap(float3 lifted)
        {
            if (TargetValid == 0) return MaxClimb;

            return math.distance(lifted.xz, Target.xz) < BareRadius ? 0f : MaxClimb;
        }

        /// <summary>
        /// Поведение особи «на камне». Возвращает, на какую высоту над камнем ей хочется:
        /// на горб спины соседа, на которого она лезет или на котором стоит.
        /// </summary>
        /// <param name="progress">Сколько особь продвинулась вперёд, юниты в секунду: подъём по горбу
        /// идёт на ходу.</param>
        /// <param name="incline">Крутизна горба под ней — см. <see cref="SpiderTuning.Hump"/>.</param>
        private float Behave(int index, ref SpiderState spider, SpiderTuning tuning, float3 lifted, float radius,
            float hover, float reach, float stride, out float progress, out float incline)
        {
            progress = 0f;
            incline = 0f;

            var height = tuning.Height * spider.Scale;
            var ceiling = CraterCap(lifted);

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

                return 0f;
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

                return 0f;
            }

            if (UpdateLurk(ref spider, tuning, distance)) return 0f;

            if (spider.Clip == (int)SpiderClip.Attack)
            {
                // Бьющий только держится на тех, кто под ним, и никуда не лезет: он стоит,
                // а подъём идёт лишь на ходу. Раньше половина бьющих поднималась по игроку,
                // стоя на месте, и это читалось ровно как жалоба: «доходят и плавно
                // взлетают вверх».
                Flock(index, lifted, spider.Position, spider.Up, math.normalizesafe(toTarget), spider.Climb,
                    spider.Tier, radius, height, false, ceiling, out var held, out _, out var heldTier, out var attackShove, out _);

                if (held > 0f) spider.Tier = heldTier;

                // Тела бьющих раздвигаются, как у всех. Раньше расталкивание у бьющих
                // считалось и выбрасывалось: кольцо у игрока стояло друг в друге.
                //
                // Но бьющий стоит крепче: свою долю он сдвигает вчетверо слабее, а уступают
                // ему подходящие. Иначе кольцо у игрока толкали по кругу, и бьющий, всё время
                // доворачивая на игрока вплотную, вертелся: 313 градусов в секунду.
                Shove(ref spider, attackShove * 0.25f);

                // Отодвинули дальше дистанции удара — удар прерывается, особь снова идёт
                // к игроку. Иначе бьющая замахивалась бы с полутора юнитов «в воздух».
                if (math.length(aim - spider.Position) > reach * 1.25f)
                {
                    spider.Clip = (int)SpiderClip.Walk;
                    spider.Phase = 0f;

                    return held;
                }

                UpdateAttack(ref spider, tuning, toTarget, distance, normal, depth, hover, onField);

                return held;
            }

            if (TargetValid != 0 && distance <= reach)
            {
                spider.Clip = (int)SpiderClip.Attack;
                spider.Phase = 0f;

                return spider.Climb;
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

                desired += Flock(index, lifted, spider.Position, spider.Up, math.normalizesafe(toTarget),
                    spider.Climb, spider.Tier, radius, height, false, ceiling, out var waiting, out _, out var waitingTier,
                    out var waitingShove, out _);

                if (waiting > 0f) spider.Tier = waitingTier;

                Shove(ref spider, waitingShove);

                desired = math.normalizesafe(desired - spider.Up * math.dot(desired, spider.Up));

                spider.Velocity = math.lerp(spider.Velocity, desired * (tuning.MoveSpeed * 0.25f),
                    math.saturate(DeltaTime * Acceleration));

                Advance(ref spider, tuning, hover, stride, onField, 0f);

                return waiting;
            }

            // У самого игрока — прямо на него по своей поверхности, а не по полю потока.
            // Поле стекает к клетке игрока, а она на полу: стенные у самого игрока
            // спускались на пол, и замер давал у игрока 85% толпы на полу против 28%
            // вдали. Прямое направление, приведённое к плоскости стены, ведёт вдоль стены
            // к ближайшей к игроку точке — и стенной остаётся на стене.
            var nearPlayer = TargetValid != 0 && distance < WallRange;

            if (onField && !nearPlayer) desired += flow;
            if (nearPlayer) desired += math.normalizesafe(toTarget) * 1.5f;

            // Вблизи игрока поле потока вырождается: в клетке-источнике направление ноль,
            // и толпа, дойдя до неё, топталась бы вокруг, а не давила. Если особь уже идёт
            // на игрока прямо (nearPlayer), второй раз тягу не добавляем: двойная тяга
            // продавливала соседей насквозь, и тела у игрока сидели друг в друге.
            if (!nearPlayer && TargetValid != 0 && distance < CloseRange) desired += math.normalizesafe(toTarget) * 1.5f;

            // Если особь оказалась вне размеченных клеток (уровень перестроили, взрыв
            // вынес стену), она всё равно должна двигаться к игроку, а не замирать.
            if (!onField && TargetValid != 0) desired += math.normalizesafe(toTarget);

            // Стенолазы — своя доля толпы (по отдельному жребию от Rank, чтобы не совпадать
            // со стороной обхода): тянутся на стены и вверх по ним, к своду, и налезают
            // друг на друга уже там. На стене «вверх» — это мировой верх, приведённый
            // к её плоскости; на полу его нет, и стенолаз берёт вбок, к ближайшей стене.
            // На своде тяга гаснет сама — выше некуда, остаётся ползти по нему к игроку.
            if (math.frac(spider.Rank * 7.31f) < ClimberShare)
            {
                var worldUp = new float3(0f, 1f, 0f);
                var upWall = worldUp - spider.Up * math.dot(worldUp, spider.Up);

                if (math.lengthsq(upWall) > 0.04f)
                {
                    desired += math.normalize(upWall) * ClimberPull;
                }
                else if (spider.Up.y > 0.5f)
                {
                    var toward = math.normalizesafe(desired - spider.Up * math.dot(desired, spider.Up));
                    var side = spider.Rank < 0.5f ? 1f : -1f;

                    desired += math.cross(spider.Up, toward) * (side * ClimberPull);
                }
            }

            // Куда особь рвётся — ДО стаи: по этому направлению решается, кто у неё
            // «впереди», то есть на чью спину лезть.
            var heading = math.normalizesafe(desired - spider.Up * math.dot(desired, spider.Up));

            var drive = tuning.MoveSpeed * spider.SpeedScale;

            desired += Flock(index, lifted, spider.Position, spider.Up, heading, spider.Climb, spider.Tier, radius, height,
                TargetValid != 0 && distance < PileRadius, ceiling, out var support, out incline, out var supportTier,
                out var shove, out var contact);

            if (support > 0f) spider.Tier = supportTier;

            Shove(ref spider, shove);

            // Упёрся — обходит. Вбок по поверхности, каждая особь в свою сторону (по Rank,
            // чтобы не метаться): на полу коридора это ведёт на стену, со стены — на свод.
            // Так затор у игрока расползается по стенам, а не только громоздится вверх.
            var stuck = math.saturate(1f - math.dot(spider.Velocity, heading) / math.max(0.1f, drive * 0.5f));

            if (TargetValid != 0 && distance < PileRadius && stuck > 0f)
            {
                var side = spider.Rank < 0.5f ? 1f : -1f;
                desired += math.cross(spider.Up, heading) * (side * stuck * DetourWeight);
            }

            // Всё движение идёт В ПЛОСКОСТИ ПОВЕРХНОСТИ: составляющая вдоль нормали
            // означала бы отрыв от камня или вход в него, а прижимом занимается
            // отдельный шаг, по замеренной глубине.
            var up = spider.Up;
            desired = math.normalizesafe(desired - up * math.dot(desired, up));

            // По чужой спине — тем же шагом, что по камню, только часть его уходит вверх:
            // по горизонтали выходит медленнее. Иначе мелкий паук, взбегающий на тарантула,
            // взлетал бы на его спину за пару кадров.
            var wanted = desired * (tuning.MoveSpeed * spider.SpeedScale * math.rsqrt(1f + incline * incline));

            // Сначала срезаем то, куда особь ХОЧЕТ, — тогда скорость подтягивается к уже
            // допустимой плавно, без рывков; потом то, что осталось от разгона прошлых кадров.
            wanted = Block(wanted, contact, spider.Up);

            spider.Velocity = math.lerp(spider.Velocity, wanted, math.saturate(DeltaTime * Acceleration));
            spider.Velocity = Block(spider.Velocity, contact, spider.Up);

            // Смотрит на цель (поле потока, игрок, тяга стенолаза), а не по сумме со стаей:
            // расталкивание в плотной давке меняет направление каждый кадр, и толпа,
            // повёрнутая по нему, вертелась даже у бьющих.
            Advance(ref spider, tuning, hover, stride, onField, math.length(wanted), heading);

            progress = math.max(0f, math.dot(spider.Velocity, heading));

            return support;
        }

        /// <summary>
        /// Раздвигает тела: сдвиг по поверхности, только туда, где можно стоять.
        /// Позицию, а не скорость: скорость, сложенная с тягой к игроку, продавливала
        /// соседа насквозь — у игрока 92% особей сидели телом в другой.
        /// </summary>
        private void Shove(ref SpiderState spider, float3 shove)
        {
            var along = shove - spider.Up * math.dot(shove, spider.Up);

            if (math.lengthsq(along) < 1e-8f) return;

            if (Field.CanStand(spider.Position + along)) spider.Position += along;
        }

        /// <summary>
        /// Срезает у скорости составляющую, идущую в тела соседей впереди: упёршийся скользит
        /// вдоль, а не давит насквозь.
        ///
        /// Без этого раздвигать тела было бесполезно. Расталкивание складывается с тягой
        /// к игроку и нормируется, так что в давке особь шла вперёд на полной скорости,
        /// и каждый шаг вдавливал её обратно в того, от кого её только что отодвинули.
        /// Остановленная особь дальше сама решает, что делать: обходит вбок, всходит
        /// по горбу на спину или скребёт лапами в очереди.
        /// </summary>
        private static float3 Block(float3 velocity, float3 contact, float3 up)
        {
            var into = contact - up * math.dot(contact, up);
            var length = math.length(into);

            if (length < 1e-4f) return velocity;

            into /= length;

            var push = math.dot(velocity, into);

            return push > 0f ? velocity - into * push : velocity;
        }

        /// <summary>
        /// Ведёт высоту к желаемой. Желаемая — горб чужой спины под особью, поэтому растёт она
        /// только на ходу к соседу: всходит особь по нему как по склону, со скоростью, которую
        /// требует его крутизна. Вниз — сходит по горбу или падает с ускорением, если опоры не стало.
        ///
        /// Первая версия поднимала с постоянной скоростью, сколько бы особь ни шла, — и та,
        /// что стояла, упёршись или ударяя, ровно всплывала вверх, как на лифте. Пользователь
        /// так это и описал: «доходят до игрока и плавно взлетают вверх». Вторая поднимала
        /// упёршегося на дыбах, стоя перед спиной соседа на всю её высоту, — и в кадре это
        /// был паук, висящий в воздухе перед бьющим. Стоя теперь поднимается только шаг
        /// передних лап на край горба; остальное — на ходу.
        ///
        /// Особь, забравшаяся на соседа у свода, упёрлась бы в породу напротив, поэтому
        /// желаемая высота срезается по глубине той поверхности, что смотрит навстречу.
        /// </summary>
        private void UpdateClimb(ref SpiderState spider, float target, float radius, float progress, float incline)
        {
            target = math.clamp(target, 0f, MaxClimb);

            if (target > 0f)
            {
                Field.SampleAt(spider.Position + spider.Up * target, out _, out var facing, out var gap, out var valid);

                if (valid && math.dot(facing, spider.Up) < -0.3f)
                {
                    target = math.max(0f, target + gap - radius * 0.6f);
                    target = math.min(target, MaxClimb);
                }
            }

            var before = spider.Climb;

            if (target > before + 0.01f)
            {
                // Горб растёт под особью на ходу ровно на крутизну за юнит продвижения; сверх
                // этого — шаг передних лап на край горба, когда упёрлась в соседа.
                //
                // На плато (крутизны нет) горб растёт не от её хода, а потому что поднимается
                // тот, на ком она лежит, — и она поднимается вместе с ним, сразу. Со скоростью
                // лазания верхний ярус отставал от нижнего и оказывался у него внутри.
                if (incline > 0f)
                {
                    // Сильно отставшая от горба — карабкается быстрее: иначе, пока она всходит,
                    // её тело сидит в спине того, на кого она лезет.
                    var rise = math.max(ClimbRate + progress * incline, (target - before) * CatchUp);

                    spider.Climb = math.min(target, before + rise * DeltaTime);
                }
                else
                {
                    // Плавно, а не шагом постоянной скорости: нижний подрагивает, и верхний,
                    // повторявший каждое его движение за кадр, передавал дрожь дальше вверх по куче.
                    spider.Climb = math.lerp(before, target, math.saturate(DeltaTime * CarryResponse));
                }

                spider.ClimbVel = (spider.Climb - before) / DeltaTime;
                return;
            }

            // Падать — только если опора ушла заметно, а не на волос: иначе лежащий
            // на спине соседа подрагивал вместе с каждым его шагом. Но, начав спускаться,
            // особь идёт за горбом вниз, пока он спадает: сходящий со спины соседа иначе
            // спрыгивал бы ступеньками по 0.12.
            if (target < before - 0.12f || (target < before - 0.01f && spider.ClimbVel < 0f))
            {
                var fall = math.min(spider.ClimbVel, 0f) - Gravity * DeltaTime;

                spider.Climb = math.max(target, before + fall * DeltaTime);
                spider.ClimbVel = spider.Climb > target ? fall : (spider.Climb - before) / DeltaTime;
                return;
            }

            spider.ClimbVel = 0f;
        }

        /// <summary>Падение без опоры — для тех, чьё поведение не ведёт высоту: трупов и уходящих.</summary>
        private void Drop(ref SpiderState spider)
        {
            if (spider.Climb <= 0f) return;

            spider.ClimbVel = math.min(spider.ClimbVel, 0f) - Gravity * DeltaTime;

            var fall = math.min(spider.Climb, -spider.ClimbVel * DeltaTime);

            spider.Position -= spider.Up * fall;
            spider.Climb -= fall;

            if (spider.Climb <= 0f) spider.ClimbVel = 0f;
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
            //
            // Отлив — не паника: волна спала, и лишние уходят обычным бегом. Рывок
            // врассыпную читался бы как испуг, а орда не пугается.
            var rush = spider.Ebb != 0 ? 1.1f : 2.2f;

            spider.Velocity = math.lerp(spider.Velocity, away * (tuning.MoveSpeed * spider.SpeedScale * rush),
                math.saturate(DeltaTime * Acceleration));

            spider.Clip = (int)SpiderClip.Walk;

            Advance(ref spider, tuning, hover, stride, onField, 0f);
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

            if (spider.Timer >= (spider.Gibbed != 0 ? SpiderHash.GibSeconds : tuning.CorpseLinger)) spider.Active = 0;
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

            // Терпение засады. Таймер у живой особи свободен (он про трупы), им и меряем.
            // Без терпения засадник, мимо которого игрок не прошёл, висел до конца стадии
            // и занимал место в населении: прогон к 6:47 насчитал 514 засадников из 541
            // живых, и досыла не было вовсе — директор считал цель выполненной.
            // Порог разный у разных особей (по Rank), иначе засада срывалась бы залпом.
            spider.Timer += DeltaTime;

            var patient = spider.Timer < LurkPatience * (0.6f + 0.8f * spider.Rank);

            if (TargetValid == 0 || (distance > tuning.LurkTrigger && patient)) return true;

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

        /// <summary>
        /// Расталкивание, выравнивание и сбивание в кучу — три классических правила стаи —
        /// плюс опора: на какой высоте особи стоять, если она лезет на соседа или стоит на нём.
        ///
        /// Толкаются только особи одного яруса. Раньше слой был один, и задние упирались
        /// в спины передних и стояли: замер давал 40% толпы у игрока, не делающей ничего.
        /// Теперь упёршаяся лезет соседу на спину, и сосед её больше не держит — она у него
        /// на горбу (<see cref="SpiderTuning.Hump"/>). Сползти с кучи она может: горб спадает
        /// к кончикам лап, а за ними опоры нет.
        /// </summary>
        /// <param name="lifted">Позиция с подъёмом — ей особь соприкасается с соседями.</param>
        /// <param name="ground">Позиция на камне — от неё меряется высота соседей.</param>
        /// <param name="heading">Куда особь рвётся, в плоскости поверхности.</param>
        /// <param name="climb">Её подъём над камнем.</param>
        /// <param name="tier">Её ярус: держится она на тех, кто ярусом ниже, а лезет на тех, кто её яруса.</param>
        /// <param name="height">Высота этой особи в мире, с аркой лап.</param>
        /// <param name="pile">Можно ли лезть на соседа: только у игрока, см. <see cref="PileRadius"/>.</param>
        /// <param name="ceiling">Потолок кучи здесь — см. <see cref="CraterCap"/>.</param>
        /// <param name="support">На какую высоту над камнем ей встать: самый высокий горб под ней.</param>
        /// <param name="incline">Крутизна этого горба в её точке.</param>
        /// <param name="supportTier">На каком ярусе она будет, стоя на этом горбе.</param>
        /// <param name="shove">На сколько раздвинуть тело прямо сейчас, в плоскости поверхности.</param>
        /// <param name="contact">Куда особи нельзя: сумма направлений на соседей впереди, которых она касается.</param>
        private float3 Flock(int index, float3 lifted, float3 ground, float3 up, float3 heading, float climb,
            int tier, float radius, float height, bool pile, float ceiling, out float support, out float incline,
            out int supportTier, out float3 shove, out float3 contact)
        {
            var separation = float3.zero;
            var alignment = float3.zero;
            var cohesion = float3.zero;

            support = 0f;
            incline = 0f;
            supportTier = 0;
            shove = float3.zero;
            contact = float3.zero;

            var core = radius * SpiderTuning.CoreShare;

            // Начать лезть можно, только если место наверху свободно: на спине того, на кого
            // лезем, никто не лежит. Решается после разбора всех соседей, поэтому горб того,
            // на кого только собираемся лезть, и его твёрдое тело откладываются в сторону,
            // а наездники вокруг запоминаются.
            //
            // Без этого второй ярус набивался так же плотно, как первый: у каждого наездника
            // вплотную сидело по пять других, раздвигание между ними работало без передышки,
            // и тряслись 34-43% наездников. Со свободным местом наверху на спине — по одному.
            //
            // Занято — именно его спина, а не «кто-то наверху впереди»: так у игрока
            // упёршиеся не лезли на свободного бьющего, если наездник сидел на соседнем.
            var startSupport = 0f;
            var startIncline = 0f;
            var startTier = 0;
            var startBase = float4.zero;

            // Выше этого над нашим камнем лежит тот, кто уже на спине, на которую лезем.
            var startTop = 0f;

            var riders = new FixedList512Bytes<float4>();
            var riderHeights = new FixedList128Bytes<float>();

            // На нас кто-то лежит — сами не лезем. Иначе дно стопки уезжало вверх из-под верхних,
            // те не поспевали за ним или упирались в потолок кучи и оказывались внутри нижнего:
            // так сидела большая часть «глубоких» пар, когда ярусов стало больше двух.
            var loaded = false;
            var pendingContact = float3.zero;
            var pendingShove = float3.zero;
            var pendingSeparation = float3.zero;

            // На ком стоим: смещение от его центра к нам и докуда тянется плато его спины.
            var mount = float4.zero;

            var neighbours = 0;
            var examined = 0;
            var scanned = 0;

            var radiusSq = NeighbourRadius * NeighbourRadius;
            var cell = SpiderHash.Cell(lifted, HashCellSize);

            // Разбор соседей: своя клетка первой, дальние отсеиваются одной проверкой
            // расстояния и в лимит не идут.
            //
            // Раньше лимит считал КАЖДОГО кандидата, и клетки шли по порядку с угла. В давке
            // у игрока в соседних клетках сотни особей, и тридцать два разобранных оказывались
            // случайными дальними, а тот, в ком особь сидела телом, до разбора не доходил.
            // Замер: у игрока 92% особей сидели телом в другой, и из них 355 из 503 — оба
            // на камне, то есть расталкивание их просто не видело.
            var scanLimit = MaxNeighbours * 8;

            for (var n = 0; n < 27; n++)
            {
                var o = (n + 13) % 27;
                var key = SpiderHash.Key(cell + new int3(o % 3 - 1, o / 3 % 3 - 1, o / 9 - 1));

                if (!Hash.TryGetFirstValue(key, out var other, out var iterator)) continue;

                do
                {
                    if (other == index) continue;
                    if (++scanned > scanLimit) goto done;

                    var data = Neighbours[other];

                    var delta = lifted - data.xyz;

                    var distanceSq = math.lengthsq(delta);
                    if (distanceSq < 1e-6f) continue;

                    var touch = data.w + radius;

                    // Дальний — ни толкать, ни выравниваться: дешёвый отсев до всего остального.
                    if (distanceSq >= math.max(radiusSq, touch * touch)) continue;

                    if (++examined > MaxNeighbours && distanceSq >= touch * touch) continue;

                    // Ярус соседа: насколько он выше или ниже вдоль нашей нормали.
                    var vertical = math.dot(delta, up);
                    var planar = delta - up * vertical;
                    var planarSq = math.lengthsq(planar);
                    var planarLength = math.sqrt(planarSq);

                    // Спина соседа и наша: на этой высоте один ложится на другого. По настоящей
                    // высоте особи, а не по доле суммы радиусов: доля ставила верхнего на середину
                    // высоты нижнего, то есть внутрь него, — жалоба «залезают друг в друга,
                    // а не друг на друга».
                    var theirBack = Heights[other] * SpiderTuning.BackShare;
                    var ourBack = height * SpiderTuning.BackShare;

                    // Один ярус — пока ни один не лежит на спине другого.
                    var sameLayer = vertical >= 0f
                        ? math.saturate(1f - vertical / math.max(0.05f, theirBack))
                        : math.saturate(1f + vertical / math.max(0.05f, ourBack));

                    // Опора — горб спины соседа. Ярусов сколько угодно: лезть можно и на того, кто
                    // сам лежит на чужой спине, — пользователь просил, чтобы куча росла в три ряда
                    // и выше и заполняла ход целиком.
                    //
                    // Лезть — на того, кто стоит вровень с нами, впереди по ходу и не движется: бьёт,
                    // упёрся, лежит на чужой спине. Начавшая подниматься продолжает, пока он не побежал, —
                    // запас нужен, чтобы опора не мигала, пока нижний переступает.
                    //
                    // Держаться — только на том, чей ярус ниже нашего (SpiderState.Tier). Строгий порядок
                    // по ярусу не даёт куче поднять саму себя: двое не могут держать друг друга, и цепочка
                    // опор всегда кончается на камне. Прежние версии держали на любом, кто ниже «хоть
                    // на волос», и к концу стадии 338 из 384 висели у потолка высоты (грабля №43).
                    // Порядок по высоте с запасом тоже пробовался и мигал: верхний, отстав на кадр
                    // от поднявшегося нижнего, терял запас и падал, а нижний садился уже на него, —
                    // в любой момент падала и поднималась треть второго яруса.
                    // От скорости нижнего держание не зависит: порог по скорости мигал, и вместе
                    // с ним включалось и выключалось твёрдое тело — тряслись 43% наездников.
                    //
                    // Лезть на любого, кто не уходит вперёд, пробовалось — и повторило граблю
                    // «второго этажа»: наверх ушли 66% толпы у игрока, тела наверху сидели друг
                    // в друге. Ярус держит исключением свободное место на спине, см. выше.
                    var above = math.dot(data.xyz - ground, up);
                    var theirClimb = Climbs[other];

                    // Насколько его лапы выше наших. Минус — он ниже, и на нём можно стоять.
                    var rise = above - climb;
                    var theirTier = Tiers[other];

                    // Он на нашей поверхности, а не на стене над нами: его камень — вровень с нашим.
                    var sameGround = math.abs(above - theirClimb) < SpiderTuning.LevelTolerance;

                    // Мы у соседа на горбу: он не держит нас телом и не расталкивает.
                    var onHump = false;

                    // Только собираемся на него лезть: его тело держит нас, если место наверху занято.
                    var starting = false;

                    // Сосед у нас на горбу: не расталкиваем его — бьющий отталкивался бы от того,
                    // кто на него лезет. Телом он держит по-прежнему, если впереди: под наездника,
                    // стоящего на чужих лапах, не подлезают. Только тот, кто ярусом выше: двое
                    // одного яруса друг другу не опора, и без этого условия проходили друг в друга —
                    // глубоко сидели 38%.
                    var underRider = false;

                    if (sameGround && theirTier > tier && rise > 0.05f)
                    {
                        // Лежать на нас он может, только если мы не мельче его — см. SturdyShare.
                        // Иначе мелкий, считая крупного своим наездником, не расталкивал его
                        // и подлезал под него — крупный садился в мелкого телом.
                        underRider = radius >= data.w * SpiderTuning.SturdyShare &&
                                     SpiderTuning.Hump(planarLength, radius, height, data.w, out _) > 0f;
                        loaded |= underRider;

                        // Лежит наверху: если на спине того, на кого собираемся лезть, — занято.
                        if (riders.Length < riders.Capacity && riderHeights.Length < riderHeights.Capacity)
                        {
                            riders.Add(new float4(-planar, data.w * SpiderTuning.CoreShare));
                            riderHeights.Add(above);
                        }
                    }

                    // Под потолок кучи его спина должна помещаться целиком: иначе лезущий упирался бы
                    // в потолок посреди горба и висел перед спиной в воздухе. По его собственному
                    // подъёму, без поправки на бугор пола под нами: с ней спина крупного тарантула
                    // то влезала, то нет, стоило ему переступить. И сам он не мельче нас —
                    // см. SpiderTuning.SturdyShare. И он не на верхнем ярусе — см. PileTiers.
                    var fits = theirClimb + theirBack <= ceiling && data.w >= radius * SpiderTuning.SturdyShare &&
                               theirTier < PileTiers - 1;

                    if (sameGround && fits)
                    {
                        var hump = SpiderTuning.Hump(planarLength, data.w, Heights[other], radius, out var slope);

                        var speedSq = math.lengthsq(Velocities[other]);
                        var ahead = pile && math.dot(-planar, heading) > 0.3f * planarLength;

                        // Уже поднялись над его лапами — держимся на нём; ещё у его лап — всходим.
                        var begun = -rise > 0.05f;

                        // Держимся на том, чей ярус ниже; на ярусе ниже, но ещё у его лап — продолжаем
                        // всходить, пока он не побежал.
                        var lower = theirTier < tier;
                        var holding = lower && begun;
                        var continuing = lower && !begun && ahead && speedSq < 2.25f;

                        // Лезем на того, кто нашего яруса, вровень с нами, впереди и стоит. Вровень: у стоящего
                        // на камне — на нашем камне, у лежащего на спине — в пределах половины его спины
                        // от наших лап, иначе стоящий на полу шагнул бы на наездника через ярус.
                        var level = math.abs(rise) < (theirClimb < 0.05f
                            ? SpiderTuning.LevelTolerance
                            : math.min(SpiderTuning.LevelTolerance, theirBack * 0.5f));

                        var climbing = theirTier == tier && level && ahead && speedSq < (begun ? 2.25f : 0.25f);

                        if (hump > 0f && (holding || continuing || climbing))
                        {
                            if (climbing && !begun)
                            {
                                starting = true;

                                if (above + hump > startSupport)
                                {
                                    startSupport = above + hump;
                                    startIncline = slope;
                                    startTier = theirTier + 1;
                                    startBase = new float4(-planar, data.w * SpiderTuning.CoreShare);
                                    startTop = above + theirBack * 0.5f;
                                }
                            }
                            else
                            {
                                onHump = true;

                                if (above + hump > support)
                                {
                                    support = above + hump;
                                    incline = slope;
                                    supportTier = theirTier + 1;
                                    mount = new float4(planar, SpiderTuning.HumpTop(data.w, radius));
                                }
                            }
                        }
                    }

                    // Тела твёрдые: в одном ярусе ядро тела соседа не пускает внутрь.
                    //
                    // Держит прежде всего КОНТАКТ, а не раздвигание: сосед впереди в пределах
                    // касания с запасом срезает желаемую скорость в себя каждый кадр, пока
                    // касание длится. Первая версия держала одним раздвиганием позиций на
                    // половину перекрытия — у всех разом, по позициям прошлого кадра, — а скорость
                    // срезала только в кадре перекрытия. Следующий шаг вдавливал особь обратно,
                    // и толпа дрожала с частотой кадра, вся синхронно: позиция скакала туда-обратно
                    // на 5-25 см за кадр, тряслись 68-99% особей, и дальние, и ближние.
                    var coreSum = core + data.w * SpiderTuning.CoreShare;
                    var contactReach = coreSum * ContactMargin;

                    if (!onHump && sameLayer > 0.05f && planarSq < contactReach * contactReach && planarSq > 1e-8f)
                    {
                        var fromThem = planar / planarLength;

                        // Держит только тот, кто впереди по ходу: сосед сзади вперёд не мешает.
                        var holds = -fromThem * math.saturate(math.dot(-fromThem, heading));

                        if (starting) pendingContact += holds;
                        else contact += holds;

                        // Настоящее перекрытие раздвигаем мягко, долей за кадр: доля в половину
                        // с обеих сторон перелетала и раскачивала. И только в меру общего яруса:
                        // пара на разной высоте, которую подъём по горбу сводит в один ярус,
                        // иначе получала полный толчок разом. В квадрате меры пробовалось ради дрожи
                        // в куче в три яруса — дрожь почти не сдвинулась, а тела пошли друг в друга.
                        if (planarLength < coreSum && !underRider)
                        {
                            // Глубокое перекрытие в плане раздвигается в полную силу, как бы мало
                            // ни был общий ярус: пара одного яруса на разной высоте (у каждого своя
                            // опора) иначе так и оставалась одна в другой.
                            var deep = math.saturate(2f - 2f * planarLength / coreSum);
                            var push = fromThem * ((coreSum - planarLength) * ShoveShare * math.max(sameLayer, deep));

                            if (starting) pendingShove += push;
                            else shove += push;
                        }
                    }

                    // Лапы: мягкое расталкивание, в полную силу на одном ярусе и никакое, когда
                    // один лежит на другом. Переход плавный, а не порогом: порог включал силу
                    // скачком, как только особь поднималась или падала мимо него, и толпа
                    // в пути дёргалась — угловая скорость выросла со 161-183 до 185-209.
                    if (!onHump && !underRider && sameLayer > 0f && distanceSq < touch * touch)
                    {
                        var d = math.sqrt(distanceSq);

                        // Сила растёт линейно к нулю расстояния и обнуляется на касании:
                        // обратный квадрат на плотной толпе выстреливает особей из давки.
                        var apart = delta / d * ((1f - d / touch) * sameLayer);

                        if (starting) pendingSeparation += apart;
                        else separation += apart;
                    }

                    if (distanceSq >= radiusSq || examined > MaxNeighbours) continue;

                    alignment += Velocities[other];
                    cohesion += data.xyz;
                    neighbours++;
                }
                while (Hash.TryGetNextValue(out other, ref iterator));
            }

            done:

            var occupied = false;

            if (startSupport > 0f)
            {
                // Куда встанем: на ближнюю к нам половину его спины. Наездник на дальнем краю
                // спины крупного соседа места не занимает — вторым туда встать можно.
                var landing = startBase.xyz - math.normalizesafe(startBase.xyz) * (startBase.w * 0.5f);

                for (var r = 0; r < riders.Length; r++)
                {
                    if (riderHeights[r] > startTop && math.distance(riders[r].xyz, landing) < riders[r].w + core)
                    {
                        occupied = true;
                    }
                }
            }

            if (startSupport > 0f && !occupied && !loaded)
            {
                if (startSupport > support)
                {
                    support = startSupport;
                    incline = startIncline;
                    supportTier = startTier;
                    mount = float4.zero;
                }
            }
            else
            {
                contact += pendingContact;
                shove += pendingShove;
                separation += pendingSeparation;
            }

            // Край чужой спины у игрока — упор. Наездник, идущий к игроку, переходил через спину
            // бьющего и сходил спереди, на пол, — и на спинах бьющих не лежал почти никто: замер
            // давал двоих на десяток бьющих. Теперь он остаётся на переднем краю спины, откуда
            // и бьёт, а вдоль края может сползти вбок. Вдали от игрока со спины сходят свободно:
            // куча нужна у игрока, а не в заторах посреди уровня.
            var mountLength = math.length(mount.xyz);

            if (pile && climb > SpiderTuning.RideLevel && mountLength > mount.w && mountLength > 1e-4f)
            {
                var outward = mount.xyz / mountLength;

                contact += outward * math.saturate(math.dot(outward, heading));
            }

            // Больше собственного ядра за кадр не двигаем: в давке сдвиги от десятка соседей
            // складываются, и без предела особь выстреливало бы из толпы.
            var shoveLength = math.length(shove);
            if (shoveLength > core) shove *= core / shoveLength;

            var result = separation * SeparationWeight;

            if (neighbours == 0) return result;

            result += math.normalizesafe(alignment / neighbours) * AlignmentWeight;
            result += math.normalizesafe(cohesion / neighbours - lifted) * CohesionWeight;

            return result;
        }

        /// <summary>Шаг по поверхности, прижим к ней, поворот и фаза анимации от пройденного пути.</summary>
        /// <param name="effort">С какой скоростью особь рвётся вперёд. Ноль — не рвётся.</param>
        /// <param name="intent">Куда особь рвётся. Упёршаяся смотрит туда, а не по скорости.</param>
        private void Advance(ref SpiderState spider, SpiderTuning tuning, float hover, float stride, bool onField,
            float effort, float3 intent = default)
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

            // Смотрит туда, куда рвётся, а не куда её сдвинуло. Скорость — это остаток после
            // того, как срезано всё, что шло в тела соседей, и в давке его направление скачет
            // каждый кадр: толпа, повёрнутая по нему, вертелась волчками — 350 градусов
            // в секунду против прежних 110-170, и это у всех, даже у свободно бегущих вдали.
            // Живого паука толчок в бок не разворачивает.
            if (effort > 0f && math.lengthsq(intent) > 1e-4f) heading = intent;

            spider.Rotation = Turn(spider.Rotation, heading, spider.Up, tuning.FacingSign,
                math.radians(tuning.TurnSpeed) * DeltaTime);

            // Фаза ведётся пройденным путём, а не временем: иначе лапы скользят по камню,
            // и чем сильнее особь тормозит в давке, тем заметнее. Нижняя граница нужна
            // стоящим — замершая насмерть модель читается как сломанная анимация,
            // и она же заменяет выброшенный клип стояния.
            var pace = math.max(speed, IdleStride);

            // Упёрся — скребёт лапами. Особь, которая рвётся вперёд, но стоит в давке,
            // перебирала лапами еле-еле, на скорости стоящей, и в кадре читалась как
            // скучающая: жалоба была ровно на это. Скребущая на полной каденции
            // читается как та, что лезет.
            if (effort > 0f && speed < effort * 0.5f) pace = math.max(pace, effort * 0.8f);

            spider.Phase = math.frac(spider.Phase + pace * DeltaTime / stride);
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
        [WriteOnly] public NativeArray<float> Heights;
        [WriteOnly] public NativeArray<float> Climbs;
        [WriteOnly] public NativeArray<int> Tiers;

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
                Heights[index] = 0f;
                Climbs[index] = 0f;
                Tiers[index] = 0;
                return;
            }

            Neighbours[index] = new float4(spider.Position,
                Tuning[spider.Kind].BodyRadius * spider.Scale);

            Velocities[index] = spider.Velocity;
            Heights[index] = Tuning[spider.Kind].Height * spider.Scale;
            Climbs[index] = spider.Climb;
            Tiers[index] = spider.Tier;
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
                var gibAge = 0f;
                var seed = row.z;

                if (spider.Clip == (int)SpiderClip.Dead && spider.Gibbed != 0)
                {
                    // Разорванный: куски разлетаются и съёживаются в шейдере, каждый к своему
                    // центру. Усадка целиком здесь стянула бы разлетевшиеся куски обратно в точку.
                    gibAge = math.max(spider.Timer, 1e-3f);
                    seed = SpiderHash.PackGib(spider.GibDrop, spider.Serial);

                    tint *= 1f - math.saturate(spider.Timer / SpiderHash.GibSeconds) * 0.5f;
                }
                else if (spider.Clip == (int)SpiderClip.Dead)
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
                else if (spider.Ebb == 2)
                {
                    // Уходит в щель — тем же приёмом, каким гаснет труп: усадка
                    // с потемнением, без прозрачности. В темноте на двадцати юнитах
                    // это читается как паук, забившийся в трещину.
                    var sink = math.saturate(spider.Timer / SpiderHash.SinkSeconds);

                    scale *= 1f - sink * 0.85f;
                    tint *= 1f - sink * 0.8f;
                }

                // Наклон по склону чужой спины. Ось — поперечная модели; знак от того,
                // куда смотрит модель: у пака Spiders голова в минус Z.
                var rotation = spider.Clip == (int)SpiderClip.Dead
                    ? spider.Rotation
                    : math.mul(spider.Rotation, quaternion.RotateX(-spider.Pitch * tuning.FacingSign));

                Matrices[written] = math.mul(LocalToWorld,
                    float4x4.TRS(spider.Position, rotation, scale));

                AnimState[written] = new float4(row.x, row.y, spider.Phase, seed);
                Tints[written] = new float4(spider.Hue * tint, gibAge);

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

        /// <summary>Урон в HP.</summary>
        public float Damage;

        /// <summary>Сила отброса в эпицентре, юнитов в секунду. У края сферы спадает до нуля.</summary>
        public float Impulse;

        /// <summary>
        /// Ближе этой доли радиуса к эпицентру убитого рвёт на куски; дальше — отбрасывает
        /// целым, кувырком. 0 — рвёт всех убитых, 1 — никого.
        /// </summary>
        public float GibShare;

        /// <summary>Сид для разброса кувырка. Одинаковый кувырок у всех читается как ошибка.</summary>
        public uint Seed;

        /// <summary>
        /// Отметки убитых, по одной ячейке на особь: 1 — убит, 2 — разорван на куски. Массив,
        /// а не счётчик: джоб параллельный, и общий счётчик потребовал бы атомарного сложения
        /// ради числа, которое всё равно нужно раз в выстрел. Сложить отметки дешевле.
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

            // Запас в сотую долю HP: здоровье дробное (60 × 1.5 − 30 − 30 даёт не ноль,
            // а остаток в последнем знаке), и без запаса особь выживала бы с крохой здоровья.
            if (spider.Health > 0.01f)
            {
                States[index] = spider;
                return;
            }

            spider.Clip = (int)SpiderClip.Dead;
            spider.Phase = 0f;
            spider.Timer = 0f;
            spider.Ebb = 0;

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

            // У самого эпицентра — на куски. Кувырок целиком им не нужен: крутятся куски,
            // а отброс слабее, иначе облако кусков уезжало бы от места взрыва.
            var gibbed = falloff >= 1f - GibShare;

            spider.Gibbed = gibbed ? 1 : 0;

            if (gibbed)
            {
                spider.Spin = float3.zero;
                spider.Velocity *= 0.35f;
            }

            Killed[index] = gibbed ? 2 : 1;

            States[index] = spider;
        }
    }
}
