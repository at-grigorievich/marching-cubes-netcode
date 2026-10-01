using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Многозарядный гранатомёт игрока: модель от первого лица, стрельба гранатами, взрывы.
    ///
    /// Оружие игры — гранатомёт «на отходе по коридору»: орда бежит по тоннелю, игрок пятится
    /// и кладёт гранаты в гущу. Отсюда числа: граната летит почти прямо (дальность боя —
    /// медиана линии 14-16 юнитов), дугу гранатомёт подбирает сам так, чтобы граната упала
    /// в перекрестие, а радиус взрыва 5 — чтобы взрыв на дистанции боя не доставал до игрока.
    ///
    /// Взрыв делает ровно то, что делала заготовка на клавишу B: рвёт паутину
    /// (<see cref="CaveWebs.TearAt"/>), бьёт пауков (<see cref="SpiderCrowd.DamageAt(Vector3, float, int, List{Vector3})"/>)
    /// и Матку (<see cref="CaveQueen.DamageAt"/>) — и поверх этого рисует взрыв
    /// (<see cref="CaveExplosions"/>) и брызги разорванных.
    ///
    /// Модель, материалы и эффекты создаются только в play-режиме и в сцену не пишутся:
    /// шейдеры приезжают ссылками (их проставляет «Починить свет»), чтобы попасть в сборку.
    /// </summary>
    [RequireComponent(typeof(CatacombTestRig))]
    public sealed class CaveGrenadeLauncher : MonoBehaviour
    {
        [Tooltip("Толпа пауков. Пусто — найдётся на сцене сама.")]
        [SerializeField] private SpiderCrowd crowd;

        [Tooltip("Матка. Пусто — найдётся на сцене сама.")]
        [SerializeField] private CaveQueen queen;

        [Tooltip("Мир катакомб — в нём копаются воронки. Пусто — найдётся на сцене сам.")]
        [SerializeField] private CatacombWorld world;

        [Header("Шейдеры (проставляет «Починить свет»)")]
        [Tooltip("Universal Render Pipeline/Lit — металл и полимер гранатомёта.")]
        [SerializeField] private Shader litShader;

        [Tooltip("Mine Generator/Cave Particles — огонь, дым, искры, вспышка у дула.")]
        [SerializeField] private Shader particleShader;

        [Header("Стрельба")]
        [Tooltip("Гранат в барабане.")]
        [SerializeField, Range(1, 12)] private int capacity = 6;

        [Tooltip("Не чаще этого, секунды между выстрелами. Барабан проворачивается на каждый.")]
        [SerializeField, Range(0.1f, 1.5f)] private float fireInterval = 0.32f;

        [Tooltip("Перезарядка всего барабана, секунды.")]
        [SerializeField, Range(0.3f, 5f)] private float reloadTime = 2.1f;

        [Tooltip("Начальная скорость гранаты, юниты в секунду.")]
        [SerializeField, Range(5f, 80f)] private float muzzleSpeed = 30f;

        [Tooltip("Тяжесть для гранаты, юниты в секунду за секунду. Меньше земной: в коридоре нужна " +
                 "почти прямая траектория, а не миномётная дуга.")]
        [SerializeField, Range(0f, 30f)] private float grenadeGravity = 9f;

        [Tooltip("Через столько секунд полёта граната рвётся сама.")]
        [SerializeField, Range(0.5f, 8f)] private float maxFlight = 3.5f;

        [Tooltip("Граната рвётся, пролетая ближе этого к пауку или Матке, юниты.")]
        [SerializeField, Range(0f, 3f)] private float proximity = 0.9f;

        [Header("Взрыв")]
        [Tooltip("Радиус поражения, юниты.")]
        [SerializeField, Range(1f, 12f)] private float blastRadius = 5f;

        [Tooltip("Урон паукам в радиусе. Мелочь держит одно попадание, тарантул — четыре.")]
        [SerializeField, Range(1, 10)] private int blastDamage = 3;

        [Tooltip("Урон Матке за попадание.")]
        [SerializeField, Range(0f, 10f)] private float queenDamage = 1.5f;

        [Header("Следы взрыва")]
        [Tooltip("Копать ли воронку в породе. Гарь остаётся и без неё.")]
        [SerializeField] private bool craters = true;

        [Tooltip("Радиус сферы, которой выкапывается воронка, юниты. Воксель породы — юнит, " +
                 "и сфера меньше полутора в меше почти не проявляется.")]
        [SerializeField, Range(0.8f, 4f)] private float craterRadius = 1.7f;

        [Tooltip("Насколько воронка уходит в камень при ударе вплотную, юниты. Взрыв в воздухе " +
                 "копает мельче, выше радиуса воронки — не копает вовсе.")]
        [SerializeField, Range(0f, 2f)] private float craterDepth = 0.85f;

        [Tooltip("Радиус пятна гари вокруг взрыва, юниты. Сплошная копоть — в центральной трети, " +
                 "дальше лучи и рваный край. Три юнита и больше заливали весь пол в кадре.")]
        [SerializeField, Range(0.5f, 6f)] private float scorchRadius = 2.4f;

        [Header("Вид от первого лица")]
        [SerializeField] private Vector3 viewOffset = new Vector3(0.21f, -0.2f, 0.3f);

        [Tooltip("Насколько оружие отстаёт от поворота мыши.")]
        [SerializeField, Range(0f, 3f)] private float sway = 1.2f;

        private CatacombTestRig _rig;
        private CaveExplosions _explosions;
        private CaveBlastMarks _marks;

        private Transform _view;
        private Transform _drum;
        private Renderer _flash;
        private Transform _flashTransform;

        private readonly List<Object> _owned = new List<Object>();
        private readonly List<Grenade> _grenades = new List<Grenade>();
        private readonly List<Vector3> _gibs = new List<Vector3>();

        private Material _grenadeBody;
        private Material _grenadeBand;
        private Material _trail;
        private Mesh _grenadeMesh;

        private int _ammo;
        private float _nextShot;
        private float _reloadLeft;
        private float _kick;
        private float _drumAngle;
        private float _drumTarget;
        private float _flashLeft;
        private float _retract;
        private float _bobPhase;
        private Vector2 _sway;
        private Vector3 _lastPosition;

        /// <summary>Сколько пауков убито гранатами за сессию — для интерфейса и прогона.</summary>
        public int Kills { get; private set; }

        public int Fired { get; private set; }

        public int Ammo => _ammo;

        public int Capacity => capacity;

        public bool IsReloading => _reloadLeft > 0f;

        public float BlastRadius => blastRadius;

        /// <summary>Воронки и гарь. Есть только в play-режиме.</summary>
        public CaveBlastMarks Marks => _marks;

        private sealed class Grenade
        {
            public GameObject Object;
            public TrailRenderer Trail;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Age;
            public bool Live;
        }

        private void Awake()
        {
            _rig = GetComponent<CatacombTestRig>();
            _ammo = capacity;
        }

        private void Start()
        {
            if (!Application.isPlaying) return;

            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
            if (queen == null) queen = FindFirstObjectByType<CaveQueen>();

#if UNITY_EDITOR
            // В редакторе можно и без проставленных ссылок; в сборке найдутся только
            // проставленные — см. комментарий к классу.
            if (litShader == null) litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (particleShader == null) particleShader = Shader.Find("Mine Generator/Cave Particles");
#endif

            if (litShader == null || particleShader == null)
            {
                Debug.LogError("ГРАНАТОМЁТ: не проставлены шейдеры — запустите «Починить свет и туман в текущей сцене»");
                enabled = false;
                return;
            }

            BuildView();

            var effects = new GameObject("Cave Explosions") { hideFlags = HideFlags.DontSave };
            _explosions = effects.AddComponent<CaveExplosions>();
            _explosions.Build(particleShader);
            _owned.Add(effects);

            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            _marks = effects.AddComponent<CaveBlastMarks>();
            _marks.Bind(world, crowd, transform, craterRadius, craters ? craterDepth : 0f, scorchRadius);

            _lastPosition = transform.position;
        }

        private void OnDestroy()
        {
            foreach (var owned in _owned)
            {
                if (owned != null) Destroy(owned);
            }
        }

        // ----------------------------------------------------------------- ввод и стрельба

        private void Update()
        {
            if (_view == null) return;

            var dt = Time.deltaTime;

            if (_reloadLeft > 0f)
            {
                _reloadLeft -= dt;

                // Барабан наполняется ближе к концу — когда гранатомёт уже возвращается в руку.
                if (_reloadLeft <= reloadTime * 0.3f) _ammo = capacity;
                if (_reloadLeft <= 0f) _reloadLeft = 0f;
            }

            if (_rig.IsLooking && _reloadLeft <= 0f)
            {
                if (Input.GetKeyDown(KeyCode.V) && _ammo < capacity) StartReload();
                else if (Input.GetMouseButton(0) && Time.time >= _nextShot) Fire();
            }

            // Пустой барабан перезаряжается сам: считать выстрелы посреди роя игроку незачем.
            if (_ammo == 0 && _reloadLeft <= 0f && Time.time >= _nextShot) StartReload();

            UpdateGrenades(dt);
        }

        private void Fire()
        {
            if (_ammo <= 0) return;

            _ammo--;
            Fired++;
            _nextShot = Time.time + fireInterval;
            _kick = 1f;
            _drumTarget += 60f;

            var eye = transform;
            var muzzle = _view.TransformPoint(LauncherModel.Muzzle);

            // Куда смотрит перекрестие. Луч из глаз стартует внутри капсулы игрока и её не видит.
            var aim = Physics.Raycast(eye.position, eye.forward, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : eye.position + eye.forward * 50f;

            // Ствол у самой стены может оказаться в породе — тогда граната выходит из глаз.
            var start = Physics.Linecast(eye.position, muzzle, out _, ~0, QueryTriggerInteraction.Ignore)
                ? eye.position + eye.forward * 0.25f
                : muzzle;

            var toAim = aim - start;
            var distance = Mathf.Max(0.5f, toAim.magnitude);

            // Дуга под перекрестие: подброс ровно на столько, сколько граната просядет за время
            // полёта до точки прицела. Для малых углов этого достаточно, и игрок целится
            // перекрестием, а не поправками на дальность.
            var flight = Mathf.Min(distance / muzzleSpeed, 1.2f);
            var velocity = toAim / distance * muzzleSpeed + Vector3.up * (0.5f * grenadeGravity * flight);

            Launch(start, velocity);

            _flashLeft = 0.055f;
            _flashTransform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            _flashTransform.localScale = Vector3.one * Random.Range(0.13f, 0.19f);

            _explosions.MuzzleLight(muzzle + eye.forward * 0.3f);

            _rig.AddKick(2.4f);
            _rig.AddShake(0.12f);
        }

        private void StartReload()
        {
            if (_reloadLeft > 0f || _ammo >= capacity) return;

            _reloadLeft = reloadTime;
            _drumTarget += 720f;
        }

        // ----------------------------------------------------------------- гранаты

        private void Launch(Vector3 position, Vector3 velocity)
        {
            Grenade grenade = null;

            foreach (var g in _grenades)
            {
                if (!g.Live) { grenade = g; break; }
            }

            if (grenade == null)
            {
                grenade = CreateGrenade();
                _grenades.Add(grenade);
            }

            grenade.Position = position;
            grenade.Velocity = velocity;
            grenade.Age = 0f;
            grenade.Live = true;

            grenade.Object.transform.SetPositionAndRotation(position, Quaternion.LookRotation(velocity));
            grenade.Object.SetActive(true);
            grenade.Trail.Clear();
        }

        private void UpdateGrenades(float dt)
        {
            foreach (var g in _grenades)
            {
                if (!g.Live) continue;

                g.Age += dt;

                var from = g.Position;
                g.Velocity += Vector3.down * (grenadeGravity * dt);

                var step = g.Velocity * dt;
                var length = step.magnitude;

                // Удар о камень. Луч начинается в пустоте хода, где летит граната, — грабли №1
                // про лучи из толщи породы здесь не про него.
                if (length > 1e-5f && Physics.SphereCast(from, 0.06f, step / length, out var hit, length, ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    Release(g);
                    Detonate(hit.point + hit.normal * 0.12f, hit.normal);
                    continue;
                }

                g.Position = from + step;
                g.Object.transform.SetPositionAndRotation(g.Position, Quaternion.LookRotation(g.Velocity));

                // Взрыватель по близости: пауки коллайдеров не имеют, и лучом их не задеть.
                var nearSpider = crowd != null && g.Age > 0.04f && crowd.TouchesSpider(g.Position, proximity);
                var nearQueen = queen != null && queen.IsVulnerable &&
                                (g.Position - queen.Centre).magnitude < queen.HitRadius + proximity;

                if (nearSpider || nearQueen || g.Age >= maxFlight)
                {
                    Release(g);
                    Detonate(g.Position, -g.Velocity.normalized);
                }
            }
        }

        private void Release(Grenade grenade)
        {
            grenade.Live = false;
            grenade.Object.SetActive(false);
        }

        /// <summary>
        /// Взрыв в точке: паутина, пауки, Матка, эффекты, брызги, тряска камеры.
        /// Публичный — им же пользуется отладочный взрыв стенда на клавишу B.
        /// </summary>
        public int Detonate(Vector3 point, Vector3 normal)
        {
            // Воронка — первой: разорванные меряют высоту падения кусков по плотности,
            // и куски должны лечь уже на дно ямы, а не повиснуть над ним.
            if (_marks != null) _marks.Blast(point);

            CaveWebs.TearAt(point, blastRadius);

            var killed = crowd != null ? crowd.DamageAt(point, blastRadius, blastDamage, _gibs) : 0;

            if (queen != null) queen.DamageAt(point, blastRadius, queenDamage);

            Kills += killed;

            if (_explosions != null)
            {
                _explosions.Explode(point, normal, blastRadius);

                // Брызг не больше четырёх десятков на взрыв: по плотной куче рвёт и сотню,
                // а частиц от этого больше, чем глаз различает.
                var count = Mathf.Min(_gibs.Count, 40);

                for (var i = 0; i < count; i++)
                {
                    var away = (_gibs[i] - point).normalized + normal * 0.6f;
                    _explosions.Gore(_gibs[i], away);
                }
            }

            // Тряска по удалённости: у самого игрока — сильно, на дистанции боя — толчок.
            var distance = (point - transform.position).magnitude;
            _rig.AddShake(Mathf.Clamp01(1.2f - distance / 22f) * 1.1f);

            return killed;
        }

        // ----------------------------------------------------------------- вид от первого лица

        private void LateUpdate()
        {
            if (_view == null) return;

            var dt = Mathf.Max(Time.deltaTime, 1e-4f);

            // Отдача — пружина: выстрел бросает назад и вверх, дальше плавно возвращает.
            _kick = Mathf.Lerp(_kick, 0f, dt * 11f);

            _drumAngle = Mathf.Lerp(_drumAngle, _drumTarget, dt * (_reloadLeft > 0f ? 5f : 22f));
            _drum.localRotation = Quaternion.Euler(0f, 0f, _drumAngle);

            // Отставание от мыши: оружие чуть догоняет поворот, как в руках.
            var mouse = _rig.IsLooking
                ? new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"))
                : Vector2.zero;

            _sway = Vector2.Lerp(_sway, Vector2.ClampMagnitude(mouse, 3f) * sway, dt * 9f);

            // Покачивание на ходу — от горизонтальной скорости игрока.
            var moved = transform.position - _lastPosition;
            _lastPosition = transform.position;

            var speed = new Vector2(moved.x, moved.z).magnitude / dt;
            var walk = Mathf.Clamp01(speed / 6f);

            _bobPhase += speed * dt * 1.7f;

            var bob = new Vector3(Mathf.Sin(_bobPhase) * 0.007f, Mathf.Abs(Mathf.Cos(_bobPhase)) * 0.009f, 0f) * walk;

            // Упёрся в стену — оружие отводится назад и вниз, а не протыкает породу.
            var wall = Physics.Raycast(transform.position, transform.forward, out var hit, 1.1f, ~0,
                QueryTriggerInteraction.Ignore)
                ? Mathf.Clamp01(1f - (hit.distance - 0.35f) / 0.7f)
                : 0f;

            _retract = Mathf.Lerp(_retract, wall, dt * 10f);

            // Перезарядка: гранатомёт уходит вниз и заваливается набок, барабан крутится.
            var reload = _reloadLeft > 0f ? Mathf.Sin(Mathf.PI * (1f - _reloadLeft / reloadTime)) : 0f;

            var kick = _kick * _kick;

            _view.localPosition = viewOffset + bob
                                  + new Vector3(0f, 0.012f, -0.08f) * kick
                                  + new Vector3(-0.03f, -0.1f, -0.05f) * reload
                                  + new Vector3(-0.02f, -0.06f, -0.24f) * _retract;

            _view.localRotation = Quaternion.Euler(
                -10f * kick + 18f * reload + 22f * _retract + _sway.y * 1.6f,
                -_sway.x * 1.8f - 2f,
                38f * reload - _sway.x * 1.2f);

            // Вспышка у дула — несколько кадров.
            _flashLeft -= dt;
            _flash.enabled = _flashLeft > 0f;
        }

        // ----------------------------------------------------------------- сборка

        private void BuildView()
        {
            // Металл светлее и менее металлический, чем был бы в жизни: отражать в пещере нечего
            // (ни неба, ни проб), и сильно металлический материал читался чёрным пятном,
            // на котором свет фонаря давал лишь редкий блик.
            var metal = Lit("Launcher Metal", new Color(0.26f, 0.26f, 0.28f), 0.45f, 0.55f);
            var polymer = Lit("Launcher Polymer", new Color(0.3f, 0.3f, 0.21f), 0f, 0.35f);
            var dark = Lit("Launcher Dark", new Color(0.025f, 0.025f, 0.025f), 0.3f, 0.2f);
            var brass = Lit("Launcher Brass", new Color(0.74f, 0.54f, 0.22f), 1f, 0.62f);

            _grenadeBody = Lit("Grenade Body", new Color(0.25f, 0.28f, 0.14f), 0.2f, 0.35f);
            _grenadeBand = brass;

            _view = new GameObject("Grenade Launcher View") { hideFlags = HideFlags.DontSave }.transform;
            _view.SetParent(transform, false);
            _view.localPosition = viewOffset;

            // Чуть мельче натуральной величины: в натуральную барабан у правого нижнего угла
            // занимал четверть кадра.
            _view.localScale = Vector3.one * 0.88f;
            _owned.Add(_view.gameObject);

            Part(_view, "Body", Owned(LauncherModel.BuildBody()), metal, polymer, dark);

            _drum = Part(_view, "Drum", Owned(LauncherModel.BuildDrum()), metal, brass, dark).transform;

            // Точка коллиматора: светится сама, видна игроку сзади сквозь рамку.
            var dotMaterial = Particle("Sight Dot", Paint(64, (u, v, r) =>
            {
                var a = Mathf.Clamp01(1f - r * 1.2f);
                return new Color(1f, 1f, 1f, a * a * a);
            }), true);

            dotMaterial.color = new Color(1f, 0.15f, 0.1f, 1f);

            var dot = Part(_view, "Sight Dot", Owned(LauncherModel.BuildQuad()), dotMaterial);
            dot.transform.localPosition = LauncherModel.SightDot;
            dot.transform.localScale = Vector3.one * 0.014f;

            // Вспышка у дула: язык пламени вдоль ствола и звезда поперёк.
            var flashMaterial = Particle("Muzzle Flash", Paint(64, (u, v, r) =>
            {
                // Вдоль v — от дула (0) к кончику языка (1); по u — поперёк.
                var along = (v + 1f) * 0.5f;
                var width = Mathf.Lerp(0.9f, 0.1f, along);
                var body = Mathf.Clamp01(1f - Mathf.Abs(u) / width) * Mathf.Clamp01(1f - along);
                var star = Mathf.Clamp01(1f - r) * (0.5f + 0.5f * Mathf.Abs(Mathf.Cos(Mathf.Atan2(v, u) * 3f)));
                var a = Mathf.Clamp01(Mathf.Max(body, star * star));

                return new Color(1f, 0.85f + 0.15f * a, 0.6f + 0.4f * a, a);
            }), true);

            flashMaterial.color = new Color(1f, 0.72f, 0.38f, 1f);

            var flash = Part(_view, "Muzzle Flash", Owned(LauncherModel.BuildMuzzleFlash()), flashMaterial);
            _flashTransform = flash.transform;
            _flashTransform.localPosition = LauncherModel.Muzzle;
            _flash = flash;
            _flash.enabled = false;

            _grenadeMesh = Owned(LauncherModel.BuildGrenade());

            _trail = Particle("Grenade Trail", Paint(32, (u, v, r) => new Color(1f, 1f, 1f, Mathf.Clamp01(1f - Mathf.Abs(u)))), true);
        }

        private Grenade CreateGrenade()
        {
            var go = new GameObject("Grenade") { hideFlags = HideFlags.DontSave };
            go.transform.localScale = Vector3.one * 1.6f;
            _owned.Add(go);

            go.AddComponent<MeshFilter>().sharedMesh = _grenadeMesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { _grenadeBody, _grenadeBand };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // След: горячий у гранаты, остывает и тает — по нему видно, куда ушёл выстрел.
            var trail = go.AddComponent<TrailRenderer>();
            trail.sharedMaterial = _trail;
            trail.time = 0.22f;
            trail.minVertexDistance = 0.15f;
            trail.widthCurve = AnimationCurve.EaseInOut(0f, 0.09f, 1f, 0.01f);
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.75f, 0.4f), 0f), new GradientColorKey(new Color(0.6f, 0.25f, 0.1f), 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;

            go.SetActive(false);

            return new Grenade { Object = go, Trail = trail };
        }

        private Renderer Part(Transform parent, string name, Mesh mesh, params Material[] materials)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;

            // Тень от оружия у лица легла бы на весь кадр пятном от фонаря.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            return renderer;
        }

        private Material Lit(string name, Color color, float metallic, float smoothness)
        {
            var material = new Material(litShader) { name = name, hideFlags = HideFlags.DontSave };

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);

            _owned.Add(material);
            return material;
        }

        private Material Particle(string name, Texture2D texture, bool additive)
        {
            var material = new Material(particleShader) { name = name, hideFlags = HideFlags.DontSave };

            material.mainTexture = texture;
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)(additive
                ? UnityEngine.Rendering.BlendMode.One
                : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_FogToBlack", additive ? 1f : 0f);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            _owned.Add(material);
            return material;
        }

        private Texture2D Paint(int size, System.Func<float, float, float, Color> paint)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };

            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size * 2f - 1f;
                    var v = (y + 0.5f) / size * 2f - 1f;

                    pixels[y * size + x] = paint(u, v, Mathf.Sqrt(u * u + v * v));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(true, true);

            _owned.Add(texture);
            return texture;
        }

        private T Owned<T>(T asset) where T : Object
        {
            _owned.Add(asset);
            return asset;
        }

        // ----------------------------------------------------------------- интерфейс

        private void OnGUI()
        {
            if (_view == null || !_rig.IsLooking) return;

            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var previous = GUI.color;

            // Перекрестие: четыре штриха с просветом — центр открыт, видно, куда летит граната.
            GUI.color = new Color(1f, 0.9f, 0.75f, 0.85f);

            const float gap = 6f;
            const float len = 9f;

            GUI.DrawTexture(new Rect(centre.x - gap - len, centre.y - 1f, len, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centre.x + gap, centre.y - 1f, len, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centre.x - 1f, centre.y - gap - len, 2f, len), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centre.x - 1f, centre.y + gap, 2f, len), Texture2D.whiteTexture);

            // Барабан: по гнезду на гранату, справа внизу.
            var x0 = Screen.width - 40f - capacity * 22f;
            var y0 = Screen.height - 64f;

            for (var i = 0; i < capacity; i++)
            {
                GUI.color = i < _ammo ? new Color(1f, 0.72f, 0.3f, 0.95f) : new Color(0.35f, 0.3f, 0.25f, 0.6f);
                GUI.DrawTexture(new Rect(x0 + i * 22f, y0, 16f, 26f), Texture2D.whiteTexture);
            }

            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleRight };

            GUI.color = new Color(1f, 0.85f, 0.65f, 0.95f);
            GUI.Label(new Rect(Screen.width - 340f, y0 + 30f, 300f, 22f),
                _reloadLeft > 0f ? "ПЕРЕЗАРЯДКА" : $"гранат {_ammo}/{capacity}    убито {Kills}", style);

            GUI.color = previous;
        }
    }
}
