using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter))]
public class POPGenerator : MonoBehaviour
{
    public SliderUI triangleSlider;
    public SliderUI vertexSlider;
    public SliderUI quantizationSlider;
    
    private POPBuffer _popBuffer;

    private Mesh _mesh;
    // Start is called before the first frame update
    private void Start()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        _mesh = meshFilter.mesh;
        _popBuffer = POPBuffer.GeneratePopBuffer(_mesh); //TODO 현재는 32bit float인듯?
        
        triangleSlider.SetMinMax(0, (int)_mesh.GetIndexCount(0));
        vertexSlider.SetMinMax(0, _mesh.vertexCount);
        quantizationSlider.SetMinMax(1, 32);
    }

    private void Update()
    {
        int quantizationLevel = (int)Time.realtimeSinceStartup % 8 + 1;
        ApplyPOPBuffer(_mesh, quantizationLevel);
    }

    private void ApplyPOPBuffer(Mesh mesh, int quantizationLevel)
    {
        (NativeArray<float3> verticesNative, NativeArray<uint> indicesNative) = POPBuffer.Decode(_popBuffer, quantizationLevel);
        
        int vertexAttributeCount = 1; //Position (, Normal, Tangent, UV, Vertex Color etc...)
        int vertexCount = verticesNative.Length;
        int triangleIndexCount = indicesNative.Length;

        Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1); //TODO : submesh Count로 대체
        Mesh.MeshData meshData = meshDataArray[0];

        var vertexAttributes = new NativeArray<VertexAttributeDescriptor>(vertexAttributeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        vertexAttributes[0] = new VertexAttributeDescriptor(dimension: 3);
        meshData.SetVertexBufferParams(vertexCount, vertexAttributes);
        vertexAttributes.Dispose();

        NativeArray<float3> positions = meshData.GetVertexData<float3>();
        positions.CopyFrom(verticesNative); //TODO 아에 여기다가 맨 처음부터 카피하도록??
        
        meshData.SetIndexBufferParams(triangleIndexCount, IndexFormat.UInt32);
        NativeArray<uint> indices = meshData.GetIndexData<uint>();
        indices.CopyFrom(indicesNative);  //TODO 아에 여기다가 맨 처음부터 카피하도록??

        meshData.subMeshCount = 1;
        meshData.SetSubMesh(0, new SubMeshDescriptor(0, triangleIndexCount)
        // {
        //     bounds = mesh.bounds, //TODO POP bound로 대체
        //     vertexCount = vertexCount
        // }, MeshUpdateFlags.DontRecalculateBounds
        );

        // mesh.bounds = mesh.bounds;  //TODO POP bound로 대체
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);
        mesh.RecalculateNormals();
        
        triangleSlider.SetCurrentValue(indicesNative.Length);
        vertexSlider.SetCurrentValue(verticesNative.Length);
        quantizationSlider.SetCurrentValue(quantizationLevel);
    }
}

struct Level
{
    public Level(List<Vector3Int> cells, List<Vector3> positions)
    {
        this.cells = cells;
        this.positions = positions;
    }

    public List<Vector3Int> cells { get; } //삼각형 index
    public List<Vector3> positions { get; } //삼각형 vertex
}

class POPBuffer
{
    private Bounds boundingBox;
    private List<Level> levels; //TODO streaming을 하지 않을 거라면 level별로 position을 나눌 필요가 없다. index만 나눠지면 된다. 

    public static (NativeArray<float3> vertices, NativeArray<uint> indices) Decode(POPBuffer popBuffer, int quantizationLevel)
    {
        //현재 quantizationLevel이 32이상일 경우 indices에서 문제가 있는 듯함. (31일 때랑 index, vertex 갯수가 같은데 이상하게 에러가 생김)
        List<uint> indices = popBuffer.levels.Take(quantizationLevel).Select(level => level.cells)
            .SelectMany(cells => cells).SelectMany(tri => new uint[]{ (uint)tri.x, (uint)tri.y, (uint)tri.z}).ToList();
        List<Vector3> positions = popBuffer.levels.Take(quantizationLevel).Select(level => level.positions)
            .SelectMany(positions => positions).ToList();
        // Debug.Log("Vertex Count : " + positions.Count);
        // Debug.Log("Index Count : " + indices.Count);
        
        if (indices.Count != 0 && positions.Count != 0) //TODO 왜 여기서 positions을 또 quantize 하는거? 
        {
            positions = QuantizeVertices(positions, quantizationLevel, popBuffer.boundingBox);
            positions = RescaleVertices(positions, popBuffer.boundingBox);
        }
        
        
        return (GetNativeVertexArrays(positions.ToArray()), new NativeArray<uint>(indices.ToArray(), Allocator.Temp));
    }
    
