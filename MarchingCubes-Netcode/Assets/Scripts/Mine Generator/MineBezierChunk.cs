#nullable enable

using MineGenerator.Containers;
using MineGenerator.Data;
using UnityEngine;

namespace MineGenerator
{
    public class MineBezierChunk : MonoBehaviour
    {
        [SerializeField] private ChunkData chunkData = null!;
        [SerializeField] private MeshFilter meshFilter = null!;
        
        private PointsContainer? _pointsContainer;
        private MeshContainer? _meshContainer;

        public bool IsEmptyChunk => _pointsContainer?.IsEmptyChunk ?? true;
        
        public void GenerateChunk(Vector3[] curvesPoints)
        {
            CreateMeshContainer();
            CreatePointsContainer();
            
            _pointsContainer!.GeneratePointsByBezier(curvesPoints, chunkData.TunnelParameters.RadiusWithError);
            _meshContainer!.UpdateMesh(_pointsContainer.PointsArray);
        }

        private void OnDisable()
        {
            _pointsContainer?.Dispose();
        }
        
        private void CreateMeshContainer()
        {
            if (_meshContainer != null)
            {
                _meshContainer.ClearMesh();
                return;
            }
            _meshContainer = new MeshContainer(meshFilter,chunkData.GridParameters,chunkData.IsoLevel);
        }
        private void CreatePointsContainer()
        {
            var worldDelta = transform.position;
            _pointsContainer = new PointsContainer(chunkData.GridParameters, chunkData.NoiseParameters, worldDelta);
        }
        
        private void OnDrawGizmosSelected()
        {
            if(_pointsContainer == null) return;

            var gridSize = _pointsContainer.GridSize;

            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        Gizmos.color = _pointsContainer[x, y, z].Density >= chunkData.IsoLevel 
                            ? Color.white : Color.black;
                        
                        Gizmos.DrawCube(_pointsContainer[x, y, z].Position,Vector3.one*.1f);
                    }  
                }
            }
        }
    }
}