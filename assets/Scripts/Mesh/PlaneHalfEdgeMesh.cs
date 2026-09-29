using System;
using System.Threading.Tasks;
using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

[GlobalClass, Tool]
public partial class PlaneHalfEdgeMesh : HalfEdgeMesh {
	private Vector2 planeSize = new(500f, 500f);
	private uint baseSubdivisions = 16;
	[Export] public Vector2 PlaneSize {
		get => planeSize;
		set {
			planeSize = value;
			Invalidate();
		}
	}

	[Export(PropertyHint.Range, "0, 2048, 1")]
	public uint BaseSubdivisions {
		get => baseSubdivisions;
		set {
			baseSubdivisions = value;
			Invalidate();
		}
	}
	
	public override Aabb GetAABB() {
		return Engine.IsEditorHint() 
			? new Aabb(new Vector3(-PlaneSize.X * 0.5f, -1f, -PlaneSize.Y * 0.5f),
				new Vector3(PlaneSize.X, 1f, PlaneSize.Y))
			: new Aabb(new Vector3(-PlaneSize.X * 0.5f, -1f, -PlaneSize.Y * 0.5f),
				new Vector3(PlaneSize.X, 100f, PlaneSize.Y));
	}

	protected override void Build(out byte[] half_edge_bytes, out byte[] vertex_bytes) {
		NumHalfEdges = BaseSubdivisions * BaseSubdivisions * 4;
		var halfEdgeBuffer = new HalfEdge[NumHalfEdges];
		var vertBuffer = new Vector3[NumHalfEdges];
		NumVerts = 0;
		for (uint x = 0; x < BaseSubdivisions; x++) {
			for (uint y = 0; y < BaseSubdivisions; y++) {
				BuildFace(x, y, halfEdgeBuffer, vertBuffer);
			}
		}

		uint half_edge_array_size = NumHalfEdges * 24;
		uint vert_buffer_size = 12 * NumVerts;

		half_edge_bytes = new byte[8 + half_edge_array_size];
		vertex_bytes = new byte[vert_buffer_size];
		uint[] first_two = [NumHalfEdges, NumVerts];
		Buffer.BlockCopy(first_two, 0, half_edge_bytes, 0, 8);
		Buffer.BlockCopy(PackHalfEdges(halfEdgeBuffer), 0, half_edge_bytes, 8, (int) half_edge_array_size);
		Buffer.BlockCopy(PackVector3s(vertBuffer), 0, vertex_bytes, 0, (int) vert_buffer_size);
	}

	private void BuildFace(uint x_index, uint y_index, HalfEdge[] halfEdgeBuffer, Vector3[] vertBuffer) {
		uint max_id = BaseSubdivisions - 1;
		uint verts_per_row = BaseSubdivisions + 1;
		uint face = x_index + y_index * BaseSubdivisions;
		var verts = new uint[4];
		var edges = new uint[4];
		var twins = new int[4];

		uint first_vert = x_index + y_index * verts_per_row;
		verts[0] = first_vert;
		verts[1] = 1 + first_vert;
		verts[2] = 1 + first_vert + verts_per_row;
		verts[3] = first_vert + verts_per_row;

		uint first_edge = x_index + y_index * (verts_per_row * 2);
		edges[0] = first_edge;
		edges[1] = first_edge + BaseSubdivisions + 1;
		edges[2] = first_edge + 2 * verts_per_row;
		edges[3] = first_edge + BaseSubdivisions;

		twins[0] = y_index > 0 ? (int) (4 * (face - BaseSubdivisions) + 2) : -1;
		twins[1] = x_index < max_id ? (int) (4 * (face + 1) + 3) : -1;
		twins[2] = y_index < max_id ? (int) (4 * (face + BaseSubdivisions)) : -1;
		twins[3] = x_index > 0 ? (int) (4 * (face - 1) + 1) : -1;

		uint start_id = 4 * face;
		for (uint i = 0; i < 4; i++) {
			uint curr_id = start_id + i;
			HalfEdge curr_he;
			curr_he.face = face;
			curr_he.edge = edges[i];
			curr_he.vert = verts[i];
			curr_he.twin = twins[i];
			curr_he.next = i == 3 ? start_id : curr_id + 1;
			curr_he.prev = i == 0 ? start_id + 3 : curr_id - 1;
			halfEdgeBuffer[curr_id] = curr_he;
		}

		Vector2 step = PlaneSize / BaseSubdivisions;
		float half_size = BaseSubdivisions * 0.5f;

		float x_lower = (x_index - half_size) * step.Y;
		float x_upper = x_lower + step.Y;
		float y_lower = (y_index - half_size) * step.X;
		float y_upper = y_lower + step.X;

		var v0 = new Vector3(x_lower, 0f, y_lower);
		var v1 = new Vector3(x_upper, 0f, y_lower);
		var v2 = new Vector3(x_upper, 0f, y_upper);
		var v3 = new Vector3(x_lower, 0f, y_upper);

		vertBuffer[verts[0]] = v0;
		NumVerts++;
		if (x_index == max_id) {
			vertBuffer[verts[1]] = v1;
			NumVerts++;
		}
		if (y_index == max_id) {
			vertBuffer[verts[3]] = v3;
			NumVerts++;
		}
		if (x_index == max_id && y_index == max_id) {
			vertBuffer[verts[2]] = v2;
			NumVerts++;
		}
	}
}