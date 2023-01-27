using Unity.Burst;
using UnityEngine;

namespace MineGenerator
{
    [BurstCompile]
    public static class Interpolation
    {
        public static Vector3 Interpolate(Vector3 edgeVertex1, float valueAtVertex1,
            Vector3 edgeVertex2, float valueAtVertex2, float isoLevel)
        {
            return edgeVertex1 +
                   InterpolateFloat(isoLevel,valueAtVertex1, valueAtVertex2) * (edgeVertex2 - edgeVertex1);
        }
        
        [BurstCompile]
        public static float InterpolateFloat(float isoLevel, float densityA, float densityB)
        {
            return (isoLevel - densityA) / (densityB - densityA);
        }
    }
}