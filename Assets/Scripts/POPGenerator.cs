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
    public int LevelClamp = 8;
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
        int quantizationLevel = (int)Time.realtimeSinceStartup % LevelClamp + 1;
        ApplyPOPBuffer(_mesh, quantizationLevel);
    }

    private void ApplyPOPBuffer(Mesh mesh, int quantizationLevel)
    {
        (NativeArray<float3> verticesNative, NativeArray<uint> indicesNative, NativeArray<float2> uvNative, List<int> subMeshIndexCount) = POPBuffer.Decode(_popBuffer, quantizationLevel);
        
        int vertexAttributeCount = 2; //Position UV (Normal, Tangent, Vertex Color etc...)
        int vertexCount = verticesNative.Length;
        int triangleIndexCount = indicesNative.Length;

        Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1); //TODO : submesh Count로 대체
        //?? 이걸 submesh Count로 바꾸면, meshData마다 똑같은 vertex를 설정해줘야하는건가? 아니면 0번째만 vertex설정해주면 되는건가? 알아보기 
        Mesh.MeshData meshData = meshDataArray[0];

        var vertexAttributes = new NativeArray<VertexAttributeDescriptor>(vertexAttributeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        vertexAttributes[0] = new VertexAttributeDescriptor(dimension: 3);
        vertexAttributes[1] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, dimension: 2, stream: 1); //TODO : position만 stream 0 나머진 다 stream 1
        meshData.SetVertexBufferParams(vertexCount, vertexAttributes);
        vertexAttributes.Dispose();

        NativeArray<float3> positions = meshData.GetVertexData<float3>();
        positions.CopyFrom(verticesNative); //TODO 아에 여기다가 맨 처음부터 카피하도록??
        
        NativeArray<float2> texCoords = meshData.GetVertexData<float2>(1);
        texCoords.CopyFrom(uvNative);
        
        meshData.SetIndexBufferParams(triangleIndexCount, IndexFormat.UInt32);
        NativeArray<uint> indices = meshData.GetIndexData<uint>();
        indices.CopyFrom(indicesNative);  //TODO 아에 여기다가 맨 처음부터 카피하도록??

        meshData.subMeshCount = mesh.subMeshCount;
        for (int i = 0; i < subMeshIndexCount.Count; i++)
        {
            meshData.SetSubMesh(i, new SubMeshDescriptor(i == 0 ? 0 : subMeshIndexCount[i-1], subMeshIndexCount[i])
                // {
                //     bounds = mesh.bounds, //TODO POP bound로 대체
                //     vertexCount = vertexCount
                // }, MeshUpdateFlags.DontRecalculateBounds
            );
        }
        
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
    public Level(CellPerLevel cells, List<Vector3> positions, List<Vector2> uvs)
    {
        this.cells = cells;
        this.positions = positions;
        this.uvs = uvs;
    }
    
    public CellPerLevel cells { get; } //삼각형 index
    public List<Vector3> positions { get; } //삼각형 vertex
    public List<Vector2> uvs { get; }
}

struct CellPerSubmesh
{
    public List<Vector3Int> cells;

    public CellPerSubmesh(List<Vector3Int> cells)
    {
        this.cells = cells;
    }

    public CellPerSubmesh(int count)
    {
        cells = new List<Vector3Int>(count);
    }
}
struct CellPerLevel
{
    //TODO 아래 collection을 field로 접근하게 하지말고, 이 struct/class자체가 [,]를 override해서 접근할 수 있게 바꿔야 코드가 깔끔해짐.
    public List<CellPerSubmesh> cellPerSubmeshes; //List<List<Vector3Int>> 

    public CellPerLevel(int count)
    {
        cellPerSubmeshes = Enumerable.Range(0, count).Select(_ => new CellPerSubmesh(0)).ToList(); 
    }
}

class POPBuffer
{
    private Bounds boundingBox;
    private List<Level> levels; //TODO streaming을 하지 않을 거라면 level별로 position을 나눌 필요가 없다. index만 나눠지면 된다. 

