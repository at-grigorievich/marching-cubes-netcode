using System.Collections.Generic;
using System.Linq;
using MineGenerator.Data;
using NaughtyBezierCurves;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    public class ChunkBezierGenerator: MonoBehaviour
    {
        public const string MineChunksTag = "Mine-Behaviour";
        
        [SerializeField] private BezierCurve3D[] curves;
        
        [SerializeField] private MineBezierChunk chunkPrefab;
        [SerializeField] private ChunkData chunkData;

        [SerializeField] private Vector3 chunkCounts;
        
        private HashSet<MineBezierChunk> _chunks;

        [ContextMenu("Generate Chunks")]
        public void CreateChunks()
        {
            _chunks = new HashSet<MineBezierChunk>();

            var curvesPoints = GetAllBezierCurvesPoints();

            CreateChunksGrid();
            GenerateChunks(curvesPoints);
            ClearEmptyChunks();
            SetupChunksGrid();
        }

        private void CreateChunksGrid()
        {
            var step = chunkData.GridParameters.DeltaStep * chunkData.GridParameters.GridSize;

            for (int x = 0; x < chunkCounts.x; x++)
            {
                for (int y = 0; y < chunkCounts.y; y++)
                {
                    for (int z = 0; z < chunkCounts.z; z++)
                    {
                        var xyz = new Vector3(x, y, z);
                        var position = xyz * step - xyz * chunkData.GridParameters.DeltaStep;
                        var instance = CreateChunk(position);

                        _chunks.Add(instance);
                    }
                }
            }
        }

        private void SetupChunksGrid()
        {
            var mineObject = new GameObject();
            mineObject.name = $"{MineChunksTag}-{Mathf.Abs(mineObject.GetInstanceID())}";
            var mineBehaviour = mineObject.AddComponent<MineBehaviour>();

            mineBehaviour.AddMineChunks(_chunks.ToArray());
        }
        
        private void GenerateChunks(Vector3[] curvesPoints)
        {
            foreach (var mineBezierChunk in _chunks)
            {
                mineBezierChunk.GenerateChunk(curvesPoints);
            }
        }
        private void ClearEmptyChunks()
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.IsEmptyChunk)
                {
                    DestroyImmediate(chunk.gameObject);
                }
            }

            _chunks.RemoveWhere(c => c == null);
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
    }
}