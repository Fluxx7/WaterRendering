using System.Threading.Tasks;
using Godot;

namespace GodotWaterRendering.assets.Scripts.Utility;
[GlobalClass]
public abstract partial class HalfEdgeMesh : Resource {
	public struct HalfEdge {
		public int twin;
		public uint next;
		public uint prev;
		public uint vert;
		public uint edge;
		public uint face;
	}
	private byte[] halfEdgeBytes, vertexBytes;
	private uint numHalfEdges, numVerts;

	public uint NumVerts {
		get {
			EnsureBuilt();
			return numVerts;
		}
		protected set => numVerts = value;
	}

	public uint NumHalfEdges {
		get {
			EnsureBuilt();
			return numHalfEdges;
		}
		protected set => numHalfEdges = value;
	}
	public byte[] HalfEdgeBytes {
		get {
			EnsureBuilt();
			return halfEdgeBytes;
		}
	}
	public byte[] VertexBytes {
		get {
			EnsureBuilt();
			return vertexBytes;
		}
	}

	private bool _dirty = true;
	


	protected void Invalidate() { _dirty = true; EmitChanged(); }

	private void EnsureBuilt() {
		if (!_dirty) return;
		_dirty = false;
		Build(out halfEdgeBytes, out vertexBytes);
	}

	protected abstract void Build(out byte[] half_edge_bytes, out byte[] vertex_bytes);

	public abstract Aabb GetAABB();

	protected static int[] PackHalfEdges(HalfEdge[] halfedges) {
		var HalfEdgesFlat = new int[6 * halfedges.Length];
		Parallel.For(0, halfedges.Length, index => {
			long flat_index = index * 6;
			HalfEdgesFlat[flat_index] = halfedges[index].twin;
			HalfEdgesFlat[flat_index + 1] = (int) halfedges[index].next;
			HalfEdgesFlat[flat_index + 2] = (int) halfedges[index].prev;
			HalfEdgesFlat[flat_index + 3] = (int) halfedges[index].vert;
			HalfEdgesFlat[flat_index + 4] = (int) halfedges[index].edge;
			HalfEdgesFlat[flat_index + 5] = (int) halfedges[index].face;
		});
		return HalfEdgesFlat;
	}
	
	protected static float[] PackVector3s(Vector3[] vectors) {
		var vectors_flat = new float[3 * vectors.Length];
		Parallel.For(0, vectors.Length, index => {
			long flat_index = index * 3;
			vectors_flat[flat_index] = vectors[index].X;
			vectors_flat[flat_index + 1] = vectors[index].Y;
			vectors_flat[flat_index + 2] = vectors[index].Z;
		});
		return vectors_flat;
	}


}