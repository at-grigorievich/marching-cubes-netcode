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

        public NativeArray<float> CubeValues;
        
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
                        CubeValues[0] = Points[x * GridSize * GridSize + y * GridSize + (z + 1)].Density;
                        CubeValues[1] = Points[(x + 1) * GridSize * GridSize + y * GridSize + (z + 1)].Density;
                        CubeValues[2] = Points[(x + 1) * GridSize * GridSize + y * GridSize + z].Density;
                        CubeValues[3] = Points[x * GridSize * GridSize + y * GridSize + z].Density;
                        CubeValues[4] = Points[x * GridSize * GridSize + (y + 1) * GridSize + (z + 1)].Density;
                        CubeValues[5] = Points[(x + 1) * GridSize * GridSize + (y + 1) * GridSize + (z + 1)].Density;
                        CubeValues[6] = Points[(x + 1) * GridSize * GridSize + (y + 1) * GridSize + z].Density;
                        CubeValues[7] = Points[x * GridSize * GridSize + (y + 1) * GridSize + z].Density;

                        var worldPos = new Vector3(x, y, z) * DeltaStep;

                        int cubeIndex = 0;
                        if (CubeValues[0] < IsoLevel) cubeIndex |= 1;
                        if (CubeValues[1] < IsoLevel) cubeIndex |= 2;
                        if (CubeValues[2] < IsoLevel) cubeIndex |= 4;
                        if (CubeValues[3] < IsoLevel) cubeIndex |= 8;
                        if (CubeValues[4] < IsoLevel) cubeIndex |= 16;
                        if (CubeValues[5] < IsoLevel) cubeIndex |= 32;
                        if (CubeValues[6] < IsoLevel) cubeIndex |= 64;
                        if (CubeValues[7] < IsoLevel) cubeIndex |= 128;

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
                                CubeValues[e00],
                                MarchingCubesTables.cubeCorners[e01] * DeltaStep, CubeValues[e01], IsoLevel) + worldPos;

                            Vector3 b = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e10] * DeltaStep,
                                CubeValues[e10],
                                MarchingCubesTables.cubeCorners[e11] * DeltaStep, CubeValues[e11], IsoLevel) + worldPos;

                            Vector3 c = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e20] * DeltaStep,
                                CubeValues[e20],
                                MarchingCubesTables.cubeCorners[e21] * DeltaStep, CubeValues[e21], IsoLevel) + worldPos;

                            AddVerticesAndTriangles(c, b, a);
                        }
                    }
                }
            }
        }

        private void AddVerticesAndTriangles(Vector3 a, Vector3 b, Vector3 c)
        {
            int triIndex = Triangles.Length;
            
            Vertices.Add(a);
            Vertices.Add(b);
            Vertices.Add(c);
            
            Triangles.Add(triIndex);
            Triangles.Add(triIndex + 1);
            Triangles.Add(triIndex + 2);
        }
    }
}