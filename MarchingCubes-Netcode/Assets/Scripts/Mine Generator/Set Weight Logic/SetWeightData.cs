using UnityEngine;

namespace MineGenerator.SetWeight
{
    public readonly struct SetWeightData
    {
        public Vector3 Point { get; }
        public float Radius { get; }
        
        public float Pressure { get; }

        public SetWeightData(Vector3 point, float radius, float pressure)
        {
            Point = point;
            Radius = radius;
            Pressure = pressure;
        }
    }
}