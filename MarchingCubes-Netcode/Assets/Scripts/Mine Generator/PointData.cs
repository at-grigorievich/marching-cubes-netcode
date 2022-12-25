using UnityEngine;

public struct PointData
{
    public Vector3 Position;
    public float Density;

    public PointData(Vector3 pos, float dens)
    {
        Position = pos;
        Density = dens;
    }
}
