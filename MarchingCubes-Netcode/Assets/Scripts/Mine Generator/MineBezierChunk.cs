using Mine_Generator.Data;
using MineGenerator.Containers;
using MineGenerator.Data;
using MineGenerator.Interfaces;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(MeshCollider))]
    public class MineBezierChunk : MonoBehaviour, IWeightEditable
    {
        [SerializeField] private MeshFilter meshFilter;
        
        [SerializeField, HideInInspector] private PointsContainer pointsContainer;
        [SerializeField, HideInInspector] private MeshContainer meshContainer;

        public bool IsEmptyChunk => pointsContainer?.IsEmptyChunk ?? true;
        
#if  UNITY_EDITOR
        public void GenerateChunk(Vector3[] curvesPoints)
        {
            CreateMeshContainer();
            CreatePointsContainer();
            
            pointsContainer.GeneratePointsByBezier(curvesPoints);
            meshContainer.UpdateMesh(pointsContainer.PointsArray);
        }
        public void SaveMeshChunk(string path,string pathName) => meshContainer?.SaveMeshAsset(path,pathName);
#endif       
        
        public void UpdateWeight(WeightModifyData modifyData)
        {
            pointsContainer!.UpdateWeight(modifyData);
            meshContainer!.UpdateMesh(pointsContainer.PointsArray);
        }
        
        private void CreateMeshContainer()
        {
            var meshCollider = GetComponent<MeshCollider>();
            meshContainer = new MeshContainer(meshFilter,meshCollider);
        }
        private void CreatePointsContainer()
        {
            var worldDelta = transform.position;
            pointsContainer = new PointsContainer(worldDelta);
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
                        
                        Gizmos.color = pointsContainer[x,y,z].Density >= ChunkData.instance.IsoLevel
                            ? Color.white : Color.black;

                        if (pointsContainer[x, y, z].IsCorner)
                        {
                            Gizmos.color = Color.red;
                        }
                        
                        Gizmos.DrawCube(pointsContainer[x, y, z].Position,Vector3.one*.1f);
                    }  
                }
            }
        }
#endif
    }
}