    public static unsafe NativeArray<float3> GetNativeVertexArrays(Vector3[] vertexArray) //https://gist.github.com/LotteMakesStuff/c2f9b764b15f74d14c00ceb4214356b4
    {
        // create a destination NativeArray to hold the vertices
        NativeArray<float3> verts = new NativeArray<float3>(vertexArray.Length, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);

        // pin the mesh's vertex buffer in place...
        fixed (void* vertexBufferPointer = vertexArray)
        {
            // ...and use memcpy to copy the Vector3[] into a NativeArray<floar3> without casting. whould be fast!
            UnsafeUtility.MemCpy(NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(verts),
                vertexBufferPointer, vertexArray.Length * (long) UnsafeUtility.SizeOf<float3>());
        }
        // we only hve to fix the .net array in place, the NativeArray is allocated in the C++ side of the engine and
        // wont move arround unexpectedly. We have a pointer to it not a reference! thats basically what fixed does,
        // we create a scope where its 'safe' to get a pointer and directly manipulate the array

        return verts;
    }

    public static POPBuffer GeneratePopBuffer(Mesh mesh)
    {
        int[] indices = mesh.GetIndices(0); //TODO 이거 nativeArray PTR로 복사비용 없앨 수 없나?
        List<Vector3Int> cells = indices.Select((elem, index) => new { Elem = elem, Index = index })
            .GroupBy(x => x.Index / 3)
            .Select(group => new Vector3Int(
                group.ElementAt(0).Elem,
                group.ElementAt(1).Elem,
                group.ElementAt(2).Elem)).ToList(); //TODO 이렇게 긴 LINQ, 최선인가..?

        List<Vector3> positions = new List<Vector3>();
        mesh.GetVertices(positions);

        return Encode(mesh.bounds, cells, positions, 32);
    }

    static POPBuffer Encode(Bounds boundingBox, List<Vector3Int> cells, List<Vector3> positions, int maxLevel)
    {
        // Bounds boundingBox = ComputeBoundingBox(positions);

        List<List<Vector3Int>> buckets = BuildBuckets(boundingBox, cells, positions, maxLevel);
        List<Level> levels = BuildLevels(buckets, positions);

        return new POPBuffer() { boundingBox = boundingBox, levels = levels };
    }

    static Bounds ComputeBoundingBox(List<Vector3> positions) //TODO 최적화 된 버전 찾아보기 -> Bounds 가 Object Space BB를 제공함.
    {
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        positions.ForEach((elem) =>
        {
            min = Vector3.Min(min, elem);
            max = Vector3.Max(max, elem);
        });
        return new Bounds((min + max) / 2, max - min);
    }

    static List<List<Vector3Int>> BuildBuckets(Bounds boundingBox, List<Vector3Int> cells, List<Vector3> positions, int maxLevel)
    {
        int[] cellLevels = Enumerable.Repeat(-1, cells.Count).ToArray();

        for (int level = maxLevel; level > 0; level--)
        {
            List<Vector3> quantizedPositions = QuantizeVertices(positions, level, boundingBox);
            List<int> cellIndices = ListNonDegenerateCells(cells, quantizedPositions);

            foreach (var index in cellIndices)
            {
                cellLevels[index] = level;
            }
        }

        List<List<Vector3Int>> buckets = Enumerable.Range(0, maxLevel).Select(_ => new List<Vector3Int>()).ToList();
        for(int i = 0; i < cellLevels.Length; i++) 
        {
            var cellLevel = cellLevels[i];
            if(cellLevel != -1) 
            {
                buckets[cellLevel - 1].Add(cells[i]);
            }
        }

        return buckets;
    }

