using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Core
{
    /// <summary>
    /// Переиспользуемые буферы для полигонизации одного чанка.
    ///
    /// Один экземпляр живёт всё время работы генератора: нативные списки не пересоздаются
    /// на каждую перестройку меша, а только очищаются. Это важно для WebGL, где каждый
    /// <c>ToArray()</c> старой версии давал managed-мусор на любой удар киркой.
    /// </summary>
    public sealed class ChunkMeshBuilder : IDisposable
    {
        private NativeList<float3> _vertices;
        private NativeList<float3> _normals;
        private NativeList<int> _triangles;
        private NativeArray<int> _edgeVertexIndex;

        private readonly int _cells;

        public int VertexCount => _vertices.IsCreated ? _vertices.Length : 0;
        public int IndexCount => _triangles.IsCreated ? _triangles.Length : 0;
        public bool IsEmpty => IndexCount == 0;

        public ChunkMeshBuilder(int cells)
        {
            _cells = cells;

            // Практический потолок для пещерного поля — порядка 1.5 вершин на ячейку.
            var guess = math.max(64, cells * cells * cells / 2);

            _vertices = new NativeList<float3>(guess, Allocator.Persistent);
            _normals = new NativeList<float3>(guess, Allocator.Persistent);
            _triangles = new NativeList<int>(guess * 3, Allocator.Persistent);
            _edgeVertexIndex = new NativeArray<int>(MarchingCubesJob.EdgeCacheLength(cells), Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        public JobHandle Schedule(NativeArray<float> density, int dim, int pad, float voxelSize, float isoLevel,
            JobHandle dependency = default)
        {
            _vertices.Clear();
            _normals.Clear();
            _triangles.Clear();

            var job = new MarchingCubesJob
            {
                Density = density,
                TriTable = MarchingCubesLookup.TriTable,
                EdgeCornerA = MarchingCubesLookup.EdgeCornerA,
                EdgeCornerB = MarchingCubesLookup.EdgeCornerB,

                Dim = dim,
                Pad = pad,
                VoxelSize = voxelSize,
                IsoLevel = isoLevel,

                Vertices = _vertices,
                Normals = _normals,
                Triangles = _triangles,
                EdgeVertexIndex = _edgeVertexIndex
            };

            return job.Schedule(dependency);
        }

        /// <summary>Заливает результат в существующий меш. Вызывать после Complete() хендла.</summary>
        public void Apply(Mesh mesh)
        {
            mesh.Clear();

            if (_triangles.Length == 0)
            {
                mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
                return;
            }

            // Один чанк 32^3 легко перебирает лимит 16-битных индексов — раньше это молча
            // обрезало геометрию.
            mesh.indexFormat = _vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;

            mesh.SetVertices(_vertices.AsArray().Reinterpret<Vector3>());
            mesh.SetNormals(_normals.AsArray().Reinterpret<Vector3>());
            mesh.SetIndices(_triangles.AsArray(), MeshTopology.Triangles, 0, false);

            mesh.RecalculateBounds();
        }

        public void Dispose()
        {
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_normals.IsCreated) _normals.Dispose();
            if (_triangles.IsCreated) _triangles.Dispose();
            if (_edgeVertexIndex.IsCreated) _edgeVertexIndex.Dispose();
        }
    }
}
