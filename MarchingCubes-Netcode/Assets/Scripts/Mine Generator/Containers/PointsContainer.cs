#nullable enable

using System;
using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    [Serializable]
    public class PointsContainer
    {
        [SerializeField,HideInInspector] private PointData[] _points = null!;

        [SerializeField,HideInInspector] private GridData gridData = null!;
        [SerializeField,HideInInspector] private NoiseData noiseData = null!;

        [SerializeField,HideInInspector] private Vector3 worldDelta;
        
        public PointData this[int x, int y, int z] => _points[x*GridSize*GridSize+y*GridSize + z];
        public PointData[] PointsArray => _points;

        public Vector3 LeftBottomBottomPoint => this[0,0,0].Position;
        public Vector3 RightTopBottomPoint => this[gridData!.GridSize - 1, 0, gridData.GridSize - 1].Position;
        public Vector3 LeftBottomTopPoint => this[0, gridData!.GridSize - 1, 0].Position;

        public float DeltaStep => gridData?.DeltaStep ?? 0f;
        public int GridSize => gridData?.GridSize ?? 0;

        public bool IsEmptyChunk
        {
            get
            {
                var gridSize = GridSize;

                if (gridSize == 0) return true;
                
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
            this.gridData = gridData;
            this.noiseData = noiseData;
            this.worldDelta = worldDelta;
        }

        public void GeneratePointsByBezier(Vector3[] curvesPoints, float radius)
        {
            int pointsCount = GridSize * GridSize * GridSize;
            var points = new NativeArray<PointData>(pointsCount,Allocator.TempJob);
            var curvePoints = new NativeArray<Vector3>(curvesPoints, Allocator.TempJob);

            var createPointsJob = new CreatePointsJob()
            {
                Data = points,
                GridSize = GridSize,
                DeltaStep = DeltaStep,
                WorldDelta = worldDelta
            };

            var setupPointsJob = new SetupPointWeightJob()
            {
                CurvePoints = curvePoints,
                Data = points,

                GridSize = this.GridSize,
                NoiseScale = noiseData!.NoiseScale,
                NoiseAmplitude = noiseData!.NoiseAmplitude,

                Radius = radius
            };

            JobHandle createPointsHandle = createPointsJob.Schedule();
            JobHandle setupPointsHandle = 
                setupPointsJob.Schedule(pointsCount, 0, createPointsHandle);
            
            createPointsHandle.Complete();
            setupPointsHandle.Complete();

            _points = points.ToArray();
            
            curvePoints.Dispose();
            points.Dispose();
        }
    }
}