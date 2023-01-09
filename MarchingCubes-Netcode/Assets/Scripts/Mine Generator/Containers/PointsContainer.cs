using System;
using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    public class PointsContainer: IDisposable
    {
        private NativeArray<PointData> _points;

        private readonly GridData _gridData;
        private readonly NoiseData _noiseData;

        private readonly Vector3 _worldDelta;
        
        public PointData this[int x, int y, int z] => _points[x*GridSize*GridSize+y*GridSize + z];
        public PointData[] PointsArray => _points.ToArray();

        public Vector3 LeftBottomBottomPoint => this[0,0,0].Position;
        public Vector3 RightTopBottomPoint => this[_gridData.GridSize - 1, 0, _gridData.GridSize - 1].Position;
        public Vector3 LeftBottomTopPoint => this[0, _gridData.GridSize - 1, 0].Position;

        public float DeltaStep => _gridData.DeltaStep;
        public int GridSize => _gridData.GridSize;

        public bool IsEmptyChunk
        {
            get
            {
                var gridSize = _gridData.GridSize;
                for (int x = 0; x < gridSize; x++)
                {
                    for (int y = 0; y < gridSize; y++)
                    {
                        for (int z = 0; z < gridSize; z++)
                        {
                            if (this[x,y,z].Density > Mathf.Epsilon)
                                return false;
                        }
                    }
                }
                return true;
            }
        }
        
        public PointsContainer(GridData gridData, NoiseData noiseData, Vector3 worldDelta)
        {
            _gridData = gridData;
            _noiseData = noiseData;
            _worldDelta = worldDelta;
        }

        public void GeneratePointsByBezier(Vector3[] curvesPoints, float radius)
        {
            int pointsCount = GridSize * GridSize * GridSize;
            _points = new NativeArray<PointData>(pointsCount,Allocator.Persistent);
            var curvePoints = new NativeArray<Vector3>(curvesPoints, Allocator.TempJob);

            var createPointsJob = new CreatePointsJob()
            {
                Data = _points,
                GridSize = GridSize,
                DeltaStep = DeltaStep,
                WorldDelta = _worldDelta
            };

            var setupPointsJob = new SetupPointWeightJob()
            {
                CurvePoints = curvePoints,
                Data = _points,

                GridSize = this.GridSize,
                NoiseScale = _noiseData.NoiseScale,
                NoiseAmplitude = _noiseData.NoiseAmplitude,

                Radius = radius
            };

            JobHandle createPointsHandle = createPointsJob.Schedule();
            JobHandle setupPointsHandle = 
                setupPointsJob.Schedule(pointsCount, 0, createPointsHandle);
            
            createPointsHandle.Complete();
            setupPointsHandle.Complete();

            curvePoints.Dispose();
        }
        
        public void Dispose()
        {
            _points.Dispose();
        }
    }
}