    public static (NativeArray<float3> vertices, NativeArray<uint> indices, NativeArray<float2> uvs, List<int> subMeshIndexCount) Decode(POPBuffer popBuffer, int quantizationLevel)
    {
        List<uint> indices = new List<uint>();
        List<int> subMeshStartAndCount = new List<int>();
        int submeshCount = popBuffer.levels[0].cells.cellPerSubmeshes.Count;
        for (int i = 0; i < submeshCount; i++)
        {
            for (int j = 0; j < quantizationLevel; j++)
            {
                indices.AddRange(popBuffer.levels[j].cells.cellPerSubmeshes[i].cells.SelectMany(tri => new uint[]{ (uint)tri.x, (uint)tri.y, (uint)tri.z}));
            }
            subMeshStartAndCount.Add(indices.Count - subMeshStartAndCount.Sum());
        }
        
        // List<uint> indices = popBuffer.levels.Take(quantizationLevel)
        //     .Select(level => level.cells.cellPerSubmeshes).SelectMany(cellPerSubmeshes => cellPerSubmeshes)
        //     .Select(cellPerSubmesh => cellPerSubmesh.cells).SelectMany(cells => cells)
        //     .SelectMany(tri => new uint[]{ (uint)tri.x, (uint)tri.y, (uint)tri.z}).ToList();
        
        List<Vector3> positions = popBuffer.levels.Take(quantizationLevel).Select(level => level.positions)
            .SelectMany(positions => positions).ToList();
        List<Vector2> uvs = popBuffer.levels.Take(quantizationLevel).Select(level => level.uvs)
            .SelectMany(uvs => uvs).ToList();
        
        if (indices.Count != 0 && positions.Count != 0) //TODO 왜 여기서 positions을 또 quantize 하는거? 
        {
            positions = QuantizeVertices(positions, quantizationLevel, popBuffer.boundingBox);
            positions = RescaleVertices(positions, popBuffer.boundingBox);
        }
        
        return (GetNativeVertexArrays(positions.ToArray()), new NativeArray<uint>(indices.ToArray(), Allocator.Temp), GetNativeVertexArrays(uvs.ToArray()), subMeshStartAndCount);
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
    
    public static unsafe NativeArray<float2> GetNativeVertexArrays(Vector2[] vertexArray) //https://gist.github.com/LotteMakesStuff/c2f9b764b15f74d14c00ceb4214356b4
    {
        // create a destination NativeArray to hold the vertices
        NativeArray<float2> verts = new NativeArray<float2>(vertexArray.Length, Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);

        // pin the mesh's vertex buffer in place...
        fixed (void* vertexBufferPointer = vertexArray)
        {
            // ...and use memcpy to copy the Vector3[] into a NativeArray<floar3> without casting. whould be fast!
            UnsafeUtility.MemCpy(NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(verts),
                vertexBufferPointer, vertexArray.Length * (long) UnsafeUtility.SizeOf<float2>());
        }
        // we only hve to fix the .net array in place, the NativeArray is allocated in the C++ side of the engine and
        // wont move arround unexpectedly. We have a pointer to it not a reference! thats basically what fixed does,
        // we create a scope where its 'safe' to get a pointer and directly manipulate the array

        return verts;
    }

    public static POPBuffer GeneratePopBuffer(Mesh mesh)
    {
        List<Vector3> positions = new List<Vector3>();
        mesh.GetVertices(positions);

        List<Vector2> uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);

        List<CellPerSubmesh> cellPerSubMeshes = new List<CellPerSubmesh>();
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            int[] indices = mesh.GetIndices(i); //TODO 이거 nativeArray PTR로 복사비용 없앨 수 없나?
            List<Vector3Int> cells = indices.Select((elem, index) => new { Elem = elem, Index = index })
                .GroupBy(x => x.Index / 3)
                .Select(group => new Vector3Int(
                    group.ElementAt(0).Elem,
                    group.ElementAt(1).Elem,
                    group.ElementAt(2).Elem)).ToList(); //TODO 이렇게 긴 LINQ, 최선인가..?
            
            cellPerSubMeshes.Add(new CellPerSubmesh(cells));
        }

