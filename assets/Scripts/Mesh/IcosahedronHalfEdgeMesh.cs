using System;
using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

[GlobalClass, Tool]
public partial class IcosahedronHalfEdgeMesh : HalfEdgeMesh {
	private float radius = 500f;
	[Export]
	public float Radius {
		get => radius;
		set {
			radius = value;
			Invalidate();
		}
	}
	
	public override Aabb GetAABB() {
		return new Aabb(new Vector3(-radius, -radius, -radius),
			new Vector3(radius * 2f, radius * 2f, radius * 2f));
	}

	protected override void Build(out byte[] half_edge_bytes, out byte[] vertex_bytes) {
		const uint num_half_edges = 60;
		const uint num_verts = 12;
		
		NumHalfEdges = num_half_edges;
		var halfEdgeBuffer = new HalfEdge[num_half_edges];
		var vertBuffer = new Vector3[num_verts];
		NumVerts = num_verts;
		BuildShape(halfEdgeBuffer, vertBuffer);
		
		const uint half_edge_array_size = num_half_edges * 24;
		const uint vert_buffer_size = 12 * num_verts;

		half_edge_bytes = new byte[8 + half_edge_array_size];
		vertex_bytes = new byte[vert_buffer_size];
		uint[] first_two = [num_half_edges, num_verts];
		Buffer.BlockCopy(first_two, 0, half_edge_bytes, 0, 8);
		Buffer.BlockCopy(PackHalfEdges(halfEdgeBuffer), 0, half_edge_bytes, 8, (int) half_edge_array_size);
		Buffer.BlockCopy(PackVector3s(vertBuffer), 0, vertex_bytes, 0, (int) vert_buffer_size);
	}

	private void BuildShape(HalfEdge[] halfEdgeBuffer, Vector3[] vertBuffer) {
		float w = float.Sqrt(4.0f * radius * radius / 3.617924f) / 2f;
		float l = 1.628f * w / 2f;
		float y_top = radius;
		float y_mid_upper = radius * 0.447213595357f;
		float y_mid_lower = -y_mid_upper;
		float y_bottom = -y_top;
		float xz_len = float.Sqrt(radius * radius - y_mid_upper * y_mid_upper);

		vertBuffer[0] = new Vector3(0f, y_top, 0f);
		for (var i = 0; i < 5; i++) {
			float x = xz_len * float.Cos(i * 2f / 5f * float.Pi);
			float z = xz_len * float.Sin(i * 2f / 5f * float.Pi);
			vertBuffer[i + 1] = new Vector3(x, y_mid_upper, z);
			int i_lower = i < 3 ? i + 8 : i + 3;
			vertBuffer[i_lower] = new Vector3(-x, y_mid_lower, -z);
		}

		vertBuffer[11] = new Vector3(0f, y_bottom, 0f);

		for (uint i = 0; i < 5; i++) {
			uint i_upper = i + 1;
			uint i_lower = i + 6;
			uint i_prev = i == 4 ? 0 : i + 1;
			BuildFace(i, 0, i + 1, i_prev + 1, halfEdgeBuffer);
			// upper, lower, upper
			BuildFace(i+5, i_upper, i_lower, i_prev + 1, halfEdgeBuffer);
			// lower, lower, upper
			BuildFace(i+10, i_lower, i_prev + 6, i_prev + 1, halfEdgeBuffer);
			BuildFace(i+15, i_prev + 6, i_lower, 11, halfEdgeBuffer);
		}

	}

	private void BuildFace(uint face, uint v0_index, uint v1_index, uint v2_index, HalfEdge[] halfEdgeBuffer) {
		uint[] verts = [v0_index, v1_index, v2_index];
		var edges = new uint[3];
		uint start_id = 3 * face;
		var twins = new int[3];

		int prev_face, prev_start_id;
		int next_face, next_start_id;
		int twin_face, twin_start_id;
		switch (face) {
			case < 5:
				prev_face = face == 0 ? 4 : (int) face - 1;
				prev_start_id = prev_face * 3;
				
				next_face = face == 4 ? 0 : (int)face + 1;
				next_start_id = next_face * 3;

				twin_face = (int) face + 5;
				twin_start_id = twin_face * 3;
				
				twins[0] = prev_start_id + 2;
				twins[1] = twin_start_id + 2;
				twins[2] = next_start_id;
				
				edges[0] = face;
				edges[1] = face + 5;
				edges[2] = face == 4 ? 0 : face + 1;
				break;
			case < 10:
				prev_face = face == 5 ? 14 : (int) face + 4;
				prev_start_id = prev_face * 3;
				
				next_face = face == 9 ? 14 : (int) face + 5;
				next_start_id = next_face * 3;
				
				
				
				twin_face = (int) face - 5;
				twin_start_id = twin_face * 3;
				
				twins[0] = prev_start_id + 1;
				twins[1] = next_start_id + 2;
				twins[2] = twin_start_id + 1;
				
				edges[0] = face + 5;
				edges[1] = face == 9 ? 10 : face + 6;
				edges[2] = face; 
				break;
			case < 15:
				prev_face = face == 14 ? 9 : (int) face - 5;
				prev_start_id = prev_face * 3;
				
				next_face = face == 14 ? 5 : (int)face - 4;
				next_start_id = next_face * 3;
				
				twin_face = (int) face + 5;
				twin_start_id = twin_face * 3;
				
				twins[0] = twin_start_id;
				twins[1] = next_start_id;
				twins[2] = prev_start_id + 1;
				
				edges[0] = face + 5;
				edges[1] = face == 14 ? 10 : face + 1;
				edges[2] = face; 
				break;
			default:
				prev_face = face == 15 ? 19 : (int) face - 1;
				prev_start_id = prev_face * 3;
				
				next_face = face == 19 ? 15 : (int)face + 1;
				next_start_id = next_face * 3;
				
				twin_face = (int) face - 5;
				twin_start_id = twin_face * 3;
				
				twins[0] = twin_start_id; 
				twins[1] = prev_start_id + 2;
				twins[2] = next_start_id + 1;
				
				edges[0] = face;
				edges[1] = 0;
				edges[2] = 0; 
				break;
		}
		
		

		
		for (uint i = 0; i < 3; i++) {
			uint curr_id = start_id + i;
			HalfEdge curr_he;
			curr_he.face = face;
			curr_he.edge = edges[i];
			curr_he.vert = verts[i];
			curr_he.twin = twins[i];
			curr_he.next = i == 2 ? start_id : curr_id + 1;
			curr_he.prev = i == 0 ? start_id + 2 : curr_id - 1;
			halfEdgeBuffer[curr_id] = curr_he;
		}
	}
}