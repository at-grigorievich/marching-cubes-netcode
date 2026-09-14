using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using MineGenerator.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = Unity.Mathematics.Random;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Процедурные многоуровневые катакомбы: планировка -> поля плотности чанков -> меши.
    ///
    /// Генерация разложена по кадрам с бюджетом в миллисекундах, поэтому её видно как
    /// прогресс, а не как фриз. Это же условие обязательно для WebGL: там
    /// <c>webGLThreadsSupport</c> выключен, воркеров нет и любой джоб доезжает на главном потоке.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CatacombWorld : MonoBehaviour
    {
        [SerializeField] private CatacombSettings settings;
        [SerializeField] private bool generateOnStart = true;

        [Header("Отладка")]
        [SerializeField] private bool drawLayoutGizmos = true;

        private readonly List<CatacombChunk> _chunks = new List<CatacombChunk>();
        private readonly List<CatacombChunk> _dirtyChunks = new List<CatacombChunk>();

        private CatacombLayout _layout;
        private ChunkMeshBuilder _builder;

        private NativeList<CaveBox> _boxScratch;

        private Coroutine _routine;
        private int _builderCells = -1;

        public CatacombSettings Settings => settings;
        public CatacombLayout Layout => _layout;
        public IReadOnlyList<CatacombChunk> Chunks => _chunks;

        public bool IsGenerating { get; private set; }
        public float Progress { get; private set; }
        public int CurrentSeed { get; private set; }

        public event Action<float> ProgressChanged;
        public event Action Generated;

        private void Start()
        {
            if (generateOnStart) Generate();
        }

        private void OnDestroy()
        {
            Clear();
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Clear;
#endif
        }

#if UNITY_EDITOR
        private void OnEnable()
        {
            // Поля плотности и буферы мешера живут в нативной памяти, а ссылка на них —
            // в обычном C#-поле. Перезагрузка домена такое поле обнуляет, и без явной
            // очистки Unity рапортует об утечке.
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }
#endif

        private void LateUpdate()
        {
            if (_dirtyChunks.Count > 0) FlushDirtyChunks();
        }

        /// <summary>Запускает генерацию с бюджетом по кадрам (в play mode) или сразу (в редакторе).</summary>
        public void Generate(int? seed = null)
        {
            if (!Application.isPlaying)
            {
                GenerateImmediate(seed);
                return;
            }

            var resolved = ResolveSeed(seed);

            Clear();
            _routine = StartCoroutine(GenerateRoutine(resolved));
        }

        /// <summary>Синхронная генерация целиком — для кнопки в инспекторе и для тестов.</summary>
        public void GenerateImmediate(int? seed = null)
        {
            var resolved = ResolveSeed(seed);

            Clear();

            var steps = GenerateSteps(resolved, false);
            while (steps.MoveNext())
            {
                // Без бюджета итератор не отдаёт управление и отрабатывает за один вызов.
            }
        }

        public void Clear()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }

            IsGenerating = false;

            foreach (var chunk in _chunks) chunk.Dispose();
            _chunks.Clear();
            _dirtyChunks.Clear();

            _layout?.Dispose();
            _layout = null;

            _builder?.Dispose();
            _builder = null;
            _builderCells = -1;

            if (_boxScratch.IsCreated) _boxScratch.Dispose();

            DestroyStrayChildren();
        }

        /// <summary>Выкапывает породу в сфере — кирка, взрыв гранаты.</summary>
        public bool Dig(Vector3 worldPosition, float radius, float strength = 1f) =>
            ModifyDensity(worldPosition, radius, math.abs(strength));

        /// <summary>Наращивает породу в сфере.</summary>
        public bool Fill(Vector3 worldPosition, float radius, float strength = 1f) =>
            ModifyDensity(worldPosition, radius, -math.abs(strength));

        public bool ModifyDensity(Vector3 worldPosition, float radius, float amount)
        {
            if (settings == null || _chunks.Count == 0 || radius <= 0f) return false;

            var local = transform.InverseTransformPoint(worldPosition);

            // Джоб правит сэмплы не до Radius, а до Radius + Softness — у него ранний выход
            // по sdf > softness. Отбор по одному лишь радиусу пропускал чанк, попавший только
            // в эту кайму, и получалось расхождение: сосед обновил общий сэмпл шва, а этот нет.
            // Изнутри-снаружи от этого не переворачивается (в кайме цель всегда ниже изоуровня),
            // но плотность в общем углу расходится, вершина на общем ребре встаёт в двух чанках
            // в разные точки, и шов расползается в щель, сквозь которую виден фон камеры.
            var reach = radius + settings.SurfaceSoftness;
            var reachSq = reach * reach;
            var touched = false;

            foreach (var chunk in _chunks)
            {
                if (!chunk.HasDensity) continue;
                if (chunk.SampleBounds.SqrDistance(local) > reachSq) continue;

                var job = new DensityModifyJob
                {
                    Density = chunk.Density,
                    ChunkOrigin = chunk.Origin,
                    VoxelSize = settings.VoxelSize,
                    Dim = settings.SampleDim,
                    Pad = CatacombSettings.SamplePadding,

                    Center = local,
                    Radius = radius,
                    Softness = settings.SurfaceSoftness,
                    Amount = amount,

                    WorldSize = settings.WorldSize,
                    BoundaryThickness = settings.BoundaryThickness
                };

                job.Schedule(settings.SampleCount, 64).Complete();

                if (!chunk.NeedsRemesh)
                {
                    chunk.NeedsRemesh = true;
                    _dirtyChunks.Add(chunk);
                }

                touched = true;
            }

            if (touched && !Application.isPlaying) FlushDirtyChunks();

            return touched;
        }

        /// <summary>
        /// Плотность в мировой точке, трилинейно по сетке чанка. Больше IsoLevel — пустота.
        ///
        /// Это единственный правильный способ спросить «здесь камень?». Физика на такой
        /// вопрос отвечает неверно: коллайдеры есть только на поверхностях ходов, внутри
        /// сплошной породы их нет вовсе, и <c>Physics.CheckSphere</c> посреди камня честно
        /// рапортует «свободно». На этом в проекте уже обжигались — заливка на физике
        /// утекала сквозь стены.
        /// </summary>
        public bool TrySampleDensity(Vector3 worldPosition, out float density)
        {
            density = 0f;

            if (settings == null || _chunks.Count == 0) return false;

            var local = (float3)transform.InverseTransformPoint(worldPosition);

            var size = settings.WorldSizeInChunks;
            var chunkSize = settings.ChunkWorldSize;

            var coord = math.clamp((int3)math.floor(local / chunkSize), 0,
                new int3(size.x - 1, size.y - 1, size.z - 1));

            // Порядок совпадает с порядком заполнения в GenerateSteps: x снаружи, z внутри.
            var index = (coord.x * size.y + coord.y) * size.z + coord.z;

            if (index < 0 || index >= _chunks.Count) return false;

            var chunk = _chunks[index];
            if (!chunk.HasDensity) return false;

            var dim = settings.SampleDim;
            var g = (local - (float3)chunk.Origin) / settings.VoxelSize + CatacombSettings.SamplePadding;

            var i0 = (int3)math.floor(g);
            var f = math.saturate(g - i0);

            var a = math.clamp(i0, 0, dim - 1);
            var b = math.clamp(i0 + 1, 0, dim - 1);

            var x0 = math.lerp(
                math.lerp(At(chunk, dim, a.x, a.y, a.z), At(chunk, dim, b.x, a.y, a.z), f.x),
                math.lerp(At(chunk, dim, a.x, b.y, a.z), At(chunk, dim, b.x, b.y, a.z), f.x), f.y);

            var x1 = math.lerp(
                math.lerp(At(chunk, dim, a.x, a.y, b.z), At(chunk, dim, b.x, a.y, b.z), f.x),
                math.lerp(At(chunk, dim, a.x, b.y, b.z), At(chunk, dim, b.x, b.y, b.z), f.x), f.y);

            density = math.lerp(x0, x1, f.z);
            return true;
        }

        /// <summary>Внутри ли точка сплошной породы. Соглашение инвертировано: меньше IsoLevel — камень.</summary>
        public bool IsSolid(Vector3 worldPosition) =>
            TrySampleDensity(worldPosition, out var density) && density <= settings.IsoLevel;

        private static float At(CatacombChunk chunk, int dim, int x, int y, int z) =>
            chunk.Density[(x * dim + y) * dim + z];

        public bool TryGetSpawnPoint(out Vector3 worldPosition)
        {
            worldPosition = transform.position;

            if (_layout == null || _layout.SpawnPoints.Count == 0) return false;

            worldPosition = transform.TransformPoint(_layout.SpawnPoints[0]);
            return true;
        }

        /// <summary>Центры залов уровня — точки для спавна волн зомби и лута.</summary>
        public IReadOnlyList<Vector3> GetRoomCenters(int level)
        {
            if (_layout == null || level < 0 || level >= _layout.RoomsByLevel.Count) return Array.Empty<Vector3>();

            var result = new List<Vector3>(_layout.RoomsByLevel[level].Count);
            foreach (var point in _layout.RoomsByLevel[level]) result.Add(transform.TransformPoint(point));

            return result;
        }

        private int ResolveSeed(int? seed)
        {
            if (seed.HasValue) return seed.Value;
            if (settings == null) return 0;

            return settings.RandomizeSeed ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : settings.Seed;
        }

        private IEnumerator GenerateRoutine(int seed)
        {
            var steps = GenerateSteps(seed, true);
            while (steps.MoveNext()) yield return steps.Current;

            _routine = null;
        }

        private IEnumerator GenerateSteps(int seed, bool budgeted)
        {
            if (settings == null)
            {
                Debug.LogError($"{nameof(CatacombWorld)}: не назначен {nameof(CatacombSettings)}.", this);
                yield break;
            }

            IsGenerating = true;
            CurrentSeed = seed;
            SetProgress(0f);

            _layout = CatacombLayout.Build(settings, seed);

            var cells = settings.ChunkResolution;
            if (_builder == null || _builderCells != cells)
            {
                _builder?.Dispose();
                _builder = new ChunkMeshBuilder(cells);
                _builderCells = cells;
            }

            _boxScratch = new NativeList<CaveBox>(256, Allocator.Persistent);

            var size = settings.WorldSizeInChunks;
            var total = math.max(1, settings.ChunkCount);
            var done = 0;

            var editorPreview = !Application.isPlaying;
            var noiseOffset = new Random((uint)math.max(1, math.abs(seed))).NextFloat3(-512f, 512f);

            var stopwatch = Stopwatch.StartNew();

            for (var x = 0; x < size.x; x++)
            for (var y = 0; y < size.y; y++)
            for (var z = 0; z < size.z; z++)
            {
                var chunk = new CatacombChunk(new int3(x, y, z), transform, settings, editorPreview);
                _chunks.Add(chunk);

                BuildDensity(chunk, noiseOffset);
                BuildMesh(chunk);

                done++;
                SetProgress(done / (float)total);

                if (!budgeted || stopwatch.Elapsed.TotalMilliseconds < settings.MillisecondsPerFrame) continue;

                stopwatch.Restart();
                yield return null;
            }

            IsGenerating = false;
            SetProgress(1f);

            Generated?.Invoke();
        }

        private void BuildDensity(CatacombChunk chunk, float3 noiseOffset)
        {
            CullPrimitives(chunk);

            var job = new CatacombDensityJob
            {
                Density = chunk.Density,

                Boxes = _boxScratch.AsArray(),

                ChunkOrigin = chunk.Origin,
                VoxelSize = settings.VoxelSize,
                Dim = settings.SampleDim,
                Pad = CatacombSettings.SamplePadding,

                WorldSize = settings.WorldSize,
                BoundaryThickness = settings.BoundaryThickness,

                NoiseScale = settings.NoiseScale,
                NoiseAmplitude = settings.NoiseAmplitude,
                NoiseOctaves = settings.NoiseOctaves,
                NoiseOffset = noiseOffset,

                SurfaceSoftness = settings.SurfaceSoftness,
                JunctionBlend = settings.JunctionBlend
            };

            job.Schedule(settings.SampleCount, 64).Complete();
        }

        /// <summary>Оставляет только примитивы, дотягивающиеся до чанка: без этого стоимость растёт как все точки на все примитивы.</summary>
        private void CullPrimitives(CatacombChunk chunk)
        {
            _boxScratch.Clear();

            var bounds = chunk.SampleBounds;
            bounds.Expand(settings.PrimitiveMargin * 2f);

            for (var i = 0; i < _layout.Boxes.Length; i++)
            {
                var box = _layout.Boxes[i];
                if (bounds.Intersects(WorldBounds(box))) _boxScratch.Add(box);
            }
        }

        /// <summary>AABB повёрнутого параллелепипеда: проекция полугабаритов на оси мира.</summary>
        private static Bounds WorldBounds(CaveBox box)
        {
            var m = new float3x3(box.Rotation);
            var half = box.HalfExtents;

            var extent = math.abs(m.c0) * half.x + math.abs(m.c1) * half.y + math.abs(m.c2) * half.z;

            return new Bounds((Vector3)box.Center, (Vector3)(extent * 2f));
        }

        private void BuildMesh(CatacombChunk chunk)
        {
            var handle = _builder.Schedule(chunk.Density, settings.SampleDim, CatacombSettings.SamplePadding,
                settings.VoxelSize, settings.IsoLevel);

            handle.Complete();

            _builder.Apply(chunk.Mesh);
            chunk.ApplyMeshResult(_builder.IsEmpty);

            chunk.NeedsRemesh = false;
        }

        private void FlushDirtyChunks()
        {
            var stopwatch = Stopwatch.StartNew();
            var budget = settings != null ? settings.MillisecondsPerFrame : 8f;

            var index = 0;
            while (index < _dirtyChunks.Count)
            {
                BuildMesh(_dirtyChunks[index]);
                index++;

                if (stopwatch.Elapsed.TotalMilliseconds >= budget) break;
            }

            _dirtyChunks.RemoveRange(0, index);
        }

        private void SetProgress(float value)
        {
            Progress = value;
            ProgressChanged?.Invoke(value);
        }

        /// <summary>Подчищает чанки, пережившие смену домена или отменённую генерацию.</summary>
        private void DestroyStrayChildren()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (!child.name.StartsWith("Chunk ", StringComparison.Ordinal)) continue;

                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (settings == null) return;

            var size = settings.WorldSize;
            var center = transform.TransformPoint(size * 0.5f);

            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
            Gizmos.DrawWireCube(center, size);

            if (!drawLayoutGizmos || _layout == null || !_layout.IsCreated) return;

            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.6f);

            for (var i = 0; i < _layout.Boxes.Length; i++)
            {
                var box = _layout.Boxes[i];

                Gizmos.matrix = transform.localToWorldMatrix *
                                Matrix4x4.TRS((Vector3)box.Center, box.Rotation, Vector3.one);

                Gizmos.DrawWireCube(Vector3.zero, (Vector3)(box.HalfExtents * 2f));
            }

            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