        return Encode(mesh.bounds, cellPerSubMeshes, positions, uvs, 32);
    }

    static POPBuffer Encode(Bounds boundingBox, List<CellPerSubmesh> cellPerSubMeshes, List<Vector3> positions, List<Vector2> uvs, int maxLevel)
    {
        List<CellPerLevel> buckets = BuildBuckets(boundingBox, cellPerSubMeshes, positions, maxLevel);
        List<Level> levels = BuildLevels(buckets, positions, uvs);

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

    static List<CellPerLevel> BuildBuckets(Bounds boundingBox, List<CellPerSubmesh> cellPerSubMeshes, List<Vector3> positions, int maxLevel)
    {
        List<int[]> cellLevelPerSubMeshes = new List<int[]>(cellPerSubMeshes.Count); //cellLevelPerSubMeshes[0] => subMesh0의 각 cell들의 quantizationLevel을 의미. 
        for (int i = 0; i < cellPerSubMeshes.Count; i++)
        {
            CellPerSubmesh cellPerSubMesh = cellPerSubMeshes[i];
            int[] cellLevels = Enumerable.Repeat(-1, cellPerSubMesh.cells.Count).ToArray();
            for (int level = maxLevel; level > 0; level--)
            {
                List<Vector3> quantizedPositions = QuantizeVertices(positions, level, boundingBox);
                List<int> cellIndices = ListNonDegenerateCells(cellPerSubMesh.cells, quantizedPositions);

                foreach (var index in cellIndices)
                {
                    cellLevels[index] = level;
                }
            }

            cellLevelPerSubMeshes.Add(cellLevels);
        }
        
        List<CellPerLevel> buckets = Enumerable.Range(0, maxLevel).Select(_ => new CellPerLevel(cellLevelPerSubMeshes.Count)).ToList(); 
        //buckets[0] => 0레벨에서의 subMesh0과 subMesh1의 삼각형들을 가지고 있음. 
        for (int i = 0; i < cellLevelPerSubMeshes.Count; i++)
        {
            int[] cellLevels = cellLevelPerSubMeshes[i]; //i번째 subMesh의 cell들의 quantizationLevel
            for(int j = 0; j < cellLevels.Length; j++) 
            {
                int cellLevel = cellLevels[j]; //j번째 cell(삼각형)의 quantizationLevel
                if(cellLevel != -1) 
                {
                    buckets[cellLevel - 1].cellPerSubmeshes[i].cells.Add(cellPerSubMeshes[i].cells[j]);
                }
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
    
    static List<Level> BuildLevels(List<CellPerLevel> buckets, List<Vector3> positions, List<Vector2> uvs) //TODO 이게 최선? 
    {
        List<Level> levels = new List<Level>(buckets.Count);
        Dictionary<int, int> indexLookup = new Dictionary<int, int>();
        int lastIndex = 0;
        
        for (int i = 0; i < buckets.Count; i++)
        {
            CellPerLevel cellPerLevel = buckets[i];
            CellPerLevel newCellPerLevel = new CellPerLevel(0);
            List<Vector3> newPositions = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            
            for (int j = 0; j < cellPerLevel.cellPerSubmeshes.Count; j++)
            {
                List<Vector3Int> cells = cellPerLevel.cellPerSubmeshes[j].cells;
                List<Vector3Int> newCells = new List<Vector3Int>();
                
                for (int k = 0; k < cells.Count; k++)
                {
                    Vector3Int tri = cells[k];
                    Vector3Int newTri = Vector3Int.zero;
                    for (int l = 0; l < 3; l++)
                    {
                        if (!indexLookup.ContainsKey(tri[l]))
                        {
                            newPositions.Add(positions[tri[l]]);
                            newUVs.Add(uvs[tri[l]]);
                            indexLookup.Add(tri[l], lastIndex++);
                        }
                        newTri[l] = indexLookup[tri[l]];
                    }
                
                    newCells.Add(newTri);
                }
                
                newCellPerLevel.cellPerSubmeshes.Add(new CellPerSubmesh(newCells));
            }
            
            levels.Add(new Level(newCellPerLevel, newPositions, newUVs));
        }
        return levels;
    }
}


