using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct CreatePointsJob: IJob
    {
        [WriteOnly] public NativeArray<PointData> Data;

        [ReadOnly] public int GridSize;
        [ReadOnly] public float DeltaStep;
        [ReadOnly] public Vector3 WorldDelta;

        [ReadOnly] public Vector2 CornersX;
        [ReadOnly] public Vector2 CornersY;
        [ReadOnly] public Vector2 CornersZ;

        public void Execute()
        {
            int index = 0;
            
            for (int x = 0; x < GridSize; x++)
            {
                for (int y = 0; y < GridSize; y++)
                {
                    for (int z = 0; z < GridSize; z++)
                    {
                        var pos = new Vector3(x*DeltaStep, y*DeltaStep, z*DeltaStep);
                        pos += WorldDelta;

                        bool isCorner = IsPointCorner(pos);
                        Data[index] = new PointData(pos, 0f, isCorner);
                        
                        index++;
                    }
                }
            }
        }

        private bool IsPointCorner(Vector3 point)
        {
            var tolerance = DeltaStep * 0.5f;

            return IsOnBorder(point.x, CornersX, tolerance)
                   || IsOnBorder(point.y, CornersY, tolerance)
                   || IsOnBorder(point.z, CornersZ, tolerance);
        }

        private static bool IsOnBorder(float value, Vector2 corners, float tolerance) =>
            value <= corners[0] + tolerance || value >= corners[1] - tolerance;
    }
}