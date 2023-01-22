#nullable enable

using System;
using MineGenerator.Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Serialization;

namespace MineGenerator.Containers
{
    [Serializable]
    public class PointsContainer
    {
        [SerializeField,HideInInspector] private PointData[] points = null!;

        [SerializeField,HideInInspector] private GridData gridData;
        [SerializeField,HideInInspector] private NoiseData noiseData;

        [SerializeField,HideInInspector] private Vector3 worldDelta;
        
        public PointData this[int x, int y, int z] => points[x*GridSize*GridSize+y*GridSize + z];
        public PointData[] PointsArray => points;

        public Vector3 LeftBottomBottomPoint => this[0,0,0].Position;
        public Vector3 RightTopBottomPoint => this[gridData.GridSize - 1, 0, gridData.GridSize - 1].Position;
        public Vector3 LeftBottomTopPoint => this[0, gridData.GridSize - 1, 0].Position;

        public float DeltaStep => gridData.DeltaStep;
        public int GridSize => gridData.GridSize;

        public bool IsEmptyChunk
        {
            get
            {
                if (GridSize == 0) return true;
                
                for (int x = 0; x < GridSize; x++)
                {
                    for (int y = 0; y < GridSize; y++)
                    {
                        for (int z = 0; z < GridSize; z++)
                        {
                            if (this[x,y,z].IsAvailable)
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

        public void GeneratePointsByBezier(Vector3[] curvesPoints, MineTunnelData tunnelData)
        {
            int pointsCount = GridSize * GridSize * GridSize;
            var pointData = new NativeArray<PointData>(pointsCount,Allocator.TempJob);
            var curvePoints = new NativeArray<Vector3>(curvesPoints, Allocator.TempJob);

            var createPointsJob = new CreatePointsJob()
            {
                Data = pointData,
                GridSize = GridSize,
                DeltaStep = DeltaStep,
                WorldDelta = worldDelta,
            };

            var setupPointsJob = new SetupPointWeightJob()
            {
                CurvePoints = curvePoints,
                Data = pointData,

                GridSize = this.GridSize,
                NoiseScale = noiseData!.NoiseScale,
                NoiseAmplitude = noiseData!.NoiseAmplitude,

                Radius = tunnelData.Radius,
                RadiusWithError = tunnelData.RadiusWithError,
                SecondRadius = tunnelData.SecondRadius
            };

            JobHandle createPointsHandle = createPointsJob.Schedule();
            JobHandle setupPointsHandle = 
                setupPointsJob.Schedule(pointsCount, 0, createPointsHandle);
            
            createPointsHandle.Complete();
            setupPointsHandle.Complete();

            points = pointData.ToArray();
            
            curvePoints.Dispose();
            pointData.Dispose();
        }
    }
}