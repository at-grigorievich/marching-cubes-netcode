using MineGenerator.Data;
using UnityEngine;

namespace MineGenerator.Containers
{
    public class PointsContainer
    {
        private readonly PointData[,,] _points;
        private readonly Vector3[] _pointsPositions;

        private readonly GridData _gridData;
        private readonly NoiseData _noiseData;

        public PointData this[int x, int y, int z] => _points[x, y, z];
        
        public Vector3 LeftBottomBottomPoint => _points[0, 0, 0].Position;
        public Vector3 RightTopBottomPoint => _points[_gridData.GridSize - 1, 0, _gridData.GridSize - 1].Position;
        public Vector3 LeftBottomTopPoint => _points[0, _gridData.GridSize - 1, 0].Position;

        public float DeltaStep => _gridData.DeltaStep;
        public int GridSize => _gridData.GridSize;

        public float NoiseScale => _noiseData.NoiseScale;
        public float NoiseAmplitude => _noiseData.NoiseAmplitude;
        
        public PointsContainer(GridData gridData, NoiseData noiseData)
        {
            _gridData = gridData;
            _noiseData = noiseData;
            
            var gridSize = _gridData.GridSize;
            var deltaStep = _gridData.DeltaStep;
            
            _points = new PointData[gridSize, gridSize, gridSize];
            _pointsPositions = new Vector3[gridSize * gridSize * gridSize];

            int count = 0;
            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        var pos = new Vector3(x*deltaStep, y*deltaStep, z*deltaStep);

                        _points[x, y, z] = new PointData(pos, 0f);
                        _pointsPositions[count] = pos;
                        
                        count++;
                    }
                }
            }
        }

        public void RecalculateWeight(Vector3 center, float radius)
        {
            for (int x = 0; x < GridSize; x++)
            {
                for (int y = 0; y < GridSize; y++)
                {
                    for (int z = 0; z < GridSize; z++)
                    {
                        Vector3 pointPos = _points[x, y, z].Position;
                        
                        float distance = (center.x - pointPos.x) * (center.x - pointPos.x) +
                                         (center.y - pointPos.y) * (center.y - pointPos.y) + 
                                         (center.z - pointPos.z) * (center.z - pointPos.z);
                        
                        var onRadius = distance <= radius;
                        var onSecRadius = distance <= radius * 2f;

                        var noiseX = (float) x / GridSize * NoiseScale;
                        var noiseY = (float) y / GridSize * NoiseScale;
                        var noiseZ = (float) z / GridSize * NoiseScale;
                        
                        float noise = Noise.PerlinNoise3D(noiseX, noiseY, noiseZ) * NoiseAmplitude;
                        
                        if (onRadius)
                        {
                            _points[x, y, z].Density = 1f;
                            _points[x, y, z].IsLocked = true;
                        }
                        else if(onSecRadius)
                        {
                            var delta = Mathf.Exp(-distance);
                            _points[x, y, z].Density += delta + noise;
                        }
                    }
                }
            }
        }

        public bool IsPointInContainer(Vector3 point)
        {
            var inX = point.x >= LeftBottomBottomPoint.x && point.x <= RightTopBottomPoint.x;
            var inY = point.y >= LeftBottomBottomPoint.y && point.y <= LeftBottomTopPoint.y;
            var inZ = point.z >= LeftBottomBottomPoint.z && point.z <= RightTopBottomPoint.z;

            return inX && inY && inZ;
        }
    }
}