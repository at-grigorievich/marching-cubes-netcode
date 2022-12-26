using System.Collections.Generic;
using System.Linq;
using NaughtyBezierCurves;
using UnityEngine;

namespace MineGenerator
{
    public class ChunkBezierGenerator: MonoBehaviour
    {
        [SerializeField] private BezierCurve3D[] curves;
        [SerializeField] private MineBezierChunk chunkPrefab;
        
        private HashSet<MineBezierChunk> _chunks;

        [ContextMenu("Generate Chunks")]
        public void GenerateChunks()
        {
            _chunks = new HashSet<MineBezierChunk>();
            foreach (var curve in curves)
            {
                CreateChunksByBezier(curve);
            }
        }

        private void CreateChunksByBezier(BezierCurve3D curve)
        {
            var passedLength = 0f;

            while (passedLength < 1f)
            {
                var startPoint = curve.GetPoint(passedLength);
                var chunk = FindOrCreateChunk(startPoint);
                
                chunk.GenerateChunk(ref passedLength,curve);
            }
        }

        private MineBezierChunk FindOrCreateChunk(Vector3 startBezierPoint)
        {
            if (_chunks.Count <= 0)
            {
                return CreateChunk(startBezierPoint);
            }
            var selected = 
                    _chunks.FirstOrDefault(chunk => chunk.IsPointInChunk(startBezierPoint));
            
            return selected == null ? CreateChunk(startBezierPoint) : selected;
        }

        private MineBezierChunk CreateChunk(Vector3 spawnPosition)
        {
            var instance = Instantiate(chunkPrefab);
            _chunks.Add(instance);
                
            return instance;
        }
    }
}