using System.Collections.Generic;
using MineGenerator.Data;
using NaughtyBezierCurves;
using UnityEngine;

namespace MineGenerator
{
    public class ChunkBezierGenerator: MonoBehaviour
    {
        [SerializeField] private BezierCurve3D[] curves;
        
        [SerializeField] private MineBezierChunk chunkPrefab;
        [SerializeField] private ChunkData chunkData;

        [SerializeField] private Vector3 chunkCounts;
        
        private List<MineBezierChunk> _chunks;

        [ContextMenu("Generate Chunks")]
        public void GenerateChunks()
        {
            _chunks = new List<MineBezierChunk>();
            GenerateChunksGrid();
            
            foreach (var mineBezierChunk in _chunks)
            {
                mineBezierChunk.GenerateChunk(curves);
            }
        }

        private void GenerateChunksGrid()
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
        
        private MineBezierChunk CreateChunk(Vector3 spawnPosition)
        {
            var instance = Instantiate(chunkPrefab);
            instance.transform.position = spawnPosition;

            return instance;
        }
    }
}