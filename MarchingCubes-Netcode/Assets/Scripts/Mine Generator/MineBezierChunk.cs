#nullable enable

using MineGenerator.Containers;
using MineGenerator.Data;
using NaughtyBezierCurves;
using UnityEngine;

namespace MineGenerator
{
    public class MineBezierChunk : MonoBehaviour
    {
        [SerializeField] private ChunkData chunkData = null!;
        [SerializeField] private MeshFilter meshFilter = null!;
        
        private PointsContainer? _pointsContainer;
        private MeshContainer? _meshContainer;

        public void GenerateChunk(ref float lengthPassed, BezierCurve3D bezier)
        {
            CreateMeshContainer();
            CreatePointsContainer();

            var addValue = _pointsContainer!.DeltaStep / bezier.GetApproximateLength();
            var chunkLength = lengthPassed;
            
            var point = bezier.GetPoint(chunkLength);
            
            while (IsPointInChunk(point) && chunkLength <= 1f)
            {
                _pointsContainer.RecalculateWeight(point, chunkData.TunnelParameters.RadiusWithError);

                chunkLength += addValue;
                point = bezier.GetPoint(chunkLength);
            }
            
            March();

            lengthPassed += chunkLength;
        }

        public bool IsPointInChunk(Vector3 point) => 
            _pointsContainer != null && _pointsContainer.IsPointInContainer(point);


        private void March()
        {
            var gridSize = _pointsContainer!.GridSize;
            var deltaStep = _pointsContainer!.DeltaStep;
            var isoLevel = chunkData.IsoLevel;
            
            for (int x = 0; x < gridSize - 1; x++)
            {
                for (int y = 0; y < gridSize - 1; y++)
                {
                    for (int z = 0; z < gridSize - 1; z++)
                    {
                        float[] cubeValues = 
                        {
                            _pointsContainer[x, y, z + 1].Density,
                            _pointsContainer[x + 1, y, z + 1].Density,
                            _pointsContainer[x + 1, y, z].Density,
                            _pointsContainer[x, y, z].Density,
                            _pointsContainer[x, y + 1, z + 1].Density,
                            _pointsContainer[x + 1, y + 1, z + 1].Density,
                            _pointsContainer[x + 1, y + 1, z].Density,
                            _pointsContainer[x, y + 1, z].Density
                        };
                        
                        Vector3 worldPos = new Vector3(x, y, z)*deltaStep + transform.position;
                        
                        int cubeIndex = 0;
                        if (cubeValues[0] < isoLevel) cubeIndex |= 1;
                        if (cubeValues[1] < isoLevel) cubeIndex |= 2;
                        if (cubeValues[2] < isoLevel) cubeIndex |= 4;
                        if (cubeValues[3] < isoLevel) cubeIndex |= 8;
                        if (cubeValues[4] < isoLevel) cubeIndex |= 16;
                        if (cubeValues[5] < isoLevel) cubeIndex |= 32;
                        if (cubeValues[6] < isoLevel) cubeIndex |= 64;
                        if (cubeValues[7] < isoLevel) cubeIndex |= 128;
                        
                        int[] edges = MarchingCubesTables.triTable[cubeIndex];
                        int oldCount = _meshContainer!.TrianglesCount;
                        
                        for (int i = 0; edges[i] != -1; i += 3) {
                            int e00 = MarchingCubesTables.edgeConnections[edges[i]][0];
                            int e01 = MarchingCubesTables.edgeConnections[edges[i]][1];

                            int e10 = MarchingCubesTables.edgeConnections[edges[i + 1]][0];
                            int e11 = MarchingCubesTables.edgeConnections[edges[i + 1]][1];

                            int e20 = MarchingCubesTables.edgeConnections[edges[i + 2]][0];
                            int e21 = MarchingCubesTables.edgeConnections[edges[i + 2]][1];

                            Vector3 a = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e00] * deltaStep, cubeValues[e00],
                                MarchingCubesTables.cubeCorners[e01] * deltaStep, cubeValues[e01], isoLevel) + worldPos;
                            
                            Vector3 b = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e10] * deltaStep, cubeValues[e10], 
                                MarchingCubesTables.cubeCorners[e11] * deltaStep, cubeValues[e11], isoLevel) + worldPos;
                            
                            Vector3 c = Interpolation.Interpolate(MarchingCubesTables.cubeCorners[e20] * deltaStep, cubeValues[e20],
                                MarchingCubesTables.cubeCorners[e21] * deltaStep, cubeValues[e21], isoLevel) + worldPos;

                            _meshContainer!.AddTriangle(c, b, a);
                        }
                        _meshContainer!.UpdateMesh(oldCount);
                    }
                }
            }
        }

        private void CreateMeshContainer()
        {
            if (_meshContainer != null)
            {
                _meshContainer.ClearMesh();
                return;
            }
            _meshContainer = new MeshContainer(meshFilter);
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
            var deltaStep = _pointsContainer.DeltaStep;
            
            float centerValue = ((gridSize-1) * deltaStep) / 2f;
            Vector2 center = Vector2.one * centerValue;

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