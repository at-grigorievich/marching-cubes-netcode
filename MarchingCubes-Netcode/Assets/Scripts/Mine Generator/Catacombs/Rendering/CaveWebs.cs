using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Паутина в узких местах.
    ///
    /// Отдельный компонент от CaveFixtures намеренно: тот про свет, этот про то, чей
    /// это дом. Общего у них только событие генерации.
    ///
    /// Паутина вешается ровно на щели, и это не украшение: щель — единственное место
    /// уровня, которое игрок обязан пройти вплотную, и именно там нужно сказать «здесь
    /// живут пауки» до того, как они выбегут. Заодно паутина работает ориентиром —
    /// щель с паутиной отличается от щели без неё, и уровень перестаёт быть одинаковым
    /// коричневым коридором.
    /// </summary>
    [ExecuteAlways]
    public sealed class CaveWebs : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;

        /// <summary>
        /// Меши паутины. Берутся готовые из пака PolyOne Cobwebs, а не рисуются в текстуру.
        ///
        /// Заполняет пункт меню «Починить свет»; подменить руками в инспекторе можно, и
        /// тогда сработает любой набор — код не знает про этот пак ничего, кроме того,
        /// что меш лежит в плоскости XY. Это проверено замером: все шестнадцать мешей
        /// центрированы в нуле и плоские по Z (толщина 0.05-0.28 против размаха 0.65-1.55),
        /// то есть ориентируются той же LookRotation, что и прежний квад.
        /// </summary>
        [Tooltip("Префабы паутины. Заполняет пункт меню «Починить свет».")]
        [SerializeField] private GameObject[] webPrefabs;

        [Tooltip("Доля щелей, затянутых паутиной.")]
        [SerializeField, Range(0f, 1f)] private float webShare = 0.7f;

        /// <summary>
        /// Во сколько раз паутина крупнее самой щели.
        ///
        /// Больше единицы намеренно: края должны уходить в породу, а не висеть в воздухе
        /// ровным обрезанным кругом. Но ненамного — у мешей из пака кромка чёткая,
        /// и лишний размер не растворяется по краям, как у прежней нарисованной текстуры,
        /// а просто утапливает паутину в камень целиком. Прежние 4.2-5.4 юнита были
        /// в два с лишним раза шире щели, и половина паутин оказывалась внутри стены.
        /// </summary>
        [Tooltip("Во сколько раз паутина крупнее поперечника щели.")]
        [SerializeField] private Vector2 webOvershoot = new Vector2(1.15f, 1.4f);

        // Приглушённая и грязноватая: на полной яркости паутина читается как свежая
        // краска, а не как то, что висит тут годами.
        [SerializeField] private Color webColor = new Color(0.62f, 0.62f, 0.56f, 0.3f);

        [Header("Вязкость")]
        [Tooltip("Во сколько раз медленнее игрок идёт внутри паутины.")]
        [SerializeField, Range(0.05f, 1f)] private float webSpeedScale = 0.25f;

        [Tooltip("Во сколько раз медленнее игрок падает внутри паутины.")]
        [SerializeField, Range(0.05f, 1f)] private float webFallScale = 0.2f;

        /// <summary>
        /// Толщина вязкого объёма поперёк паутины, юниты.
        ///
        /// Сам меш — почти плоский лист, и объём по его толщине игрок проскакивал бы
        /// за один кадр на любой разумной скорости: при 6 юнитах в секунду и 60 кадрах
        /// шаг равен 0.1 юнита, а лист тоньше. Вязнуть надо примерно полметра пути,
        /// иначе торможения не почувствовать.
        /// </summary>
        [Tooltip("Толщина вязкого объёма поперёк паутины, юниты.")]
        [SerializeField] private float webThickness = 1.1f;

        /// <summary>Меньше этого радиуса паутина в кадре не читается — такие места пропускаем.</summary>
        private const float MinReadableRadius = 0.9f;

        private static MaterialPropertyBlock _tint;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private Transform _container;

        private void OnEnable()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            if (world != null) world.Generated += Rebuild;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;
        }

        public void Rebuild()
        {
            Clear();

            if (world == null || world.Layout == null) return;

            var pinches = world.Layout.Pinches;
            if (pinches.Count == 0) return;

            var container = new GameObject("Cave Webs");
            container.transform.SetParent(transform, false);

            if (!Application.isPlaying) container.hideFlags = HideFlags.DontSave;
            _container = container.transform;

            var random = new System.Random(world.CurrentSeed ^ 0x7EB5);

            foreach (var pinch in pinches)
            {
                if (random.NextDouble() >= webShare) continue;

                SpawnWeb(pinch, random);
            }
        }

        public void Clear()
        {
            if (_container != null)
            {
                DestroyNow(_container.gameObject);
                _container = null;
            }

            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "Cave Webs") DestroyNow(child.gameObject);
            }
        }

        private void SpawnWeb(CatacombLayout.Pinch pinch, System.Random random)
        {
            if (webPrefabs == null || webPrefabs.Length == 0) return;

            var prefab = webPrefabs[random.Next(webPrefabs.Length)];
            if (prefab == null) return;

            // Плоскостью поперёк хода: паутина перегораживает проход, а не лежит вдоль него.
            var facing = Quaternion.LookRotation(pinch.Direction);

            // Замер до создания объекта: если отверстия здесь нет, паутины не будет вовсе,
            // и создавать её, чтобы тут же снести, незачем.
            var center = pinch.Center + Vector3.up * 1.4f;
            var radius = MeasureOpening(ref center, facing, pinch);

            if (radius < 0f) return;

            var web = Instantiate(prefab, _container);
            web.name = "Web";

            web.transform.position = center;

            // Поворот вокруг оси хода случайный: мешей всего шестнадцать, и без него
            // одна и та же паутина узнаётся с другого конца уровня.
            web.transform.rotation = facing * Quaternion.Euler(0f, 0f, (float)random.NextDouble() * 360f);

            // Подгон под щель. Масштаб равномерный: у мешей есть толщина, и сплющивать
            // её по Z — значит превращать нити обратно в наклейку, ради ухода от которой
            // всё и делалось.
            var span = radius * 2f * Mathf.Lerp(webOvershoot.x, webOvershoot.y, (float)random.NextDouble());

            web.transform.localScale = Vector3.one * (span / Mathf.Max(0.01f, PlanarSize(web)));

            Tint();

            foreach (var view in web.GetComponentsInChildren<MeshRenderer>())
            {
                // Паутина тонкая и полупрозрачная: тени от неё выглядят как грязь, а приём
                // теней делает её чёрной тряпкой.
                view.shadowCastingMode = ShadowCastingMode.Off;
                view.receiveShadows = false;
                view.lightProbeUsage = LightProbeUsage.Off;

                // Цвет через блок свойств, а не через копию материала: материал у всех
                // шестнадцати мешей общий и лежит в паке, а renderer.material наплодил бы
                // копий, которые в режиме редактирования оседают в сцене.
                view.SetPropertyBlock(_tint);
            }

            AddDragVolume(web, radius);
        }

        /// <summary>
        /// Вязкий объём. Коробка-триггер, а не коллайдер по мешу.
        ///
        /// По мешу нельзя: нити тонкие и с прорехами, и попадание зависело бы от того,
        /// прошёл игрок ровно по нити или между. Вязнуть надо во всей паутине целиком —
        /// это же не сетка-рабица, а липкое полотно.
        ///
        /// Триггер, а не глухой коллайдер: проход паутина не запирает, она его замедляет.
        /// Глухой вдобавок нарушил бы связность уровня — щель бывает единственной связью
        /// между кусками, а заливка по полю плотности этого не заметит, она про породу.
        /// </summary>
        private void AddDragVolume(GameObject web, float radius)
        {
            var box = web.AddComponent<BoxCollider>();

            box.isTrigger = true;

            // Размеры в локальных: объект отмасштабирован под щель, и коробка едет вместе
            // с ним. Толщину поперёк задаём в мировых и делим обратно на масштаб — она
            // про шаг игрока за кадр, а не про размер паутины.
            var scale = Mathf.Max(0.001f, web.transform.localScale.x);
            var side = radius * 2f / scale;

            box.size = new Vector3(side, side, webThickness / scale);
            box.center = Vector3.zero;

            web.AddComponent<CaveWeb>().Configure(webSpeedScale, webFallScale);
        }

        /// <summary>
        /// Рвёт паутины в радиусе от точки. Зовётся попаданием — взрывом гранаты, киркой.
        ///
        /// По точке и радиусу, а не по конкретной паутине, потому что оружие в этой игре
        /// гранатомёт: попадание здесь — это область, а не луч. Для луча достаточно взять
        /// <see cref="CaveWeb"/> с того, во что он попал, и позвать Tear.
        /// </summary>
        /// <returns>Сколько паутин порвано.</returns>
        public static int TearAt(Vector3 center, float radius)
        {
            var hits = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Collide);
            var torn = 0;

            foreach (var hit in hits)
            {
                var web = hit.GetComponent<CaveWeb>();

                if (web == null) continue;

                web.Tear();
                torn++;
            }

            return torn;
        }

        /// <summary>
        /// Радиус реально прорезанного отверстия в плоскости щели; заодно доводит центр.
        ///
        /// Номинальный размер из <see cref="CatacombLayout.Pinch"/> для посадки паутины
        /// не годится: он описывает бокс, которым щель вырезали, а поле плотности потом
        /// сужает отверстие шумом стен — настолько, что щель в два юнита шум способен
        /// закрыть целиком. Паутина по номиналу оказывалась вдвое шире дыры и уходила
        /// в породу почти вся, наружу торчал огрызок нитей.
        ///
        /// Луч здесь допустим, хотя связность уровня физикой проверять нельзя: запрет
        /// касается лучей, НАЧАТЫХ внутри сплошной породы — там коллайдеров нет вовсе,
        /// и луч проходит камень насквозь. Этот начинается в пустоте щели и ищет её
        /// стенку изнутри, то есть ровно то, для чего коллайдеры и существуют.
        /// </summary>
        /// <returns>
        /// Радиус отверстия, либо -1, если в этой точке отверстия нет. Второе бывает:
        /// щель — это узкий кусок ЗВЕНА хода, а середина звена попадает и в зал, и в
        /// развилку, где стен вокруг попросту нет. Паутина, посаженная туда, висит
        /// посреди зала на пустом месте — ей не за что держаться.
        /// </returns>
        private static float MeasureOpening(ref Vector3 center, Quaternion facing, CatacombLayout.Pinch pinch)
        {
            var nominal = Mathf.Max(pinch.Width, pinch.Height) * 0.5f;

            var right = facing * Vector3.right;
            var up = facing * Vector3.up;

            // Сначала центр: отверстие редко приходится серединой на ось хода, а паутина,
            // посаженная мимо, наполовину в стене даже при верном размере.
            center += right * 0.5f * (Reach(center, right, nominal) - Reach(center, -right, nominal));
            center += up * 0.5f * (Reach(center, up, nominal) - Reach(center, -up, nominal));

            // Потом радиус — по самому тесному направлению из восьми. Именно оно решает,
            // влезет паутина в дыру или нет.
            var radius = nominal;
            var walls = 0;

            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.PI * 0.25f;
                var direction = right * Mathf.Cos(angle) + up * Mathf.Sin(angle);

                RaycastHit hit;

                if (!Physics.Raycast(center, direction, out hit, nominal * 2f, ~0, QueryTriggerInteraction.Ignore))
                    continue;

                walls++;
                radius = Mathf.Min(radius, hit.distance);
            }

            // Больше половины направлений должны упираться в породу. Меньше — это не щель,
            // а открытое место, и паутине там делать нечего.
            if (walls < 5) return -1f;

            // И слишком тесное место тоже пропускаем: паутина метром поперёк в кадре
            // не читается вовсе — нити теряются в породе, и от неё остаётся грязноватое
            // пятно, по которому щель не узнать. А узнаваемость щели — это её работа.
            return radius < MinReadableRadius ? -1f : radius;
        }

        /// <summary>Расстояние до стенки в направлении, или номинал, если луч ушёл в пустоту.</summary>
        private static float Reach(Vector3 from, Vector3 direction, float nominal)
        {
            RaycastHit hit;

            return Physics.Raycast(from, direction, out hit, nominal * 2f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.distance
                : nominal;
        }

        /// <summary>Наибольшая сторона меша в плоскости паутины, в локальных единицах.</summary>
        private static float PlanarSize(GameObject web)
        {
            var largest = 0f;

            foreach (var filter in web.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;

                var size = filter.sharedMesh.bounds.size;
                largest = Mathf.Max(largest, Mathf.Max(size.x, size.y));
            }

            return largest;
        }

        private void Tint()
        {
            if (_tint == null) _tint = new MaterialPropertyBlock();

            _tint.SetColor(BaseColor, webColor);
        }

        private static void DestroyNow(Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
