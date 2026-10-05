using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Особые пауки (M3.2). Порядок — номер в счётчиках и в профиле (задания M4).</summary>
    public enum SpecialKind
    {
        /// <summary>Плевун: сидит на стене или своде и плюёт кислотой дугой.</summary>
        Spitter,

        /// <summary>Ловчий: подкрадывается, визжит и прыгает — прижимает игрока.</summary>
        Lurker,

        /// <summary>Ткач: стреляет нитью и тянет игрока к толпе.</summary>
        Weaver,

        /// <summary>Бомбардир: идёт к игроку и лопается кислотным облаком — по паукам тоже.</summary>
        Bomber,

        Count
    }

    /// <summary>Что особый собирается сделать — для звука и значка на краю экрана.</summary>
    public enum SpecialCue
    {
        Appeared,
        Spit,
        Shriek,
        Thread,
        Swell
    }

    /// <summary>
    /// Особые пауки посреди орды — Vermintide 2 держит напряжение не числом крыс, а особыми,
    /// которых слышно заранее и которых надо снять первыми (план M3.2).
    ///
    /// Директор особых — бюджет L4D/V2: таймер 20–40 с, короче при высоком давлении, не в рой
    /// (рой — время толпы), не больше двух живых на ярусе 1 и четырёх на ярусе 3. Каждый
    /// появляется вне поля зрения: в полосе спавна толпы, за пределами пирамиды камеры или
    /// за породой.
    ///
    /// Сами особые — <see cref="SoloSpider"/>: обычные классы, а не компоненты; менеджер их
    /// ведёт, рисует и считает. Лужи кислоты и нити — тоже здесь: их единицы.
    ///
    /// Компонент сцены на объекте мира, пересоздаётся «Починить свет» (грабли №8, №25).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CaveSpecials : MonoBehaviour, IShotHandler
    {
        [SerializeField] private CatacombWorld world;
        [SerializeField] private SpiderCrowd crowd;
        [SerializeField] private CaveRunDirector director;
        [SerializeField] private CatacombTestRig rig;

        [Header("Расписание (бюджет L4D/V2)")]
        [Tooltip("Пауза между особыми при низком давлении, секунды.")]
        [SerializeField, Range(5f, 120f)] private float calmInterval = 40f;

        [Tooltip("Пауза между особыми на гребне давления, секунды.")]
        [SerializeField, Range(5f, 120f)] private float peakInterval = 20f;

        [Tooltip("Первый особый — не раньше, секунды стадии: первые полминуты — время освоиться.")]
        [SerializeField, Range(0f, 300f)] private float firstAt = 45f;

        [Tooltip("Живых особых одновременно на ярусе 1 и на ярусе 3 (между — по прямой).")]
        [SerializeField, Range(1, 8)] private int maxAliveTier1 = 2;
        [SerializeField, Range(1, 8)] private int maxAliveTier3 = 4;

        [Header("Живучесть, HP")]
        [SerializeField] private float spitterHealth = 120f;
        [SerializeField] private float lurkerHealth = 160f;
        [SerializeField] private float weaverHealth = 140f;
        [SerializeField] private float bomberHealth = 200f;

        [Tooltip("Ихора за особого, в попаданиях живучести (как вид толпы: мелочь 1, тарантул 4).")]
        [SerializeField, Range(1, 30)] private int ichorHits = 8;

        [Header("Кислота")]
        [Tooltip("Лужа кислоты: радиус, жизнь, урон в секунду.")]
        [SerializeField, Range(0.5f, 5f)] private float puddleRadius = 1.6f;
        [SerializeField, Range(0.5f, 20f)] private float puddleSeconds = 4f;
        [SerializeField, Range(0f, 50f)] private float puddleDps = 6f;

        [SerializeField] private Shader unlitShader;

        public SpiderCrowd Crowd => crowd;
        public CaveRunDirector Director => director;
        public IPlayerBody Player => rig;
        public CatacombTestRig Rig => rig;

        /// <summary>Ярус вылазки (M4): 1–3. Пока вылазки нет — первый.</summary>
        public int Tier { get; set; } = 1;

        /// <summary>Все особые, живые и доигрывающие смерть.</summary>
        public IReadOnlyList<SoloSpider> All => _all;

        public int Alive { get; private set; }

        /// <summary>Сколько каждого появилось и убито — для прогонов и заданий (M4).</summary>
        public readonly int[] Spawned = new int[(int)SpecialKind.Count];
        public readonly int[] Killed = new int[(int)SpecialKind.Count];

        /// <summary>Появившихся в поле зрения игрока — обязан быть ноль (SpecialsCheck).</summary>
        public int SpawnedInView { get; private set; }

        /// <summary>Метка особого: звук и значок на краю экрана.</summary>
        public event Action<SoloSpider, SpecialCue> Cued;

        /// <summary>Отключить расписание — прогоны ставят особых сами.</summary>
        public bool ScheduleEnabled { get; set; } = true;

        public quaternion WorldRotation => world != null ? (quaternion)world.transform.rotation : quaternion.identity;

        private readonly List<SoloSpider> _all = new List<SoloSpider>();
        private readonly List<Puddle> _puddles = new List<Puddle>();
        private readonly System.Random _random = new System.Random(0x5EC1A1);

        private float _nextSpawn;
        private bool _linked;

        private struct Puddle
        {
            public Vector3 Position;
            public float Radius;
            public float Left;
            public Transform View;
        }

        // ------------------------------------------------------------------ связи

        public void EnsureLinks()
        {
            if (_linked && crowd != null && director != null && rig != null) return;

            if (world == null) world = GetComponent<CatacombWorld>() ?? FindAnyObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindAnyObjectByType<SpiderCrowd>();
            if (director == null) director = FindAnyObjectByType<CaveRunDirector>();
            if (rig == null) rig = FindAnyObjectByType<CatacombTestRig>();

#if UNITY_EDITOR
            if (unlitShader == null) unlitShader = Shader.Find("Mine Generator/Cave Unlit");
#endif

            _linked = true;
        }

        private void OnEnable()
        {
            EnsureLinks();
            if (world != null) world.Generated += Clear;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Clear;
            Clear();
        }

        /// <summary>Убрать всех — новый уровень, новый забег, прогон.</summary>
        public void Clear()
        {
            foreach (var special in _all) special.Remove();
            _all.Clear();

            foreach (var puddle in _puddles)
            {
                if (puddle.View != null) DestroyView(puddle.View.gameObject);
            }

            _puddles.Clear();

            Alive = 0;
            SpawnedInView = 0;
            Array.Clear(Spawned, 0, Spawned.Length);
            Array.Clear(Killed, 0, Killed.Length);

            _nextSpawn = firstAt;
        }

        // ------------------------------------------------------------------ кадр

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime);
            Draw();
        }

        /// <summary>Шаг: расписание, особые, лужи. Отдельно — прогоны шагают сами.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            EnsureLinks();

            if (ScheduleEnabled) UpdateSchedule(deltaTime);

            Alive = 0;

            for (var i = _all.Count - 1; i >= 0; i--)
            {
                var special = _all[i];
                special.Tick(deltaTime);

                if (special.Gone)
                {
                    special.Remove();
                    _all.RemoveAt(i);
                    continue;
                }

                if (!special.Dead) Alive++;
            }

            UpdatePuddles(deltaTime);
        }

        public void Draw()
        {
            var layer = gameObject.layer;
            foreach (var special in _all) special.Draw(layer);
        }

        // ------------------------------------------------------------------ расписание

        private void UpdateSchedule(float deltaTime)
        {
            if (director == null || crowd == null || !crowd.HasField) return;
            if (director.Phase != CaveRunDirector.RunPhase.Stage) return;

            // Не в рой: рой — время толпы, и особый в нём не слышен и не виден.
            if (director.SwarmActive) return;

            if (director.Elapsed < _nextSpawn) return;

            var maxAlive = Mathf.RoundToInt(Mathf.Lerp(maxAliveTier1, maxAliveTier3, Mathf.Clamp01((Tier - 1) / 2f)));
            if (Alive >= maxAlive) return;

            var kind = (SpecialKind)_random.Next((int)SpecialKind.Count);

            // Не получилось поставить (некуда вне поля зрения) — попробовать через пару секунд.
            if (SpawnSpecial(kind) == null)
            {
                _nextSpawn = director.Elapsed + 2f;
                return;
            }

            // Пауза короче при высоком давлении: кривая 0.65 → 2.3, пауза 40 → 20 с.
            var pressure = Mathf.InverseLerp(0.65f, 2.3f, director.Intensity);
            _nextSpawn = director.Elapsed + Mathf.Lerp(calmInterval, peakInterval, pressure) * (0.8f + 0.4f * (float)_random.NextDouble());
        }

        // ------------------------------------------------------------------ появление

        /// <summary>
        /// Поставить особого вне поля зрения игрока. Null — места не нашлось.
        /// </summary>
        public SoloSpider SpawnSpecial(SpecialKind kind)
        {
            EnsureLinks();

            if (crowd == null || !crowd.HasField) return null;

            var wall = kind == SpecialKind.Spitter;
            var minSteps = kind == SpecialKind.Spitter ? 12 : 14;
            var maxSteps = kind == SpecialKind.Spitter ? 22 : 26;

            if (!crowd.TryPickSurfaceCell(minSteps, maxSteps, wall ? -1f : 0.7f, wall ? 0.6f : 1.01f, _random,
                    IsHiddenFromPlayer, out var foot, out var up))
            {
                return null;
            }

            return SpawnAt(kind, foot, up);
        }

        /// <summary>Поставить особого в точке (мир) — прогоны и отладка.</summary>
        public SoloSpider SpawnAt(SpecialKind kind, Vector3 worldFoot, Vector3 worldUp)
        {
            EnsureLinks();

            SoloSpider special = kind switch
            {
                SpecialKind.Spitter => new SpitterSpider(),
                SpecialKind.Lurker => new LurkerSpider(),
                SpecialKind.Weaver => new WeaverSpider(),
                _ => new BomberSpider()
            };

            var health = kind switch
            {
                SpecialKind.Spitter => spitterHealth,
                SpecialKind.Lurker => lurkerHealth,
                SpecialKind.Weaver => weaverHealth,
                _ => bomberHealth
            };

            if (director != null) health *= Mathf.Max(0.05f, director.Difficulty.Health);

            if (!special.Bind(crowd)) return null;

            if (IsVisibleToPlayer(worldFoot + worldUp)) SpawnedInView++;

            special.Spawn(this, ToLocal(worldFoot), ToLocalDirection(worldUp), health);
            _all.Add(special);

            Spawned[(int)kind]++;
            Cue(special, SpecialCue.Appeared);

            return special;
        }

        private bool IsHiddenFromPlayer(Vector3 worldPoint) => !IsVisibleToPlayer(worldPoint);

        /// <summary>
        /// Видит ли игрок точку: в пирамиде камеры и не за породой. Камера стенда — из ссылки,
        /// не <c>Camera.main</c>: у стенда нет тега MainCamera (грабли №7).
        /// </summary>
        public bool IsVisibleToPlayer(Vector3 worldPoint)
        {
            if (rig == null) return false;

            var camera = rig.GetComponent<Camera>();
            var eye = rig.transform.position;

            if (camera != null)
            {
                var viewport = camera.WorldToViewportPoint(worldPoint);
                var inside = viewport.z > 0f && viewport.x > -0.05f && viewport.x < 1.05f && viewport.y > -0.05f && viewport.y < 1.05f;
                if (!inside) return false;
            }

            var delta = worldPoint - eye;
            var distance = delta.magnitude;
            if (distance < 0.5f) return true;

            return !Physics.Raycast(eye, delta / distance, distance - 0.3f, ~0, QueryTriggerInteraction.Ignore);
        }

        // ------------------------------------------------------------------ события особых

        public void Cue(SoloSpider special, SpecialCue cue) => Cued?.Invoke(special, cue);

        /// <summary>Особый убит — в журнал толпы с флагом «особый»: ихор, задания, статистика.</summary>
        public void ReportKill(SoloSpider special, DamageSource source)
        {
            Killed[(int)special.Type]++;

            if (crowd == null) return;

            crowd.RecordKill(new KillRecord
            {
                Position = special.Centre,
                Kind = -1,
                KindHealth = ichorHits,
                Elite = false,
                Special = true,
                Source = source
            });
        }

        // ------------------------------------------------------------------ кислота

        /// <summary>Лужа кислоты на полу: урон игроку, пока стоит в ней. Её видно и её обходят.</summary>
        public void AddPuddle(Vector3 worldPoint, Vector3 normal, float scale = 1f)
        {
            var puddle = new Puddle
            {
                Position = worldPoint,
                Radius = puddleRadius * scale,
                Left = puddleSeconds
            };

            if (Application.isPlaying) puddle.View = BuildPuddleView(worldPoint, normal, puddle.Radius);

            _puddles.Add(puddle);
        }

        public int PuddleCount => _puddles.Count;

        private void UpdatePuddles(float deltaTime)
        {
            var body = rig != null ? rig.BodyCentre : Vector3.zero;

            for (var i = _puddles.Count - 1; i >= 0; i--)
            {
                var puddle = _puddles[i];
                puddle.Left -= deltaTime;

                if (puddle.Left <= 0f)
                {
                    if (puddle.View != null) DestroyView(puddle.View.gameObject);
                    _puddles.RemoveAt(i);
                    continue;
                }

                // Стоит в луже: ноги в радиусе по горизонтали и не выше полутора юнитов.
                if (rig != null && !rig.IsDashing)
                {
                    var flat = new Vector2(body.x - puddle.Position.x, body.z - puddle.Position.z);
                    var height = body.y - puddle.Position.y;

                    if (flat.sqrMagnitude < puddle.Radius * puddle.Radius && height > -0.5f && height < 1.8f)
                    {
                        rig.Hurt(DamageHit.Over(puddleDps * deltaTime, puddle.Position, DamageKind.Acid));
                    }
                }

                if (puddle.View != null)
                {
                    var fade = Mathf.Clamp01(puddle.Left / 0.6f);
                    puddle.View.localScale = Vector3.one * (puddle.Radius * 2f * Mathf.Lerp(0.6f, 1f, fade));
                }

                _puddles[i] = puddle;
            }
        }

        private Material _puddleMaterial;

        private Transform BuildPuddleView(Vector3 point, Vector3 normal, float radius)
        {
            if (_puddleMaterial == null)
            {
                _puddleMaterial = CaveMaterials.Additive("Acid Puddle", SoftDisc(), new Color(0.35f, 0.9f, 0.15f, 1f));
                _puddleMaterial.hideFlags = HideFlags.DontSave;
            }

            var go = new GameObject("Acid Puddle") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, true);
            go.transform.position = point + normal * 0.05f;
            go.transform.rotation = Quaternion.LookRotation(-normal);
            go.transform.localScale = Vector3.one * radius * 2f;

            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _puddleMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            return go.transform;
        }

        private static Texture2D _disc;

        /// <summary>Мягкий диск с рваным краем — пятно кислоты. Один на всех (ключ не нужен: цвет — в материале).</summary>
        private static Texture2D SoftDisc()
        {
            if (_disc != null) return _disc;

            const int size = 64;
            _disc = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };

            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size * 2f - 1f;
                var v = (y + 0.5f) / size * 2f - 1f;
                var r = Mathf.Sqrt(u * u + v * v);
                var angle = Mathf.Atan2(v, u);
                var edge = 0.8f + 0.12f * Mathf.Sin(angle * 5f) + 0.06f * Mathf.Sin(angle * 11f + 1.3f);
                var a = Mathf.Clamp01((edge - r) / 0.25f);

                pixels[y * size + x] = new Color(1f, 1f, 1f, a * a);
            }

            _disc.SetPixels(pixels);
            _disc.Apply(false, true);

            return _disc;
        }

        private static void DestroyView(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // ------------------------------------------------------------------ снаряды особых

        private const int TagAcid = 1;
        private const int TagThread = 2;

        /// <summary>Попадание кислоты — 10 HP; плевок летит дугой со скоростью 16 по горизонтали.</summary>
        private const float AcidDamage = 10f;
        private const float AcidSpeed = 16f;
        private const float AcidGravity = 12f;
        private const float ThreadSpeed = 40f;

        private ProjectileSystem _projectiles;
        private int _shotOwner = -1;
        private int _acidVisual = -1;
        private int _threadVisual = -1;

        /// <summary>Сколько кислоты попало в игрока и сколько отбито — для прогона.</summary>
        public int AcidHits { get; private set; }
        public int ThreadHits { get; private set; }

        /// <summary>
        /// Общая система снарядов — та же, что у пушек: плевки особых, как и плевки Матки, отбивает
        /// парирование кирки (<see cref="ProjectileSystem.Parry"/>), и полёт считает один код.
        /// </summary>
        private bool EnsureProjectiles()
        {
            var arsenal = rig != null ? rig.GetComponent<PlayerArsenal>() : null;
            var system = arsenal != null && arsenal.Context != null ? arsenal.Context.Projectiles : null;

            if (system == null) return false;
            if (system == _projectiles && _shotOwner >= 0) return true;

            _projectiles = system;
            _shotOwner = system.Register(this);

            var materials = arsenal.Context.Materials;

            _acidVisual = system.AddVisual(new ShotVisual
            {
                Mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx"),
                Materials = new[] { materials.Lit("Acid Blob", new Color(0.3f, 0.75f, 0.15f), 0f, 0.8f, new Color(0.35f, 0.9f, 0.15f)) },
                Scale = 0.32f,
                AlignToVelocity = false,
                StreakHead = new Color(0.5f, 1f, 0.25f, 0.8f),
                StreakTail = new Color(0.2f, 0.6f, 0.1f, 0f),
                StreakLength = 0.8f,
                StreakWidth = 0.12f
            });

            _threadVisual = system.AddVisual(new ShotVisual
            {
                Mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx"),
                Materials = new[] { materials.Lit("Thread Tip", new Color(0.85f, 0.88f, 0.9f), 0f, 0.5f, new Color(0.3f, 0.32f, 0.35f)) },
                Scale = 0.12f,
                AlignToVelocity = false,
                StreakHead = new Color(0.9f, 0.95f, 1f, 0.9f),
                StreakTail = new Color(0.8f, 0.85f, 0.9f, 0.3f),
                StreakLength = 2.5f,
                StreakWidth = 0.03f
            });

            return true;
        }

        /// <summary>Плевок кислотой дугой в точку (с упреждением нет: дуга медленная, её видно и обходят).</summary>
        public void FireAcid(SoloSpider from, Vector3 origin, Vector3 target)
        {
            if (!EnsureProjectiles()) return;


            var flat = new Vector3(target.x - origin.x, 0f, target.z - origin.z);
            var time = Mathf.Max(0.25f, flat.magnitude / AcidSpeed);
            var velocity = flat / time + Vector3.up * ((target.y - origin.y + 0.5f * AcidGravity * time * time) / time);

            _projectiles.Launch(new Shot
            {
                Owner = _shotOwner,
                Visual = _acidVisual,
                Flags = ShotFlags.Hostile,
                Position = origin,
                Velocity = velocity,
                Gravity = AcidGravity,
                Radius = 0.25f,
                Life = 4f,
                Tag = TagAcid
            });
        }

        /// <summary>Нить ткача — быстрый прямой снаряд; попала в игрока — ткач тянет.</summary>
        public void FireThread(WeaverSpider from, Vector3 origin, Vector3 target)
        {
            if (!EnsureProjectiles()) return;

            _projectiles.Launch(new Shot
            {
                Owner = _shotOwner,
                Visual = _threadVisual,
                Flags = ShotFlags.Hostile,
                Position = origin,
                Velocity = (target - origin).normalized * ThreadSpeed,
                Gravity = 0f,
                Radius = 0.15f,
                Life = 1.2f,
                Tag = TagThread,
                Link = _all.IndexOf(from)
            });
        }

        public void OnShot(ref Shot shot, ShotEvent what, in RaycastHit hit)
        {
            switch (what)
            {
                case ShotEvent.HitPlayer when shot.Tag == TagAcid:
                    AcidHits++;
                    rig.Hurt(DamageHit.Simple(AcidDamage, shot.Position, DamageKind.Acid));
                    shot.Dead = true;
                    break;

                case ShotEvent.HitPlayer when shot.Tag == TagThread:
                    ThreadHits++;
                    if (shot.Link >= 0 && shot.Link < _all.Count && _all[shot.Link] is WeaverSpider weaver) weaver.Catch();
                    shot.Dead = true;
                    break;

                case ShotEvent.HitRock when shot.Tag == TagAcid:
                    // На полу — лужа; на стене и своде кислота стекает и пропадает.
                    if (hit.normal.y > 0.5f) AddPuddle(hit.point, hit.normal);
                    shot.Dead = true;
                    break;

                default:
                    shot.Dead = true;
                    break;
            }
        }

        /// <summary>Облако бомбардира: лужа на камне и зелёный взрыв.</summary>
        public void AcidCloud(Vector3 centre, Vector3 up, float radius)
        {
            var arsenal = rig != null ? rig.GetComponent<PlayerArsenal>() : null;
            var explosions = arsenal != null && arsenal.Context != null ? arsenal.Context.Explosions : null;

            explosions?.Explode(centre, up, radius * 0.8f);
            arsenal?.Context?.Audio?.Play(SoundId.Explosion, 0.8f, 0.7f, centre, 4);

            if (Physics.Raycast(centre, Vector3.down, out var floor, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                AddPuddle(floor.point, floor.normal, 1.6f);
            }
        }

        /// <summary>Нить ткача, пока тянет, — линией эффектов пушек (один меш на всё).</summary>
        public void DrawThread(Vector3 from, Vector3 to)
        {
            var arsenal = rig != null ? rig.GetComponent<PlayerArsenal>() : null;
            arsenal?.Context?.Fx?.Line(from, to, 0.025f, new Color(0.85f, 0.9f, 1f, 0.9f), new Color(0.7f, 0.75f, 0.85f, 0.6f), 0.05f);
        }

        // ------------------------------------------------------------------ координаты

        public bool TryGetSampler(out SpiderFlowField.FlowSampler sampler)
        {
            sampler = default;
            return crowd != null && crowd.TryGetSampler(out sampler);
        }

        public Vector3 ToWorld(float3 local) => world != null ? world.transform.TransformPoint(local) : (Vector3)local;

        public float3 ToLocal(Vector3 worldPoint) => world != null ? (float3)world.transform.InverseTransformPoint(worldPoint) : (float3)worldPoint;

        public float3 ToLocalDirection(Vector3 worldDirection) =>
            world != null ? (float3)world.transform.InverseTransformDirection(worldDirection) : (float3)worldDirection;
    }
}
