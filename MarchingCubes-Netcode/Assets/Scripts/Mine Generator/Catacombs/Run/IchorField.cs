using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Ихор — опыт с убитых. Капли остаются на месте убийства и ПОЛЗУТ К ИГРОКУ по тем же ходам,
    /// что пауки (поле потока толпы).
    ///
    /// Это решает конфликт жанра: в коридорном отходе убитые лежат впереди, а игрок пятится
    /// назад, и без этого собирать опыт значило бы лезть в толпу. Капля лежит три секунды
    /// (риск для жадных: подбери сам — получишь раньше), потом ползёт, разгоняясь с 2 до 8 юнитов
    /// в секунду. Магнит (радиус 3, стат «Магнит») тянет напрямую; подбор с 1.2 юнита.
    ///
    /// Ценность — живучесть вида в попаданиях (1–4, элита 30), умноженная на опыт за попадание
    /// (0.06 — подобран ботом прогона забега под 9–12 уровней к 5:00). Новая капля ближе полутора юнитов
    /// к живой сливается с ней: взрыв по куче убивает полсотни, капель выходит десяток. Потолок
    /// живых — 600; сверх него — слияние в ближайшую.
    ///
    /// Тут же хитин — валюта меты (с элиты, Матки и одного из ~30 убитых), и кокон с добычей
    /// с элиты: подбирается вплотную, даёт гарантированную карточку эволюции или улучшения.
    ///
    /// Шаг — Burst-задача на 600 элементов, отрисовка — один динамический меш, один вызов.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IchorField : MonoBehaviour
    {
        public const int Capacity = 600;

        [Tooltip("Mine Generator/Cave Ichor — проставляет «Починить свет».")]
        [SerializeField] private Shader shader;

        [SerializeField] private CatacombWorld world;
        [SerializeField] private SpiderCrowd crowd;

        [Header("Капли")]
        [Tooltip("Сколько секунд капля лежит, прежде чем поползёт к игроку.")]
        [SerializeField, Range(0f, 10f)] private float lieSeconds = 3f;

        [SerializeField, Range(0.5f, 10f)] private float crawlFrom = 2f;
        [SerializeField, Range(1f, 30f)] private float crawlTo = 8f;

        [Tooltip("Разгон ползущей капли, юнитов в секунду за секунду.")]
        [SerializeField, Range(0.5f, 20f)] private float crawlRamp = 3f;

        [SerializeField, Range(1f, 40f)] private float magnetSpeed = 14f;

        [SerializeField, Range(0.3f, 4f)] private float pickupRadius = 1.2f;

        [Tooltip("Ближе этого новая капля сливается с живой того же вида.")]
        [SerializeField, Range(0f, 5f)] private float mergeRadius = 1.5f;

        [Header("Ценность")]
        /// <summary>
        /// Сколько опыта даёт одно попадание живучести убитого (мелочь — одно, тарантул — четыре).
        /// Подбирается ботом прогона по целевому окну уровней (M1.7), см. CatacombRunCheck.
        /// </summary>
        [Tooltip("Опыта за одно попадание живучести убитого.")]
        [SerializeField, Range(0.01f, 4f)] private float xpPerHit = 0.06f;

        [SerializeField, Range(1f, 200f)] private float eliteIchor = 30f;
        [SerializeField, Range(0f, 200f)] private float eliteChitin = 15f;

        [Tooltip("Один из скольких рядовых убитых роняет хитин.")]
        [SerializeField, Range(1, 500)] private int chitinOneIn = 30;

        [Header("Цвет")]
        [SerializeField] private Color ichorColor = new Color(1f, 0.62f, 0.12f);
        [SerializeField] private Color chitinColor = new Color(0.45f, 0.85f, 0.75f);
        [SerializeField] private Color cocoonColor = new Color(1f, 0.3f, 0.6f);

        private NativeArray<IchorDrop> _drops;
        private NativeArray<float> _collected;
        private NativeArray<int> _alive;

        // Поле-заглушка на одну непроходимую клетку: пока сетка толпы перестраивается, у задачи
        // должны быть настоящие массивы — пустые система безопасности не пропустит.
        private NativeArray<byte> _noWalkable;
        private NativeArray<float3> _noVectors;
        private NativeArray<float> _noDepth;
        private NativeArray<ushort> _noDistance;

        private SpiderCrowd _boundCrowd;
        private CatacombWorld _boundWorld;
        private Transform _player;
        private CharacterController _playerBody;

        private Mesh _mesh;
        private Material _material;
        private readonly List<Vector3> _vertices = new List<Vector3>(Capacity * 4);
        private readonly List<Color> _colors = new List<Color>(Capacity * 4);
        private readonly List<Vector2> _corners = new List<Vector2>(Capacity * 4);
        private readonly List<Vector2> _sizes = new List<Vector2>(Capacity * 4);
        private readonly List<int> _indices = new List<int>(Capacity * 6);

        private int _cursor;
        private uint _seed = 1;

        /// <summary>Радиус магнита, юниты — из статов забега.</summary>
        public float MagnetRadius { get; set; } = PlayerStats.BaseMagnet;

        /// <summary>
        /// Множитель ценности ихора и шанса хитина: уровень качества (M5) режет население, и без
        /// компенсации телефонный игрок рос бы медленнее компьютерного.
        /// </summary>
        public float ValueScale { get; set; } = 1f;

        /// <summary>Множитель хитина — глубина вылазки (M4).</summary>
        public float ChitinScale { get; set; } = 1f;

        public int Live { get; private set; }

        /// <summary>Наибольшее число живых капель за уровень — для прогона (потолок 600).</summary>
        public int PeakLive { get; private set; }

        /// <summary>Подобрано: вид, сумма ценности, штук. Раз в кадр на вид.</summary>
        public event Action<DropKind, float, int> Collected;

        /// <summary>Капель создано и слито за жизнь — для прогона: слияние работает.</summary>
        public int Spawned { get; private set; }
        public int MergedTotal { get; private set; }

        /// <summary>Сколько миллисекунд занял последний шаг — для отчёта цены.</summary>
        public double LastStepMs { get; private set; }

        private void OnEnable()
        {
            EnsureLinks();
            Allocate();
        }

        private void OnDisable()
        {
            Unbind();
            Release();
        }

        /// <summary>Ссылки и подписки. Отдельно от OnEnable — прогоны живут в редакторе (грабли №17).</summary>
        public void EnsureLinks()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindAnyObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindAnyObjectByType<SpiderCrowd>();

            if (_player == null)
            {
                var rig = FindAnyObjectByType<CatacombTestRig>();

                if (rig != null)
                {
                    _player = rig.transform;
                    _playerBody = rig.GetComponent<CharacterController>();
                }
            }

            if (crowd != _boundCrowd)
            {
                if (_boundCrowd != null) _boundCrowd.Killed -= OnKilled;

                _boundCrowd = crowd;
                if (crowd != null) crowd.Killed += OnKilled;
            }

            if (world != _boundWorld)
            {
                if (_boundWorld != null) _boundWorld.Generated -= Clear;

                _boundWorld = world;
                if (world != null) world.Generated += Clear;
            }

            Allocate();
        }

        private void Unbind()
        {
            if (_boundCrowd != null) _boundCrowd.Killed -= OnKilled;
            if (_boundWorld != null) _boundWorld.Generated -= Clear;

            _boundCrowd = null;
            _boundWorld = null;
        }

        private void Allocate()
        {
            if (_drops.IsCreated) return;

            _drops = new NativeArray<IchorDrop>(Capacity, Allocator.Persistent);
            _collected = new NativeArray<float>(6, Allocator.Persistent);
            _alive = new NativeArray<int>(1, Allocator.Persistent);

            _noWalkable = new NativeArray<byte>(1, Allocator.Persistent);
            _noVectors = new NativeArray<float3>(1, Allocator.Persistent);
            _noDepth = new NativeArray<float>(1, Allocator.Persistent);
            _noDistance = new NativeArray<ushort>(1, Allocator.Persistent);
        }

        private void Release()
        {
            if (_drops.IsCreated) _drops.Dispose();
            if (_collected.IsCreated) _collected.Dispose();
            if (_alive.IsCreated) _alive.Dispose();
            if (_noWalkable.IsCreated) _noWalkable.Dispose();
            if (_noVectors.IsCreated) _noVectors.Dispose();
            if (_noDepth.IsCreated) _noDepth.Dispose();
            if (_noDistance.IsCreated) _noDistance.Dispose();

            if (_mesh != null) DestroyNow(_mesh);
            if (_material != null) DestroyNow(_material);
        }

        private static void DestroyNow(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        /// <summary>Новый уровень — старые капли висели бы в пустоте.</summary>
        public void Clear()
        {
            if (!_drops.IsCreated) return;

            for (var i = 0; i < _drops.Length; i++) _drops[i] = default;

            Live = 0;
            PeakLive = 0;
        }

        // ------------------------------------------------------------------ появление

        private void OnKilled(IReadOnlyList<KillRecord> kills)
        {
            for (var i = 0; i < kills.Count; i++)
            {
                var kill = kills[i];

                if (kill.Elite)
                {
                    Drop(kill.Position, DropKind.Ichor, eliteIchor * xpPerHit * ValueScale);
                    Drop(kill.Position + Vector3.up * 0.4f, DropKind.Chitin, eliteChitin * ChitinScale);
                    Drop(kill.Position + Vector3.up * 0.2f, DropKind.Cocoon, 1f);
                    continue;
                }

                Drop(kill.Position, DropKind.Ichor, Mathf.Max(1, kill.KindHealth) * xpPerHit * ValueScale);

                if (NextRandom() * chitinOneIn < ValueScale) Drop(kill.Position, DropKind.Chitin, ChitinScale);
            }
        }

        /// <summary>Положить каплю. Рядом с живой того же вида — слить с ней (кроме коконов).</summary>
        public void Drop(Vector3 worldPoint, DropKind kind, float value)
        {
            if (!_drops.IsCreated || value <= 0f || world == null) return;

            var local = (float3)world.transform.InverseTransformPoint(worldPoint);
            var mergeSq = mergeRadius * mergeRadius;

            var nearest = -1;
            var nearestSq = float.MaxValue;
            var free = -1;

            for (var i = 0; i < _drops.Length; i++)
            {
                var drop = _drops[i];

                if (drop.Alive == 0)
                {
                    if (free < 0) free = i;
                    continue;
                }

                if (drop.Kind != (int)kind || kind == DropKind.Cocoon) continue;

                var distanceSq = math.lengthsq(drop.Position - local);
                if (distanceSq >= nearestSq) continue;

                nearest = i;
                nearestSq = distanceSq;
            }

            if (nearest >= 0 && (nearestSq <= mergeSq || free < 0))
            {
                var drop = _drops[nearest];
                drop.Value += value;
                drop.Merged += 1f;
                _drops[nearest] = drop;

                MergedTotal++;
                return;
            }

            if (free < 0) return;

            _drops[free] = new IchorDrop
            {
                Position = local,
                Normal = new float3(0f, 1f, 0f),
                Value = value,
                Seed = NextRandom(),
                Kind = (int)kind,
                Alive = 1
            };

            Spawned++;
            Live++;
        }

        /// <summary>Подтянуть капли у точки — молния «Индукция»: лежащие сразу ползут.</summary>
        public void Pull(Vector3 worldPoint, float radius)
        {
            if (!_drops.IsCreated || world == null) return;

            var local = (float3)world.transform.InverseTransformPoint(worldPoint);
            var radiusSq = radius * radius;

            for (var i = 0; i < _drops.Length; i++)
            {
                var drop = _drops[i];
                if (drop.Alive == 0 || math.lengthsq(drop.Position - local) > radiusSq) continue;

                drop.Age = math.max(drop.Age, lieSeconds + 1.5f);
                _drops[i] = drop;
            }
        }

        /// <summary>Сколько ценности лежит на уровне по виду — для прогона.</summary>
        public float Pending(DropKind kind)
        {
            if (!_drops.IsCreated) return 0f;

            var sum = 0f;
            for (var i = 0; i < _drops.Length; i++)
            {
                if (_drops[i].Alive != 0 && _drops[i].Kind == (int)kind) sum += _drops[i].Value;
            }

            return sum;
        }

        private float NextRandom()
        {
            _seed ^= _seed << 13;
            _seed ^= _seed >> 17;
            _seed ^= _seed << 5;

            return (_seed & 0xFFFFFF) / (float)0x1000000;
        }

        // ------------------------------------------------------------------ шаг

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;

            Draw();
        }

        /// <summary>Шаг капель. Отдельно от Update — прогон гоняет сам.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || !_drops.IsCreated || world == null) return;

            EnsureLinks();

            var clock = System.Diagnostics.Stopwatch.StartNew();

            for (var i = 0; i < _collected.Length; i++) _collected[i] = 0f;

            var hasField = crowd != null && crowd.HasField;
            var player = PlayerBodyLocal();

            new IchorJob
            {
                Drops = _drops,
                Field = hasField ? crowd.Field.Sampler : NoField,
                FieldValid = hasField ? 1 : 0,
                Player = player,
                DeltaTime = deltaTime,
                LieSeconds = lieSeconds,
                CrawlFrom = crawlFrom,
                CrawlTo = crawlTo,
                CrawlRamp = crawlRamp,
                MagnetRadius = MagnetRadius,
                MagnetSpeed = magnetSpeed,
                PickupRadius = pickupRadius,
                Collected = _collected,
                Alive = _alive
            }.Schedule().Complete();

            Live = _alive[0];
            PeakLive = Mathf.Max(PeakLive, Live);
            LastStepMs = clock.Elapsed.TotalMilliseconds;

            for (var kind = 0; kind < (int)DropKind.Count; kind++)
            {
                var count = (int)_collected[kind + 3];
                if (count > 0) Collected?.Invoke((DropKind)kind, _collected[kind], count);
            }
        }

        private SpiderFlowField.FlowSampler NoField => new SpiderFlowField.FlowSampler
        {
            Walkable = _noWalkable,
            Normal = _noVectors,
            Depth = _noDepth,
            Flow = _noVectors,
            Distance = _noDistance,
            Dim = new int3(1, 1, 1),
            CellSize = 1f
        };

        private float3 PlayerBodyLocal()
        {
            if (_player == null) return new float3(0f, -10000f, 0f);

            var centre = _playerBody != null ? _player.position + _player.rotation * _playerBody.center : _player.position;
            return world.transform.InverseTransformPoint(centre);
        }

        // ------------------------------------------------------------------ отрисовка

        private void Draw()
        {
            if (!_drops.IsCreated || world == null) return;

            if (_material == null)
            {
                if (shader == null) shader = Shader.Find("Mine Generator/Cave Ichor");
                if (shader == null) return;

                _material = new Material(shader) { name = "Ichor", hideFlags = HideFlags.DontSave };
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "Ichor Drops", hideFlags = HideFlags.DontSave };
                _mesh.MarkDynamic();
            }

            _vertices.Clear();
            _colors.Clear();
            _corners.Clear();
            _sizes.Clear();
            _indices.Clear();

            var toWorld = world.transform.localToWorldMatrix;
            var time = Time.time;

            for (var i = 0; i < _drops.Length; i++)
            {
                var drop = _drops[i];
                if (drop.Alive == 0) continue;

                var centre = toWorld.MultiplyPoint3x4(drop.Position + drop.Normal * 0.15f);

                Color color;
                float size;

                switch ((DropKind)drop.Kind)
                {
                    case DropKind.Chitin:
                        color = chitinColor;
                        size = 0.35f + Mathf.Min(0.4f, drop.Value * 0.02f);
                        break;

                    case DropKind.Cocoon:
                        // Кокон пульсирует — его надо найти глазами в толпе.
                        color = cocoonColor * (0.8f + 0.4f * Mathf.Sin(time * 4f));
                        size = 1.1f;
                        break;

                    default:
                        color = ichorColor;
                        size = 0.3f + Mathf.Min(0.5f, Mathf.Sqrt(drop.Value) * 0.06f);
                        break;
                }

                var start = _vertices.Count;

                for (var k = 0; k < 4; k++)
                {
                    _vertices.Add(centre);
                    _colors.Add(color);
                    _sizes.Add(new Vector2(size, drop.Seed));
                }

                _corners.Add(new Vector2(-1f, -1f));
                _corners.Add(new Vector2(1f, -1f));
                _corners.Add(new Vector2(1f, 1f));
                _corners.Add(new Vector2(-1f, 1f));

                _indices.Add(start);
                _indices.Add(start + 1);
                _indices.Add(start + 2);
                _indices.Add(start);
                _indices.Add(start + 2);
                _indices.Add(start + 3);
            }

            _mesh.Clear();

            if (_vertices.Count == 0) return;

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _corners);
            _mesh.SetUVs(1, _sizes);
            _mesh.SetTriangles(_indices, 0, false);

            var bounds = new Bounds(world.transform.position, Vector3.one * 2000f);
            _mesh.bounds = bounds;

            var parameters = new RenderParams(_material)
            {
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = bounds
            };

            Graphics.RenderMesh(parameters, _mesh, 0, Matrix4x4.identity);
        }
    }
}
