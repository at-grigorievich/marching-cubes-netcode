using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MinimalLengthCalculation : MonoBehaviour
{
    [SerializeField] private MeshFilter _filter;

    private Vector3[] _vertices;

    private void OnEnable()
    {
        _vertices = _filter.sharedMesh.vertices;
    }

    public void OnDrawGizmosSelected()
    {
        foreach (var v in _vertices)
        {
            Gizmos.DrawSphere(v, 5f);
        }
    }
}
