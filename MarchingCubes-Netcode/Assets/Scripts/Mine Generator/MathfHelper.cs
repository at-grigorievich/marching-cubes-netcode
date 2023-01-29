using Unity.Burst;
using UnityEngine;

namespace MineGenerator
{
    [BurstCompile]
    public static class MathfHelper
    {
        public static bool IsPointInRadius(Vector3 point, Vector3 center, float Radius)
        {
            float x = point.x - center.x;
            float y = point.y - center.y;
            float z = point.z - center.z;

            float distance = Mathf.Sqrt(x * x + y * y + z * z) - Radius;

            return distance <= 0f;
        }
        
        public static bool IsPointInRadius(Vector3 point, Vector3 center, float Radius, out float distance)
        {
            float x = point.x - center.x;
            float y = point.y - center.y;
            float z = point.z - center.z;

            distance = Mathf.Sqrt(x * x + y * y + z * z);

            return distance - Radius <= 0f;
        }

        public static bool IsPointInCube(Vector3 point, Vector3 center, float radius)
        {
            Vector3 leftBottom = center - new Vector3(1f, 1f, 1f) * radius;
            Vector3 rightTop = center + new Vector3(1f, 1f, 1f) * radius;

            bool isX = point.x >= leftBottom.x && point.x <= rightTop.x;
            bool isY = point.y >= leftBottom.y && point.y <= rightTop.y;
            bool isZ = point.z >= leftBottom.z && point.z <= rightTop.z;

            return isX && isY && isZ;
        }
    }
}