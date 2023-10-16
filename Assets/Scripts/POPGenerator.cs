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
        
        const int vertexAttributeCount = 2; //Position UV (Normal, Tangent, Vertex Color etc...)
        int vertexCount = verticesNative.Length;
        int triangleIndexCount = indicesNative.Length;

        Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1);
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
        indices.CopyFrom(indicesNative);

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

class POPBuffer
{
    private Bounds boundingBox;
    private List<QuantizedMesh> quantizedMeshes; //TODO streaming을 하지 않을 거라면 level별로 position을 나눌 필요가 없다. index만 나눠지면 된다. 

    public static POPBuffer GeneratePopBuffer(Mesh mesh)
    {
        List<Vector3> vertices = new List<Vector3>();
        mesh.GetVertices(vertices);

        List<Vector2> uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);

        List<SubMeshIndices> meshIndices = new List<SubMeshIndices>();
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            int[] indices = mesh.GetIndices(i); //TODO 이거 nativeArray PTR로 복사비용 없앨 수 없나?
            List<Triangle> cells = indices.Select((elem, index) => new { Elem = elem, Index = index })
                .GroupBy(x => x.Index / 3)
                .Select(group => new Triangle(
                    group.ElementAt(0).Elem,
                    group.ElementAt(1).Elem,
                    group.ElementAt(2).Elem)).ToList(); //TODO 이렇게 긴 LINQ, 최선인가..?
            
