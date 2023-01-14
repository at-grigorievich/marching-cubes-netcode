#nullable enable

using System.Collections.Generic;
using System.Linq;
using MineGenerator.Data;
using MineGenerator.Interfaces;
using NaughtyBezierCurves;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    public class ChunkBezierGenerator: MonoBehaviour
    {
        public const string MineChunksTag = "Mine-Behaviour";
        
        [SerializeField] private BezierCurve3D[] curves = null!;
        
        [SerializeField] private MineBezierChunk chunkPrefab = null!;
        [SerializeField] private ChunkData chunkData = null!;

        [ContextMenu("Generate Chunks")]
        public void CreateChunks()
        {
            var chunks = new HashSet<MineBezierChunk>();

            var curvesPoints = GetAllBezierCurvesPoints();

            CreateChunksGrid(ref chunks);
            GenerateChunks(ref chunks,curvesPoints);
            ClearEmptyChunks(ref chunks);
            SetupChunksGrid(ref chunks);
        }

        private void CreateChunksGrid(ref HashSet<MineBezierChunk> chunks)
        {
            var step = chunkData.GridParameters.DeltaStep * chunkData.GridParameters.GridSize;

            for (int x = 0; x < chunkData.GridParameters.ChunksCount.x; x++)
            {
                for (int y = 0; y < chunkData.GridParameters.ChunksCount.y; y++)
                {
                    for (int z = 0; z < chunkData.GridParameters.ChunksCount.z; z++)
                    {
                        var xyz = new Vector3(x, y, z);
                        var position = xyz * step - xyz * chunkData.GridParameters.DeltaStep;
                        var instance = CreateChunk(position);

                        chunks.Add(instance);
                    }
                }
            }
        }

        private void SetupChunksGrid(ref HashSet<MineBezierChunk> chunks)
        {
            var mineObject = new GameObject();
            mineObject.name = $"{MineChunksTag}-{Mathf.Abs(mineObject.GetInstanceID())}";
            IMineChunksCollectable mineBehaviour = mineObject.AddComponent<MineBehaviour>();

            mineBehaviour.AddMineChunks(chunks.ToArray());
        }
        
        private void GenerateChunks(ref HashSet<MineBezierChunk> chunks, Vector3[] curvesPoints)
        {
            foreach (var mineBezierChunk in chunks)
            {
                mineBezierChunk.GenerateChunk(curvesPoints);
            }
        }
        private void ClearEmptyChunks(ref HashSet<MineBezierChunk> chunks)
        {
            foreach (var chunk in chunks)
            {
                if (chunk.IsEmptyChunk)
                {
                    DestroyImmediate(chunk.gameObject);
                }
            }

            chunks.RemoveWhere(c => c == null);
        }
        
        private MineBezierChunk CreateChunk(Vector3 spawnPosition)
        {
            var instance = Instantiate(chunkPrefab);
            instance.transform.position = spawnPosition;

            return instance;
        }

        private Vector3[] GetAllBezierCurvesPoints()
        {
            var points = new List<Vector3>();
            
            foreach (var bezier in curves)
            {
                var addValue = chunkData.GridParameters.DeltaStep / bezier.GetApproximateLength();
                
                for (float curveLength = 0f; curveLength <= 1f; curveLength += addValue)
                {
                    var point = bezier.GetPoint(curveLength);
                    points.Add(point);
                }
            }

            return points.ToArray();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            var gridSize = chunkData.GridParameters.GridSize;
            var deltaStep = chunkData.GridParameters.DeltaStep;

            var chunkStep = gridSize * deltaStep;

            Gizmos.color = Color.blue;
            for (int x = 0; x < chunkData.GridParameters.ChunksCount.x; x++)
            {
                for (int y = 0; y < chunkData.GridParameters.ChunksCount.y; y++)
                {
                    for (int z = 0; z < chunkData.GridParameters.ChunksCount.z; z++)
                    {
                        var chunkPosition = new Vector3(x * chunkStep, y * chunkStep, z * chunkStep);
                        var chunkSize = new Vector3(chunkStep, chunkStep, chunkStep);
                        Gizmos.DrawWireCube(chunkPosition, chunkSize);
                    }
                }
            }
        }
#endif
    }
}