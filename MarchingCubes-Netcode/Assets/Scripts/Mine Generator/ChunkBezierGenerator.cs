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
#if UNITY_EDITOR
        public const string MineChunksTag = "Mine-Behaviour";
        
        [SerializeField] private BezierCurve3D[] curves;
        
        [SerializeField] private MineBezierChunk chunkPrefab;

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
            var step = ChunkData.instance.DeltaStep * ChunkData.instance.GridParameters.GridSize;

            for (int x = 0; x < ChunkData.instance.ChunksCount.x; x++)
            {
                for (int y = 0; y < ChunkData.instance.ChunksCount.y; y++)
                {
                    for (int z = 0; z < ChunkData.instance.ChunksCount.z; z++)
                    {
                        var xyz = new Vector3(x, y, z);
                        var position = xyz * step - xyz * ChunkData.instance.DeltaStep;
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
            
            var rb = mineObject.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;

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
                var addValue = ChunkData.instance.DeltaStep / bezier.GetApproximateLength();
                
                for (float curveLength = 0f; curveLength <= 1f; curveLength += addValue)
                {
                    var point = bezier.GetPoint(curveLength);
                    points.Add(point);
                }
            }

            return points.ToArray();
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawWireCube(ChunkData.instance.ChunksCenter,ChunkData.instance.ChunksSize);
        }
#endif
    }
}