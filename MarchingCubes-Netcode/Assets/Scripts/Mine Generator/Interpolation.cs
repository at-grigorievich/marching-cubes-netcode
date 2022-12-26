using UnityEngine;

namespace MineGenerator
{
    public static class Interpolation
    {
        public static Vector3 Interpolate(Vector3 edgeVertex1, float valueAtVertex1,
            Vector3 edgeVertex2, float valueAtVertex2, float isoLevel)
        {
            return edgeVertex1 +
                   (isoLevel - valueAtVertex1) * (edgeVertex2 - edgeVertex1) / (valueAtVertex2 - valueAtVertex1);
        }
    }
}