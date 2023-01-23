using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct MarchMeshUpdateJob : IJob
    {
        [ReadOnly] public NativeArray<PointData> Points;
        
        public NativeList<Vector3> Vertices;
        public NativeList<Vector3> Normals;
        
        public NativeList<int> Triangles;

        public NativeArray<PointData> CubeValues;
        
        [ReadOnly] public int GridSize;
        [ReadOnly] public float DeltaStep;
        [ReadOnly] public float IsoLevel;

        public void Execute()
        {
            var gridSize = GridSize - 1;
            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        CubeValues[0] = Points[x * GridSize * GridSize + y * GridSize + (z + 1)];
                        CubeValues[1] = Points[(x + 1) * GridSize * GridSize + y * GridSize + (z + 1)];
                        CubeValues[2] = Points[(x + 1) * GridSize * GridSize + y * GridSize + z];
                        CubeValues[3] = Points[x * GridSize * GridSize + y * GridSize + z];
                        CubeValues[4] = Points[x * GridSize * GridSize + (y + 1) * GridSize + (z + 1)];
                        CubeValues[5] = Points[(x + 1) * GridSize * GridSize + (y + 1) * GridSize + (z + 1)];
                        CubeValues[6] = Points[(x + 1) * GridSize * GridSize + (y + 1) * GridSize + z];
                        CubeValues[7] = Points[x * GridSize * GridSize + (y + 1) * GridSize + z];

                        var worldPos = new Vector3(x, y, z) * DeltaStep;

                        int cubeIndex = 0;
                        if (CubeValues[0].Density < IsoLevel) cubeIndex |= 1;
                        if (CubeValues[1].Density < IsoLevel) cubeIndex |= 2;
                        if (CubeValues[2].Density < IsoLevel) cubeIndex |= 4;
                        if (CubeValues[3].Density < IsoLevel) cubeIndex |= 8;
                        if (CubeValues[4].Density < IsoLevel) cubeIndex |= 16;
                        if (CubeValues[5].Density < IsoLevel) cubeIndex |= 32;
                        if (CubeValues[6].Density < IsoLevel) cubeIndex |= 64;
                        if (CubeValues[7].Density < IsoLevel) cubeIndex |= 128;

                        int[] edges = MarchingCubesTables.triTable[cubeIndex];

                        for (int i = 0; edges[i] != -1; i += 3)
                        {
                            int e00 = MarchingCubesTables.edgeConnections[edges[i]][0];
                            int e01 = MarchingCubesTables.edgeConnections[edges[i]][1];

                            int e10 = MarchingCubesTables.edgeConnections[edges[i + 1]][0];
                            int e11 = MarchingCubesTables.edgeConnections[edges[i + 1]][1];

                            int e20 = MarchingCubesTables.edgeConnections[edges[i + 2]][0];
                            int e21 = MarchingCubesTables.edgeConnections[edges[i + 2]][1];


                            Vector3 a = InterpolateVertex(e00, e01, worldPos);
                            Vector3 b = InterpolateVertex(e10, e11, worldPos);
                            Vector3 c = InterpolateVertex(e20, e21, worldPos);

                            AddNormal(CubeValues[e00],CubeValues[e01]);
                            AddNormal(CubeValues[e10],CubeValues[e11]);
                            AddNormal(CubeValues[e20],CubeValues[e21]);
                            
                            AddVerticesAndTriangles(a, b, c);
                        }
                    }
                }
            }
        }

        private void AddNormal(PointData pointA, PointData pointB)
        {
            var normalA = pointA.Position.normalized;
            var normalB = pointB.Position.normalized;

            float t = Interpolation.InterpolateFloat(IsoLevel, pointA.Density, pointB.Density);

            var normal = (normalA + t * (normalB - normalA)).normalized;
            
            Normals.Add(normal);
        }

        private Vector3 InterpolateVertex(int e0, int e1, Vector3 worldPos)
        {
            return Interpolation.Interpolate(
                MarchingCubesTables.cubeCorners[e0] * DeltaStep, 
                CubeValues[e0].Density,
                MarchingCubesTables.cubeCorners[e1] * DeltaStep, 
                CubeValues[e1].Density, 
                IsoLevel) + worldPos;
        }
        
        private void AddVerticesAndTriangles(Vector3 a, Vector3 b, Vector3 c)
        {
            int triIndex = Triangles.Length;
            
            Vertices.Add(a);
            Vertices.Add(b);
            Vertices.Add(c);

            Triangles.Add(triIndex+2);
            Triangles.Add(triIndex + 1);
            Triangles.Add(triIndex);
        }
    }
}