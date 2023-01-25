using System;
using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace MineGenerator.Containers
{
    [Serializable]
    public class MeshContainer
    {
        [SerializeField] private MeshFilter meshFilter;
        [SerializeField,HideInInspector] private Mesh mesh;

        [SerializeField,HideInInspector] private GridData gridData;
        [SerializeField,HideInInspector] private float isoLevel;

        [SerializeField,HideInInspector] private MeshCollider _collider;
        
        public MeshContainer(MeshFilter meshFilter, MeshCollider collider, GridData gridData, float isoLevel)
        {
            this.meshFilter = meshFilter;
            _collider = collider;

            mesh = new Mesh { name = "mesh"};

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
            
            mesh.Clear();
            
            JobHandle updateMeshHandle = updateMeshJob.Schedule();
            updateMeshHandle.Complete();
            
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.normals = normals.ToArray();
            
            vertices.Dispose();
            triangles.Dispose();
            points.Dispose();
            cubeValues.Dispose();
            normals.Dispose();
            
            //mesh.RecalculateNormals();
            //mesh.Optimize();
            
            //TODO: Продумать как будет изменяться меш в рантайме
            meshFilter.sharedMesh = mesh;
            _collider.sharedMesh = mesh;
        }

        public void SaveMeshAsset(string path,string name)
        {
            if(mesh.vertices.Length <= 0) return;
            var sharedMesh = meshFilter.sharedMesh;
            
            sharedMesh.name = name;
            AssetDatabase.AddObjectToAsset(sharedMesh,path);
        }
    }
}