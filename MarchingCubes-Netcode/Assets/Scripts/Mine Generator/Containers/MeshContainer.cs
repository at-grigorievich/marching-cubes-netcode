using System;
using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace MineGenerator.Containers
{
    [Serializable]
    public class MeshContainer
    {
        [SerializeField] private MeshFilter meshFilter;

        [SerializeField,HideInInspector] private GridData gridData;
        [SerializeField,HideInInspector] private float isoLevel;

        [SerializeField,HideInInspector] private MeshCollider collider;
        
        private Mesh _mesh;
        
        public MeshContainer(MeshFilter meshFilter, MeshCollider collider, GridData gridData, float isoLevel)
        {
            this.meshFilter = meshFilter;
            this.collider = collider;
            
            this.gridData = gridData;
            this.isoLevel = isoLevel;
        }

        public void UpdateMesh(PointData[] pointsArr)
        {
            var points = new NativeArray<PointData>(pointsArr, Allocator.TempJob);
            
            var vertices = new NativeList<Vector3>(Allocator.TempJob);
            var normals = new NativeList<Vector3>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            
            var cubeValues = new NativeArray<PointData>(8,Allocator.TempJob);

            var updateMeshJob = new MarchMeshUpdateJob()
            {
                Points = points,
                Vertices = vertices,
                Triangles = triangles,
                Normals = normals,
                
                CubeValues = cubeValues,
                
                GridSize = gridData!.GridSize,
                DeltaStep = gridData!.DeltaStep,
                IsoLevel = isoLevel
            };
            
            JobHandle updateMeshHandle = updateMeshJob.Schedule();
            updateMeshHandle.Complete();
            
            _mesh = new Mesh
            {
                name = "mesh",
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray(),
                normals = normals.ToArray()
            };

            vertices.Dispose();
            triangles.Dispose();
            points.Dispose();
            cubeValues.Dispose();
            normals.Dispose();
            
            //mesh.RecalculateNormals();
            //mesh.Optimize();
            
            meshFilter.mesh = _mesh;
            collider.sharedMesh = _mesh;
        }

        public void SaveMeshAsset(string path,string name)
        {
#if UNITY_EDITOR
            if(_mesh.vertices.Length <= 0) return;
            
            var sharedMesh = new Mesh
            {
                name = name,
                vertices = _mesh.vertices,
                triangles = _mesh.triangles,
                normals = _mesh.normals
            };
            
            AssetDatabase.AddObjectToAsset(sharedMesh,path);
            
            meshFilter.sharedMesh = sharedMesh;
            collider.sharedMesh = sharedMesh;
#endif
        }
    }
}