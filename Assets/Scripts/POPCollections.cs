using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class QuantizedMesh
{
    public QuantizedMesh(MeshIndices meshIndices, List<Vector3> vertices, List<Vector2> uvs)
    {
        this.MeshIndices = meshIndices;
        this.Vertices = vertices;
        this.UVs = uvs;
    }
    
    public MeshIndices MeshIndices { get; } //삼각형 index
    public List<Vector3> Vertices { get; } //삼각형 vertex
    public List<Vector2> UVs { get; }
}

public class MeshIndices : IEnumerable<SubMeshIndices>
{
    public List<SubMeshIndices> subMeshes;

    public MeshIndices(int count)
    {
        subMeshes = Enumerable.Range(0, count).Select(_ => new SubMeshIndices(0)).ToList(); 
    }

    public SubMeshIndices this[int index]
    {
        get => subMeshes[index];
        set => subMeshes[index] = value;
    }
    
    public int Count
    {
        get => subMeshes.Count;
    }

    public void Add(SubMeshIndices elem)
    {
        subMeshes.Add(elem);
    }

    public IEnumerator<SubMeshIndices> GetEnumerator()
    {
        return subMeshes.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public  class SubMeshIndices : IEnumerable<Triangle>
{
    public List<Triangle> tris;

    public SubMeshIndices(List<Triangle> tris)
    {
        this.tris = tris;
    }

    public SubMeshIndices(int count)
    {
        tris = new List<Triangle>(count);
    }
    
    public Triangle this[int index]
    {
        get => tris[index];
        set => tris[index] = value;
    }
    
    public int Count
    {
        get => tris.Count;
    }

    public void Add(Triangle elem)
    {
        tris.Add(elem);
    }

    public IEnumerator<Triangle> GetEnumerator()
    {
        return tris.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public struct Triangle : IEnumerable<int>
{
    private int p1;
    private int p2;
    private int p3;

    public Triangle(int p1, int p2, int p3)
    {
        this.p1 = p1;
        this.p2 = p2;
        this.p3 = p3;
    }

    public int this[int index]
    {
        get
        {
            switch (index)
            {
                case 0:
                    return this.p1;
                case 1:
                    return this.p2;
                case 2:
                    return this.p3;
                default:
                    throw new IndexOutOfRangeException("Only 3 points in Triangle.");
            }
        }
        set
        {
            switch (index)
            {
                case 0:
                    p1 = value;
                    break;
                case 1:
                    p2 = value;
                    break;
                case 2:
                    p3 = value;
                    break;
                default:
                    throw new IndexOutOfRangeException("Only 3 points in Triangle.");
            }
        }
    }

    public IEnumerator<int> GetEnumerator()
    {
        yield return p1;
        yield return p2;
        yield return p3;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}