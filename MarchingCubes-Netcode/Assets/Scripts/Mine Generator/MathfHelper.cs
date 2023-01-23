using Unity.Burst;
using UnityEngine;

namespace MineGenerator
{
    public static class MathfHelper
    {
        [BurstCompile]
        public static bool IsPointInRadius(Vector3 point, Vector3 center, float Radius)
        {
            float x = point.x - center.x;
            float y = point.y - center.y;
            float z = point.z - center.z;

            float distance = Mathf.Sqrt(x * x + y * y + z * z) - Radius;

            return distance <= 0f;
        }
    }
}