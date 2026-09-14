using Unity.Collections;
using UnityEngine;

namespace MineGenerator.Core
{
    /// <summary>
    /// Таблицы Marching Cubes, разложенные в плоские <see cref="NativeArray{T}"/>.
    ///
    /// Исходные таблицы (<see cref="MarchingCubesTables"/>) — это managed <c>int[][]</c>.
    /// Burst не умеет обращаться к managed-статике, поэтому джоб, который читал их напрямую,
    /// на самом деле никогда не компилировался в Burst и всё время исполнялся на Mono.
    /// Здесь таблицы один раз копируются в нативную память и передаются в джоб полем.
    /// </summary>
    public static class MarchingCubesLookup
    {
        public const int TriTableStride = 16;

        private static NativeArray<sbyte> _triTable;
        private static NativeArray<int> _edgeCornerA;
        private static NativeArray<int> _edgeCornerB;
        private static bool _created;

        public static NativeArray<sbyte> TriTable
        {
            get
            {
                EnsureCreated();
                return _triTable;
            }
        }

        public static NativeArray<int> EdgeCornerA
        {
            get
            {
                EnsureCreated();
                return _edgeCornerA;
            }
        }

        public static NativeArray<int> EdgeCornerB
        {
            get
            {
                EnsureCreated();
                return _edgeCornerB;
            }
        }

        private static void EnsureCreated()
        {
            if (_created && _triTable.IsCreated) return;

            var tri = MarchingCubesTables.triTable;
            _triTable = new NativeArray<sbyte>(tri.Length * TriTableStride, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            for (var cube = 0; cube < tri.Length; cube++)
            {
                var row = tri[cube];
                for (var i = 0; i < TriTableStride; i++)
                {
                    _triTable[cube * TriTableStride + i] = (sbyte)(i < row.Length ? row[i] : -1);
                }
            }

            var edges = MarchingCubesTables.edgeConnections;
            _edgeCornerA = new NativeArray<int>(edges.Length, Allocator.Persistent);
            _edgeCornerB = new NativeArray<int>(edges.Length, Allocator.Persistent);

            for (var i = 0; i < edges.Length; i++)
            {
                _edgeCornerA[i] = edges[i][0];
                _edgeCornerB[i] = edges[i][1];
            }

            _created = true;
            RegisterCleanup();
        }

        private static void Dispose()
        {
            if (_triTable.IsCreated) _triTable.Dispose();
            if (_edgeCornerA.IsCreated) _edgeCornerA.Dispose();
            if (_edgeCornerB.IsCreated) _edgeCornerB.Dispose();
            _created = false;
        }

        private static bool _cleanupRegistered;

        private static void RegisterCleanup()
        {
            if (_cleanupRegistered) return;
            _cleanupRegistered = true;

            Application.quitting += Dispose;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Dispose();
            };
#endif
        }
    }
}
