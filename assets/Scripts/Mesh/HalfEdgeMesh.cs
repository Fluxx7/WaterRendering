using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

public partial class HalfEdgeMesh : Resource {
	[Export] public byte[] VertexBufferData;
	[Export] public byte[] HalfEdgeBufferData;
	[Export] public uint NumVerts;
	[Export] public uint NumHalfEdges;
}