using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Один чанк катакомб: поле плотности в нативной памяти плюс объекты рендера и физики.
    ///
    /// Плотности намеренно не сериализуются. Старый <c>PointData[]</c> хранил в каждой точке
    /// ещё и мировую позицию с двумя флагами — 20 байт против 4, и всё это уезжало в префаб
    /// и сцену. Здесь позиция выводится из индекса, а поле пересоздаётся из сида.
    /// </summary>
    public sealed class CatacombChunk : IDisposable
    {
        public readonly int3 Coord;

        /// <summary>Угол ячейки (0,0,0) в координатах генерации (локальных для мира).</summary>
        public readonly Vector3 Origin;

        /// <summary>Объём, который чанк реально полигонизирует.</summary>
        public readonly Bounds CellBounds;

        /// <summary>Объём вместе с кольцом запаса — по нему отбираются примитивы и правки плотности.</summary>
        public readonly Bounds SampleBounds;

        public readonly GameObject GameObject;
        public readonly MeshFilter Filter;
        public readonly MeshRenderer Renderer;
        public readonly MeshCollider Collider;

        public readonly Mesh Mesh;

        public NativeArray<float> Density;

        public bool HasDensity => Density.IsCreated;
        public bool NeedsRemesh;

        public CatacombChunk(int3 coord, Transform parent, CatacombSettings settings, bool editorPreview)
        {
            Coord = coord;

            var chunkSize = settings.ChunkWorldSize;
            Origin = new Vector3(coord.x * chunkSize, coord.y * chunkSize, coord.z * chunkSize);

            var half = Vector3.one * (chunkSize * 0.5f);
            CellBounds = new Bounds(Origin + half, Vector3.one * chunkSize);

            var padWorld = CatacombSettings.SamplePadding * settings.VoxelSize;
            SampleBounds = new Bounds(CellBounds.center, CellBounds.size + Vector3.one * (padWorld * 2f));

            GameObject = new GameObject($"Chunk {coord.x}_{coord.y}_{coord.z}");
            GameObject.transform.SetParent(parent, false);
            GameObject.transform.localPosition = Origin;

            // HideAndDontSave, а не просто DontSave: помимо того, что чанки не должны
            // попадать в сцену (раньше сгенерированная шахта раздувала .unity на мегабайты),
            // они не должны попадать и в систему undo. Сотни временных детей регистрировать
            // в undo бессмысленно, а незарегистрированные повисают при Ctrl+Z и заваливают
            // консоль предупреждениями про dangling child.
            if (editorPreview) GameObject.hideFlags = HideFlags.HideAndDontSave;

            Mesh = new Mesh { name = GameObject.name };
            Mesh.MarkDynamic();

            Filter = GameObject.AddComponent<MeshFilter>();
            Filter.sharedMesh = Mesh;

            Renderer = GameObject.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = settings.Material;

            if (settings.ColliderMode != ColliderMode.None)
            {
                Collider = GameObject.AddComponent<MeshCollider>();
            }

            Density = new NativeArray<float>(settings.SampleCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        public void ApplyMeshResult(bool isEmpty)
        {
            Renderer.enabled = !isEmpty;

            if (Collider == null) return;

            // Присвоение того же меша не перезапекает коллайдер, поэтому сначала сбрасываем.
            Collider.sharedMesh = null;
            if (!isEmpty) Collider.sharedMesh = Mesh;
        }

        public void Dispose()
        {
            if (Density.IsCreated) Density.Dispose();

            if (Mesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(Mesh);
                else UnityEngine.Object.DestroyImmediate(Mesh);
            }

            if (GameObject == null) return;

            if (Application.isPlaying) UnityEngine.Object.Destroy(GameObject);
            else UnityEngine.Object.DestroyImmediate(GameObject);
        }
    }
}
