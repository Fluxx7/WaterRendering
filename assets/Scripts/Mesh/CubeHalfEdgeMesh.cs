using System;
using System.Threading.Tasks;
using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;

[GlobalClass, Tool]
public partial class CubeHalfEdgeMesh : HalfEdgeMesh {
	private float sidelength = 500f;

	[Export]
	public float Sidelength {
		get => sidelength;
		set {
			sidelength = value;
			Invalidate();
		}
	}
	
	public override Aabb GetAABB() {
		return new Aabb(new Vector3(-sidelength * 0.5f, -sidelength * 0.5f, -sidelength * 0.5f),
			new Vector3(sidelength, sidelength, sidelength));
	}

	protected override void Build(out byte[] half_edge_bytes, out byte[] vertex_bytes) {
		const uint num_half_edges = 24;
		const uint num_verts = 8;
		
		NumHalfEdges = num_half_edges;
		var halfEdgeBuffer = new HalfEdge[num_half_edges];
		var vertBuffer = new Vector3[num_verts];
		NumVerts = num_verts;
		for (uint x = 0; x < 6; x++) {
			BuildFace(x, halfEdgeBuffer, vertBuffer);
		}
		
		const uint half_edge_array_size = num_half_edges * 24;
		const uint vert_buffer_size = 12 * num_verts;

		half_edge_bytes = new byte[8 + half_edge_array_size];
		vertex_bytes = new byte[vert_buffer_size];
		uint[] first_two = [num_half_edges, num_verts];
		Buffer.BlockCopy(first_two, 0, half_edge_bytes, 0, 8);
		Buffer.BlockCopy(PackHalfEdges(halfEdgeBuffer), 0, half_edge_bytes, 8, (int) half_edge_array_size);
		Buffer.BlockCopy(PackVector3s(vertBuffer), 0, vertex_bytes, 0, (int) vert_buffer_size);
	}

	private void BuildFace(uint face, HalfEdge[] halfEdgeBuffer, Vector3[] vertBuffer) {
		var verts = new uint[4];
		var edges = new uint[4];
		var twins = new int[4];
		
		float half_size = sidelength * 0.5f;
		switch (face) {
			case 0: {
				verts[0] = 0;
				verts[1] = 1;
				verts[2] = 2;	
				verts[3] = 3;
			
				edges[0] = 0;
				edges[1] = 1;
				edges[2] = 2;
				edges[3] = 3;
			
				twins[0] = 8; // 0
				twins[1] = 12; // 1
				twins[2] = 16; // 2
				twins[3] = 4; // 3
			
				var v0 = new Vector3(half_size, half_size, half_size);
				var v1 = new Vector3(-half_size, half_size, half_size);
				var v2 = new Vector3(-half_size, half_size, -half_size);
				var v3 = new Vector3(half_size, half_size, -half_size);
				vertBuffer[0] = v0;
				vertBuffer[1] = v1;
				vertBuffer[2] = v2;
				vertBuffer[3] = v3;
				break;
			}
			case 1:
				verts[0] = 0;
				verts[1] = 3;
				verts[2] = 7;	
				verts[3] = 4;
			
				edges[0] = 3;
				edges[1] = 7;
				edges[2] = 11;
				edges[3] = 4;
			
				twins[0] = 3; // 4
				twins[1] = 19; // 5
				twins[2] = 20; // 6
				twins[3] = 9; // 7
				break;
			case 2:
				verts[0] = 1;
				verts[1] = 0;
				verts[2] = 4;	
				verts[3] = 5;
			
				edges[0] = 0;
				edges[1] = 4;
				edges[2] = 8;
				edges[3] = 5;
			
				twins[0] = 0; // 8
				twins[1] = 7; // 9
				twins[2] = 23; // 10
				twins[3] = 13; // 11
				break;
			case 3:
				verts[0] = 2;
				verts[1] = 1;
				verts[2] = 5;	
				verts[3] = 6;
			
				edges[0] = 1;
				edges[1] = 5;
				edges[2] = 9;
				edges[3] = 6;
			
				twins[0] = 1; // 12
				twins[1] = 11; // 13
				twins[2] = 22; // 14
				twins[3] = 17; // 15
				break;
			case 4:
				verts[0] = 3;
				verts[1] = 2;
				verts[2] = 6;	
				verts[3] = 7;
			
				edges[0] = 2;
				edges[1] = 6;
				edges[2] = 10;
				edges[3] = 7;
			
				twins[0] = 2; // 16
				twins[1] = 15; // 17
				twins[2] = 21; // 18
				twins[3] = 5; // 19
				break;
			case 5: {
				verts[0] = 4;
				verts[1] = 7;
				verts[2] = 6;	
				verts[3] = 5;
			
				edges[0] = 8;
				edges[1] = 9;
				edges[2] = 10;
				edges[3] = 11;
			
				twins[0] = 6; // 20
				twins[1] = 18; // 21
				twins[2] = 14; // 22
				twins[3] = 10; // 23
			
				var v4 = new Vector3(half_size, -half_size, half_size);
				var v5 = new Vector3(-half_size, -half_size, half_size);
				var v6 = new Vector3(-half_size, -half_size, -half_size);
				var v7 = new Vector3(half_size, -half_size, -half_size);
				vertBuffer[4] = v4;
				vertBuffer[5] = v5;
				vertBuffer[6] = v6;
				vertBuffer[7] = v7;
				break;
			}
		}

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
	}
}