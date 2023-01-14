#nullable enable

using MineGenerator.Containers;
using MineGenerator.Data;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(MeshCollider))]
    public class MineBezierChunk : MonoBehaviour
    {
        [SerializeField] private ChunkData chunkData = null!;
        [SerializeField] private MeshFilter meshFilter = null!;
        
        [SerializeField, HideInInspector] private PointsContainer? pointsContainer;
        [SerializeField, HideInInspector] private MeshContainer? meshContainer;

        private MeshCollider _meshCollider;
        
        public bool IsEmptyChunk => pointsContainer?.IsEmptyChunk ?? true;
        
        public void GenerateChunk(Vector3[] curvesPoints)
        {
            CreateMeshContainer();
            CreatePointsContainer();
            
            pointsContainer!.GeneratePointsByBezier(curvesPoints,chunkData.TunnelParameters);
            meshContainer!.UpdateMesh(pointsContainer.PointsArray);

            _meshCollider = GetComponent<MeshCollider>();
            _meshCollider.sharedMesh = meshFilter.sharedMesh;
        }

        public void SaveMeshChunk(string path,string pathName) => meshContainer?.SaveMeshAsset(path,pathName);
        
        private void CreateMeshContainer()
        {
            meshContainer = new MeshContainer(meshFilter,chunkData.GridParameters,chunkData.IsoLevel);
        }
        private void CreatePointsContainer()
        {
            var worldDelta = transform.position;
            pointsContainer = new PointsContainer(chunkData.GridParameters, chunkData.NoiseParameters, worldDelta);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if(pointsContainer == null) return;
            if(pointsContainer.PointsArray.Length <= 0) return;
            
            var gridSize = pointsContainer.GridSize;
            
            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        if(!pointsContainer[x,y,z].IsAvailable) continue;
                        
                        Gizmos.color = pointsContainer[x,y,z].Density >= chunkData.IsoLevel
                            ? Color.white : Color.black;
                        
                        Gizmos.DrawCube(pointsContainer[x, y, z].Position,Vector3.one*.1f);
                    }  
                }
            }
        }
#endif
    }
}