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
        public NativeList<int> Triangles;
        public NativeList<Vector3> Normals;

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

                            Vector3 a = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e00] * DeltaStep,
                                CubeValues[e00].Density,
                                MarchingCubesTables.cubeCorners[e01] * DeltaStep, CubeValues[e01].Density, IsoLevel) + worldPos;

                            Vector3 b = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e10] * DeltaStep,
                                CubeValues[e10].Density,
                                MarchingCubesTables.cubeCorners[e11] * DeltaStep, CubeValues[e11].Density, IsoLevel) + worldPos;

                            Vector3 c = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e20] * DeltaStep,
                                CubeValues[e20].Density,
                                MarchingCubesTables.cubeCorners[e21] * DeltaStep, CubeValues[e21].Density, IsoLevel) + worldPos;
                            
                            AddNormal(CubeValues[e00].Position,CubeValues[e00].Density,CubeValues[e01].Position,CubeValues[e01].Density);
                            AddNormal(CubeValues[e10].Position,CubeValues[e10].Density,CubeValues[e11].Position,CubeValues[e11].Density);
                            AddNormal(CubeValues[e20].Position,CubeValues[e20].Density,CubeValues[e21].Position,CubeValues[e21].Density);
                            
                            AddVerticesAndTriangles(a, b, c);
                        }
                    }
                }
            }
        }

        private void AddNormal(Vector3 a, float densityA, Vector3 b, float densityB)
        {
            var normalA = a.normalized;
            var normalB = b.normalized;

            float t = Interpolation.InterpolateFloat(IsoLevel, densityA, densityB);

            var normal = (normalA + t * (normalB - normalA)).normalized;
            
            Normals.Add(normal);
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