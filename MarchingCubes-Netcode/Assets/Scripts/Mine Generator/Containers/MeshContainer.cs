using System;
using MineGenerator.Core;
using MineGenerator.Data;
using Unity.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MineGenerator.Containers
{
    [Serializable]
    public class MeshContainer
    {
        [SerializeField] private MeshFilter meshFilter;
        [SerializeField, HideInInspector] private MeshCollider collider;

        private Mesh _mesh;

        // Буферы полигонизации одни на всё редактирование: раньше каждый мазок кистью
        // создавал новые NativeList и новый Mesh, а старый меш никто не удалял.
        private static ChunkMeshBuilder _sharedBuilder;
        private static int _sharedCells = -1;
        private static bool _cleanupRegistered;

        public MeshContainer(MeshFilter meshFilter, MeshCollider collider)
        {
            this.meshFilter = meshFilter;
            this.collider = collider;
        }

        public void UpdateMesh(PointData[] pointsArr)
        {
            var gridSize = ChunkData.instance.GridSize;
            var expected = gridSize * gridSize * gridSize;

            if (pointsArr == null || pointsArr.Length < expected || meshFilter == null) return;

            var density = new NativeArray<float>(expected, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < expected; i++) density[i] = pointsArr[i].Density;

            var builder = GetBuilder(gridSize - 1);

            builder.Schedule(density, gridSize, 0, ChunkData.instance.DeltaStep, ChunkData.instance.IsoLevel)
                .Complete();

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "chunk" };
                _mesh.MarkDynamic();
            }

            builder.Apply(_mesh);
            density.Dispose();

            // meshFilter.mesh отдаёт копию и плодит по мешу на вызов — нужен sharedMesh.
            meshFilter.sharedMesh = _mesh;

            if (collider == null) return;

            collider.sharedMesh = null;
            if (builder.IndexCount > 0) collider.sharedMesh = _mesh;
        }

        public void SaveMeshAsset(string path, string name)
        {
#if UNITY_EDITOR
            var source = _mesh != null ? _mesh : meshFilter != null ? meshFilter.sharedMesh : null;
            if (source == null || source.vertexCount <= 0) return;

            var sharedMesh = UnityEngine.Object.Instantiate(source);
            sharedMesh.name = name;

            AssetDatabase.AddObjectToAsset(sharedMesh, path);

            meshFilter.sharedMesh = sharedMesh;
            if (collider != null) collider.sharedMesh = sharedMesh;
#endif
        }

        private static ChunkMeshBuilder GetBuilder(int cells)
        {
            if (_sharedBuilder != null && _sharedCells == cells) return _sharedBuilder;

            _sharedBuilder?.Dispose();
            _sharedBuilder = new ChunkMeshBuilder(cells);
            _sharedCells = cells;

            RegisterCleanup();
            return _sharedBuilder;
        }

        private static void DisposeBuilder()
        {
            _sharedBuilder?.Dispose();
            _sharedBuilder = null;
            _sharedCells = -1;
        }

        private static void RegisterCleanup()
        {
            if (_cleanupRegistered) return;
            _cleanupRegistered = true;

            Application.quitting += DisposeBuilder;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += DisposeBuilder;
#endif
        }
    }
}