    static List<Vector3> QuantizeVertices(List<Vector3> positions, int bits, Bounds sourceBound)
    {
        if (positions.Count == 0)
            return null;

        Vector3 min = Vector3.zero;
        Vector3 max = Vector3.one * (bits >= (sizeof(int) * 8) ? int.MaxValue : (1 << bits) - 1);
        Bounds targetBounds = new Bounds((max + min) * 0.5f, max - min);

        positions = RescaleVertices(positions, targetBounds, sourceBound);
        positions = FloorVertices(positions);
        return positions;
    }

    static List<Vector3> RescaleVertices(List<Vector3> positions, Bounds targetBounds) //TODO ㅠ 이렇게 밖에 못하나..? 
    {
        Bounds sourceBound = ComputeBoundingBox(positions);
        Vector3 sourceSizeOverTargetSize = Vector3.Scale(targetBounds.size, 
            new Vector3(1 / sourceBound.size.x, 1 / sourceBound.size.y, 1 / sourceBound.size.z));
        return positions.Select((elem) => Vector3.Scale(elem - sourceBound.min, sourceSizeOverTargetSize) + targetBounds.min).ToList();
    }
    
    static List<Vector3> RescaleVertices(List<Vector3> positions, Bounds targetBounds,  Bounds sourceBound) //TODO ㅠ 이렇게 밖에 못하나..? 
    {
        Vector3 sourceSizeOverTargetSize = Vector3.Scale(targetBounds.size, 
                new Vector3(1 / sourceBound.size.x, 1 / sourceBound.size.y, 1 / sourceBound.size.z));
        return positions.Select((elem) => Vector3.Scale(elem - sourceBound.min, sourceSizeOverTargetSize) + targetBounds.min).ToList();
    }
    
    static List<Vector3> FloorVertices(List<Vector3> positions) //TODO ㅠ 이렇게 밖에 못하나..? 
    {
        return positions.Select((elem) => new Vector3(
            Mathf.Floor(elem.x),
            Mathf.Floor(elem.y),
            Mathf.Floor(elem.z))
        ).ToList();
    }

    static List<int> ListNonDegenerateCells(List<Vector3Int> cells, List<Vector3> quantizedPositions)
    {
        List<int> nonDegenerateCells = new List<int>();
        for (int i = 0; i < cells.Count; i++)
        {
            var tri = cells[i];
            if (!IsTriangleDegenerate(
                    quantizedPositions[tri[0]],
                    quantizedPositions[tri[1]],
                    quantizedPositions[tri[2]] ))
            {
                nonDegenerateCells.Add(i);
            }
        }
        return nonDegenerateCells;
    }

    static bool IsTriangleDegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        return a == b || b == c  || c == a;
    }
    
    static List<Level> BuildLevels(List<List<Vector3Int>> buckets, List<Vector3> positions) //TODO 이게 최선? 
    {
        List<Level> levels = new List<Level>(buckets.Count);
        Dictionary<int, int> indexLookup = new Dictionary<int, int>();
        int lastIndex = 0;
        
        for (int i = 0; i < buckets.Count; i++)
        {
            List<Vector3Int> cells = buckets[i];
            List<Vector3Int> newCells = new List<Vector3Int>();
            List<Vector3> newPositions = new List<Vector3>();
            
            for (int j = 0; j < cells.Count; j++)
            {
                Vector3Int tri = cells[j];
                Vector3Int newTri = Vector3Int.zero;
                for (int k = 0; k < 3; k++)
                {
                    if (!indexLookup.ContainsKey(tri[k]))
                    {
                        newPositions.Add(positions[tri[k]]);
                        indexLookup.Add(tri[k], lastIndex++);
                    }
                    newTri[k] = indexLookup[tri[k]];
                }
                
                newCells.Add(newTri);
            }
            
            levels.Add(new Level(newCells, newPositions));
        }
        return levels;
    }
}