            meshIndices.Add(new SubMeshIndices(cells));
        }

        return Encode(mesh.bounds, meshIndices, vertices, uvs, 32);
    }
    
    static POPBuffer Encode(Bounds boundingBox, List<SubMeshIndices> meshIndices, List<Vector3> positions, List<Vector2> uvs, int maxLevel)
    {
        List<MeshIndices> quantizedMeshIndices = QuantizeMeshIndices(boundingBox, meshIndices, positions, maxLevel);
        List<QuantizedMesh> levels = ReorderVertexData(quantizedMeshIndices, positions, uvs);

        return new POPBuffer() { boundingBox = boundingBox, quantizedMeshes = levels };
    }
    
    public static (NativeArray<float3> vertices, NativeArray<uint> indices, NativeArray<float2> uvs, List<int> subMeshIndexCount) Decode(POPBuffer popBuffer, int quantizationLevel)
    {
        List<uint> indices = new List<uint>();
        List<int> subMeshStartAndCount = new List<int>();
        int subMeshCount = popBuffer.quantizedMeshes[0].MeshIndices.Count;
        for (int i = 0; i < subMeshCount; i++)
        {
            for (int j = 0; j < quantizationLevel; j++)
            {
                indices.AddRange(popBuffer.quantizedMeshes[j].MeshIndices[i].tris.SelectMany(tri => new uint[]{ (uint)tri[0], (uint)tri[1], (uint)tri[2]}));
            }
            subMeshStartAndCount.Add(indices.Count - subMeshStartAndCount.Sum());
        }

        List<Vector3> positions = popBuffer.quantizedMeshes.Take(quantizationLevel).Select(level => level.Vertices)
            .SelectMany(positions => positions).ToList();
        List<Vector2> uvs = popBuffer.quantizedMeshes.Take(quantizationLevel).Select(level => level.UVs)
            .SelectMany(uvs => uvs).ToList();
        
        if (indices.Count != 0 && positions.Count != 0)
        {
            positions = QuantizeVertices(positions, quantizationLevel, popBuffer.boundingBox);
            positions = RescaleVertices(positions, popBuffer.boundingBox);
        }
        
        return (NativeConverter.GetNativeVertexArrays(positions.ToArray()), new NativeArray<uint>(indices.ToArray(), Allocator.Temp), NativeConverter.GetNativeVertexArrays(uvs.ToArray()), subMeshStartAndCount);
    }

    static List<MeshIndices> QuantizeMeshIndices(Bounds boundingBox, List<SubMeshIndices> meshIndices, List<Vector3> vertices, int maxLevel)
    {
        List<int[]> qLevelsPerSubMesh = new List<int[]>(meshIndices.Count);
        foreach (var subMeshIndices in meshIndices)
        {
            int[] qLevelOfIndex = Enumerable.Repeat(-1, subMeshIndices.tris.Count).ToArray();
            for (int qLevel = maxLevel; qLevel > 0; qLevel--)
            {
                List<Vector3> quantizedPos = QuantizeVertices(vertices, qLevel, boundingBox);
                List<int> validIndices = ListNonDegenerateCells(subMeshIndices.tris, quantizedPos);

                foreach (var index in validIndices)
                {
                    qLevelOfIndex[index] = qLevel;
                }
            }
            qLevelsPerSubMesh.Add(qLevelOfIndex);
        }
        
        List<MeshIndices> meshPerLevel = Enumerable.Range(0, maxLevel).Select(_ => new MeshIndices(qLevelsPerSubMesh.Count)).ToList();
        for (int i = 0; i < qLevelsPerSubMesh.Count; i++)
        {
            int[] qLevels = qLevelsPerSubMesh[i];
            for(int j = 0; j < qLevels.Length; j++) 
            {
                int qLevelOfTriangle = qLevels[j];
                if(qLevelOfTriangle != -1) 
                {
                    meshPerLevel[qLevelOfTriangle - 1][i].Add(meshIndices[i][j]);
                }
            }
        }
        
        return meshPerLevel;
    }
    
    static List<QuantizedMesh> ReorderVertexData(List<MeshIndices> quantizedMeshIndices, List<Vector3> vertices, List<Vector2> uvs) //TODO 이게 최선? 
    {
        List<QuantizedMesh> levels = new List<QuantizedMesh>(quantizedMeshIndices.Count);
        
        Dictionary<int, int> indexLookup = new Dictionary<int, int>();
        int lastIndex = 0;
        List<int> reorderedTri = new List<int>();
        foreach (var sourceMeshIndices in quantizedMeshIndices)
        {
            MeshIndices reorderedMeshIndices = new MeshIndices(0);
            List<Vector3> reorderedVertices = new List<Vector3>();
            List<Vector2> reorderedUVs = new List<Vector2>();
            
            foreach (var subMeshIndices in sourceMeshIndices)
            {
                List<Triangle> tris = subMeshIndices.tris;
                List<Triangle> reorderedTris = new List<Triangle>();
                
                foreach (var tri in tris)
                {
                    reorderedTri.Clear();
                    foreach (var vertexIndex in tri)
                    {
                        if (!indexLookup.ContainsKey(vertexIndex))
                        {
                            reorderedVertices.Add(vertices[vertexIndex]);
                            reorderedUVs.Add(uvs[vertexIndex]);
                            indexLookup.Add(vertexIndex, lastIndex++);
                        }
                        reorderedTri.Add(indexLookup[vertexIndex]);
                    }
                    reorderedTris.Add(new Triangle(reorderedTri[0],reorderedTri[1],reorderedTri[2]));
                }
                reorderedMeshIndices.Add(new SubMeshIndices(reorderedTris));
            }
            
            levels.Add(new QuantizedMesh(reorderedMeshIndices, reorderedVertices, reorderedUVs));
        }
        return levels;
    }
    
    static Bounds ComputeBoundingBox(List<Vector3> vertices) //TODO 최적화 된 버전 찾아보기 -> Bounds 가 Object Space BB를 제공함.
    {
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        vertices.ForEach((vert) =>
        {
            min = Vector3.Min(min, vert);
            max = Vector3.Max(max, vert);
        });
        return new Bounds((min + max) / 2, max - min);
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
    
    static List<Vector3> RescaleVertices(List<Vector3> positions, Bounds targetBounds, Bounds sourceBound) //TODO ㅠ 이렇게 밖에 못하나..? 
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

    static List<int> ListNonDegenerateCells(List<Triangle> cells, List<Vector3> quantizedPositions)
    {
        List<int> nonDegenerateCells = new List<int>();
        for (int i = 0; i < cells.Count; i++)
        {
            var tri = cells[i];
            if (!IsTriangleDegenerate(
                    quantizedPositions[tri[0]],
                    quantizedPositions[tri[1]],
                    quantizedPositions[tri[2]]))
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
}


