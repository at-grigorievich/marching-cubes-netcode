using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    public class MeshContainer
    {
        private readonly MeshFilter _meshFilter;
        private readonly Mesh _mesh;

        private readonly GridData _gridData;
        private readonly float _isoLevel;
        
        public MeshContainer(MeshFilter meshFilter, GridData gridData, float isoLevel)
        {
            _meshFilter = meshFilter;
            _mesh = new Mesh();

            _gridData = gridData;
            _isoLevel = isoLevel;
        }

        public void UpdateMesh(PointData[] pointsArr)
        {
            var points = new NativeArray<PointData>(pointsArr, Allocator.TempJob);
            var vertices = new NativeList<Vector3>(Allocator.TempJob);
            var triangles = new NativeList<int>(Allocator.TempJob);
            var cubeValues = new NativeArray<float>(8,Allocator.TempJob);

            var updateMeshJob = new MarchMeshUpdateJob()
            {
                Points = points,
                Vertices = vertices,
                Triangles = triangles,

                CubeValues = cubeValues,
                
                GridSize = _gridData.GridSize,
                DeltaStep = _gridData.DeltaStep,
                IsoLevel = _isoLevel
            };

            JobHandle updateMeshHandle = updateMeshJob.Schedule();
            updateMeshHandle.Complete();
            
            _mesh.SetVertices(vertices.ToArray());
            _mesh.SetTriangles(triangles.ToArray(),0);

            vertices.Dispose();
            triangles.Dispose();
            points.Dispose();
            cubeValues.Dispose();
            
            _mesh.RecalculateNormals();
            _meshFilter.mesh = _mesh;
        }
        
        public void ClearMesh()
        {
            _mesh.Clear();
            _meshFilter.mesh = _mesh;
        }
    }
}