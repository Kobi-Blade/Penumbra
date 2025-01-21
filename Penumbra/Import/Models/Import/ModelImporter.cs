using Lumina.Data.Parsing;
using OtterGui;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.ModelStructs;
using SharpGLTF.Schema2;

namespace Penumbra.Import.Models.Import;

public partial class ModelImporter
{
    private readonly ModelRoot _model;
    private readonly IoNotifier _notifier;

    private readonly List<MeshStruct> _meshes = new();
    private readonly List<MdlStructs.SubmeshStruct> _subMeshes = new();
    private readonly List<string> _materials = new();
    private readonly List<MdlStructs.VertexDeclarationStruct> _vertexDeclarations = new();
    private readonly List<byte> _vertexBuffer = new();
    private readonly List<ushort> _indices = new();
    private readonly List<string> _bones = new();
    private readonly List<BoneTableStruct> _boneTables = new();
    private readonly BoundingBox _boundingBox = new();
    private readonly List<string> _metaAttributes = new();
    private readonly Dictionary<string, List<MdlStructs.ShapeMeshStruct>> _shapeMeshes = new();
    private readonly List<MdlStructs.ShapeValueStruct> _shapeValues = new();

    public ModelImporter(ModelRoot model, IoNotifier notifier)
    {
        _model = model;
        _notifier = notifier;
    }

    public static MdlFile Import(ModelRoot model, IoNotifier notifier)
    {
        var importer = new ModelImporter(model, notifier);
        return importer.Create();
    }

