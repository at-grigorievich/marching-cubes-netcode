using System;
using UnityEngine;

namespace MineGenerator
{
    [Serializable]
    public struct PointData
    {
        [SerializeField,HideInInspector] public Vector3 Position;

        [SerializeField,HideInInspector] public bool IsAvailable;

        [SerializeField,HideInInspector] private float _density;

        public float Density
        {
            get => _density;
            set => _density = value; 
        }
        
        public PointData(Vector3 pos, float dens)
        {
            Position = pos;
            _density = dens;

            IsAvailable = false;
        }
    }
}