    [GeneratedRegex(@"[_ ^](?'Mesh'[0-9]+)[.-]?(?'SubMesh'[0-9]+)?$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex MeshNameGroupingRegex();

    private MdlFile Create()
    {
        foreach (var (subMeshNodes, index) in GroupedMeshNodes().WithIndex())
        {
            BuildMeshForGroup(subMeshNodes, index);
        }

        var materials = _materials.Count > 0 ? _materials : new List<string> { "/NO_MATERIAL" };
        var shapes = BuildShapes();
        var shapeMeshes = _shapeMeshes.Values.SelectMany(x => x).ToList();
        var indexBuffer = _indices.SelectMany(BitConverter.GetBytes).ToArray();

        return new MdlFile
        {
            VertexOffset = new uint[] { 0, 0, 0 },
            VertexBufferSize = new uint[] { (uint)_vertexBuffer.Count, 0, 0 },
            IndexOffset = new uint[] { (uint)_vertexBuffer.Count, 0, 0 },
            IndexBufferSize = new uint[] { (uint)indexBuffer.Length, 0, 0 },
            VertexDeclarations = _vertexDeclarations.ToArray(),
            Meshes = _meshes.ToArray(),
            SubMeshes = _subMeshes.ToArray(),
            BoneTables = _boneTables.ToArray(),
            Bones = _bones.ToArray(),
            SubMeshBoneMap = Array.Empty<ushort>(),
            Attributes = _metaAttributes.ToArray(),
            Shapes = shapes.ToArray(),
            ShapeMeshes = shapeMeshes.ToArray(),
            ShapeValues = _shapeValues.ToArray(),
            LodCount = 1,
            Lods = new[]
            {
                new MdlStructs.LodStruct
                {
                    MeshIndex = 0,
                    MeshCount = (ushort)_meshes.Count,
                    ModelLodRange = 0,
                    TextureLodRange = 0,
                    VertexDataOffset = 0,
                    VertexBufferSize = (uint)_vertexBuffer.Count,
                    IndexDataOffset = (uint)_vertexBuffer.Count,
                    IndexBufferSize = (uint)indexBuffer.Length,
                },
            },
            Materials = materials.ToArray(),
            BoundingBoxes = _boundingBox.ToStruct(),
            Radius = 1,
            BoneBoundingBoxes = Enumerable.Repeat(MdlFile.EmptyBoundingBox, _bones.Count).ToArray(),
            RemainingData = _vertexBuffer.Concat(indexBuffer).ToArray(),
            Valid = true,
        };
    }

    private IEnumerable<IEnumerable<Node>> GroupedMeshNodes()
    {
        return _model.LogicalNodes
            .Where(node => node.Mesh != null)
            .Select(node =>
            {
                var name = node.Name ?? node.Mesh.Name ?? "NOMATCH";
                var match = MeshNameGroupingRegex().Match(name);
                return (node, match);
            })
            .Where(pair => pair.match.Success)
            .OrderBy(pair =>
            {
                var subMeshGroup = pair.match.Groups["SubMesh"];
                return subMeshGroup.Success ? int.Parse(subMeshGroup.Value) : 0;
            })
            .GroupBy(
                pair => int.Parse(pair.match.Groups["Mesh"].Value),
                pair => pair.node
            )
            .OrderBy(group => group.Key);
    }

    private void BuildMeshForGroup(IEnumerable<Node> subMeshNodes, int index)
    {
        var subMeshOffset = _subMeshes.Count;
        var vertexOffset = _vertexBuffer.Count;
        var indexOffset = _indices.Count;

        var mesh = MeshImporter.Import(subMeshNodes, _notifier.WithContext($"Mesh {index}"));
        var meshStartIndex = (uint)(mesh.MeshStruct.StartIndex + indexOffset);

        var materialIndex = mesh.Material != null
            ? GetMaterialIndex(mesh.Material)
            : (ushort)0;

        var boneTableIndex = mesh.Bones != null
            ? BuildBoneTable(mesh.Bones)
            : (ushort)255;

        _meshes.Add(mesh.MeshStruct with
        {
            MaterialIndex = materialIndex,
            SubMeshIndex = (ushort)(mesh.MeshStruct.SubMeshIndex + subMeshOffset),
            BoneTableIndex = boneTableIndex,
            StartIndex = meshStartIndex,
            VertexBufferOffset1 = (uint)(mesh.MeshStruct.VertexBufferOffset1 + vertexOffset),
            VertexBufferOffset2 = (uint)(mesh.MeshStruct.VertexBufferOffset2 + vertexOffset),
            VertexBufferOffset3 = (uint)(mesh.MeshStruct.VertexBufferOffset3 + vertexOffset),
        });

        _boundingBox.Merge(mesh.BoundingBox);

        _subMeshes.AddRange(mesh.SubMeshStructs.Select(m => m with
        {
            AttributeIndexMask = Utility.GetMergedAttributeMask(
                m.AttributeIndexMask, mesh.MetaAttributes, _metaAttributes),
            IndexOffset = (uint)(m.IndexOffset + indexOffset),
        }));

        _vertexDeclarations.Add(mesh.VertexDeclaration);
        _vertexBuffer.AddRange(mesh.VertexBuffer);
        _indices.AddRange(mesh.Indices);

        foreach (var meshShapeKey in mesh.ShapeKeys)
        {
            if (!_shapeMeshes.TryGetValue(meshShapeKey.Name, out var shapeMeshes))
            {
                shapeMeshes = new List<MdlStructs.ShapeMeshStruct>();
                _shapeMeshes.Add(meshShapeKey.Name, shapeMeshes);
            }

            shapeMeshes.Add(meshShapeKey.ShapeMesh with
            {
                MeshIndexOffset = meshStartIndex,
                ShapeValueOffset = (uint)_shapeValues.Count,
            });

            _shapeValues.AddRange(meshShapeKey.ShapeValues);
        }

        if (_shapeValues.Count > ushort.MaxValue)
        {
            throw _notifier.Exception(
                $"Importing this file would require more than the maximum of {ushort.MaxValue} shape values.\nTry removing or applying shape keys that do not need to be changed at runtime in-game.");
        }
    }

    private ushort GetMaterialIndex(string materialName)
    {
        var index = _materials.IndexOf(materialName);
        if (index >= 0)
        {
            return (ushort)index;
        }

        if (_materials.Count >= 4)
        {
            return 0;
        }

        _materials.Add(materialName);
        return (ushort)_materials.Count;
    }

    private ushort BuildBoneTable(List<string> boneNames)
    {
        var boneIndices = new List<ushort>();
        foreach (var boneName in boneNames)
        {
            var boneIndex = _bones.IndexOf(boneName);
            if (boneIndex == -1)
            {
                boneIndex = _bones.Count;
                _bones.Add(boneName);
            }

            boneIndices.Add((ushort)boneIndex);
        }

        if (boneIndices.Count > 128)
        {
            throw _notifier.Exception("XIV does not support meshes weighted to a total of more than 128 bones.");
        }

        var boneIndicesArray = new ushort[128];
        Array.Copy(boneIndices.ToArray(), boneIndicesArray, boneIndices.Count);

        var boneTableIndex = _boneTables.Count;
        _boneTables.Add(new BoneTableStruct
        {
            BoneIndex = boneIndicesArray,
            BoneCount = (byte)boneIndices.Count,
        });

        return (ushort)boneTableIndex;
    }

    private List<MdlFile.Shape> BuildShapes()
    {
        var shapes = new List<MdlFile.Shape>();
        foreach (var (keyName, keyMeshes) in _shapeMeshes)
        {
            shapes.Add(new MdlFile.Shape
            {
                ShapeName = keyName,
                ShapeMeshStartIndex = new ushort[] { (ushort)shapes.Count, 0, 0 },
                ShapeMeshCount = new ushort[] { (ushort)keyMeshes.Count, 0, 0 },
            });
        }
        return shapes;
    }
}
