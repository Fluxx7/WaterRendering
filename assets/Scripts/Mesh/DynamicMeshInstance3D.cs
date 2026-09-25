using System;
using FluxxiShaderLang;
using Godot;
using Godot.Collections;
using Range = System.Range;

namespace GodotWaterRendering.assets.Scripts.Utility;

[Tool]
[GlobalClass]
public partial class DynamicMeshInstance3D : GeometryInstance3D {
	protected readonly MeshRD Mesh = new();

	private FSLFile cbTreeFile = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/mesh/cbtree_kernels.fsl");
	private ComputeGroup cbtGroup;
	private ComputePlan cbtreeUpdateBase = new();
	private ComputePlan cbtreeUpdate = new();
	
	private ComputeKernel bisectKernel;
	private ComputeKernel vertexKernel;
	

	public ComputeKernel VertexKernel {
		get => vertexKernel;
		set {
			vertexKernel = value;
			relinkQueued = true;
		}
	}

	private bool useCustom0Buffer = false;
	private FSLVertexBuffer localVertBuffer, finalVertBuffer, custom0Buffer;
	private FSLIndexBuffer indexBuffer;
	private FSLStorageBuffer dispatchBuffer, vertexDispatchBuffer, halfedgeDispatchBuffer;
	private FSLStorageBuffer commandBuffer;
	private FSLStorageBuffer bisectorBuffer, bisectorIDs, bisectorNeighbors, bisectorNeighborsCopy, bisectorIndices, vertexIndices;
	private FSLStorageBuffer cbTreeBuffer, cbtDataBuffer, halfEdgeBuffer, vertexBitfieldBuffer;
	private FSLStorageBuffer splitBisectors, allocatingBisectors, mergeBisectors, simplifyingBisectors, propagatingBisectors;
	private uint numVertices, numIndices;
	private Rid vertBufferId, indexBufferId, commandBufferId, custom0BufferId;
	protected bool rebuildQueued = false;
	private bool relinkQueued = false;
	private Aabb meshAabb;
	
	private bool needsInit = true;
	private bool _cbtSizeDirty = false;

	private uint maxDepth = 16;
	private uint _baseSubdivisions = 16;
	private uint maxBisectorCount;
	
	protected FSLUniformBuffer cameraInfoBuffer;
	public bool Update = true;
	private bool customVertexKernel = false;

	[Export] public bool renderCube = false;

	[Export(PropertyHint.Range, "1, 64,")] public uint UpdatesPerFrame = 1;

	[Export] public Vector2 PlaneSize = new(500f, 500f);
	[Export] public float SphereRadius = 500f;
	
	[Export]
	public uint MaxDepth {
		get => maxDepth;
		set {
			_cbtSizeDirty = true;
			maxDepth = value;
			rebuildQueued = true;
		}
	}

	[Export(PropertyHint.Range, "0, 2048, 1")]
	public uint BaseSubdivisions {
		get => _baseSubdivisions;
		set {
			_baseSubdivisions = value;
			_cbtSizeDirty = true;
			rebuildQueued = true;
		}
	}
	
	private uint CbtDepth {
		get {
			var roots = 4ul * _baseSubdivisions * _baseSubdivisions;
			uint minDepth = 0;
			while ((1ul << (int)minDepth) < roots) minDepth++;
			return Math.Max(maxDepth, minDepth);
		}
	}

	private Material _surfaceMaterial;
	
	[Export]
	public Material SurfaceMaterial {
		get => _surfaceMaterial;
		set {
			_surfaceMaterial = value;
			if (Mesh.GetSurfaceCount() == 0) return;
			Mesh.SurfaceSetMaterial(0, _surfaceMaterial);
		}
	}
	
	private uint _triangleSize = 65;
	
	[Export]
	public uint TriangleSize {
		get => _triangleSize;
		set {
			_triangleSize = value;
			UpdateTriangleSizeBuffer();
		}
	}

	protected void RedisplaceVerts() {
		vertexKernel.DispatchIndirect(vertexDispatchBuffer, 0);
	}
	
	private uint surfacePrimitiveType = (uint) Godot.Mesh.PrimitiveType.Triangles;

	private Mesh.ArrayFormat GetSurfaceFormat() {
		Mesh.ArrayFormat format = Godot.Mesh.ArrayFormat.FormatVertex | Godot.Mesh.ArrayFormat.FormatIndex;
		if (useCustom0Buffer) {
			format |= Godot.Mesh.ArrayFormat.FormatCustom0;
			format |= (Mesh.ArrayFormat)((ulong)Godot.Mesh.ArrayCustomFormat.RgbaFloat
										 << (int)Godot.Mesh.ArrayFormat.FormatCustom0Shift);
		}
		return format;
	}
	
	public override void _EnterTree() {
		SetBase(Mesh.GetRid());
		RenderingServer.Singleton.CallOnRenderThread(new Callable(this, MethodName.RenderThreadInit));
	}

	private void RenderThreadInit() {
		if (!needsInit) return;
		needsInit = false;
		
		InitCBTrees();
		ConnectFrameDriver();
		if (relinkQueued) {
			RelinkVertexKernel();
			relinkQueued = false;
		}
	}
	
	public override void _ExitTree() {
		var cb = new Callable(this, MethodName.OnFramePreDraw);
		if (RenderingServer.Singleton.IsConnected(RenderingServer.SignalName.FramePreDraw, cb))
			RenderingServer.Singleton.Disconnect(RenderingServer.SignalName.FramePreDraw, cb);
		SetBase(default);
	}
	
	private void ConnectFrameDriver() {
		var cb = new Callable(this, MethodName.OnFramePreDraw);
		if (!RenderingServer.Singleton.IsConnected(RenderingServer.SignalName.FramePreDraw, cb))
			RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePreDraw, cb);
	}

	enum MeshLoadStage {
		READY,
		PENDING_NUM_HALFEDGES,
		NUM_HALFEDGES_READY,
		PENDING_NUM_FACES,
		NUM_FACES_READY
	}

	private MeshLoadStage loadStage = MeshLoadStage.READY;
	private uint numFaces, numHalfEdges;

	private void OnFramePreDraw() {
		if (needsInit) return;
		if (rebuildQueued) UpdateParams();
		if (Engine.IsEditorHint()) return;
		if (relinkQueued) {
			RelinkVertexKernel();
			relinkQueued = false;
		}
		
		if (loadStage != MeshLoadStage.READY) {
			switch (loadStage) {
				case MeshLoadStage.NUM_HALFEDGES_READY: {
					IndexHalfEdgeFaces();
				} break;
				case MeshLoadStage.NUM_FACES_READY: {
					SeedRootBisectors();
				} break;
				default:
					break;
			}
		} else if (Update) UpdateCBTrees();
		
	}

	private void UpdateTriangleSizeBuffer() {
		cbtGroup?.GetUniformBuffer("ClassificationTarget")?.SetBuffer(new Dictionary<StringName, Variant> {
			{"triangle_size", _triangleSize}
		});
	}
	
	protected void UpdateCBTrees() {
		if (loadStage != MeshLoadStage.READY) return;
		Camera3D cam = GetViewport()?.GetCamera3D();
		if (cam is null) return;
		Vector3 localCam = GlobalTransform.AffineInverse() * cam.GlobalPosition;
		float viewportHeight = cam.GetViewport().GetVisibleRect().Size.Y;
		Projection mvp = cam.GetCameraProjection() * new Projection(cam.GetCameraTransform().AffineInverse() * GlobalTransform);
		Basis toLocal = GlobalTransform.AffineInverse().Basis;
		Vector3 viewDir = -(toLocal * cam.GlobalTransform.Basis.Z).Normalized();
		
		cameraInfoBuffer.SetBuffer(new Dictionary<StringName, Variant> {
			{"camera_pos", localCam},
			{"screen_size", new Vector2(viewportHeight, cam.GetViewport().GetVisibleRect().Size.X)},
			{"view_vector", viewDir},
			{"modelToView", mvp},
			{"_padding1", 0f},
			{"_padding2", 0f}
		});
		for (var i = 0; i < UpdatesPerFrame; i++) {
			cbtreeUpdate.Dispatch();
		}
	}
	
	private void UpdateParams() {
		UpdateBufferSizes();
		if (_cbtSizeDirty) {
			RebuildCBTGenPlan();
			_cbtSizeDirty = false;
		}

		rebuildQueued = false;
	}

	private void RelinkVertexKernel() {
		if (vertexKernel == null) {
			UnlinkVertexKernel();
		} else {
			LinkVertexKernel();
		}
	}

	public void EnableCustom0Buffer(FSLVertexBuffer custom0_buffer) {
		custom0Buffer = custom0_buffer;
		custom0Buffer.SetVertexSizeBytes(16);
		custom0Buffer.SetVertexCount(numVertices);
		custom0Buffer.ConnectAndCall(Callable.From((Rid new_rid) => {
			custom0BufferId = new_rid;
		}));
		useCustom0Buffer = true;
		RelinkSurface();
	}

	private void RelinkSurface() {
		Mesh.ClearSurfaces();
		
		Mesh.AddSurface(
			GetSurfaceFormat(),
			(Mesh.PrimitiveType) surfacePrimitiveType,
			(int)numVertices,
			vertBufferId,
			meshAabb,
			useCustom0Buffer ? custom0BufferId : default,
			indexCount: (int)numIndices,
			indexBuffer: indexBufferId,
			material: _surfaceMaterial,
			indirectBuffer: commandBufferId
		);
	}
	
	private void LinkVertexKernel() {
		customVertexKernel = true;
		localVertBuffer.SetVertexCount(numVertices);
		finalVertBuffer.CopyTo(localVertBuffer);
		cbtGroup.AssignResource(localVertBuffer, "InternalVertexBuffer");
		vertexKernel.AssignResource(localVertBuffer, "VertexInputBuffer");
		vertexKernel.AssignResource(finalVertBuffer, "VertexOutputBuffer");
		vertexKernel.AssignResource(cameraInfoBuffer, "CameraBuffer");
		vertexKernel.AssignResource(vertexIndices, "VertexCountBuffer");
		cbtreeUpdate.AddBarrier().AddKernelIndirect(vertexKernel, vertexDispatchBuffer, 0);
	}

	private void UnlinkVertexKernel() {
		customVertexKernel = false;
		localVertBuffer.CopyTo(finalVertBuffer);
		cbtGroup.AssignResource(finalVertBuffer, "InternalVertexBuffer");
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddPlan(cbtreeUpdateBase);
	}

	private void UpdateCBTreeDepth() {
		uint depth = CbtDepth;
		maxBisectorCount = 1u << (int)depth;
		cbTreeBuffer.SetUnsizedElementCount(maxBisectorCount * 2);
		
		cbtGroup.Dispatch("prepPipeline", 1,1 ,1);
		cbtGroup.Dispatch("initCBTree", maxBisectorCount * 2, 1, 1, new Dictionary<StringName, Variant> {
			{ "cbt_depth_in", depth }
		});
		UpdateBufferSizes();
	}
	
	private void UpdateBufferSizes() {
		// minimum 65536 + 1 to ensure that indices are 32-bit, I don't feel like checking manually both here and in the shader right now
		// the root centroid vertex for each face is stored at maxBisectorCount + face_index, all other root vertices
		// are stored at the index corresponding to their vertex number
		numIndices = Math.Max(66537, maxBisectorCount * 3);
		indexBuffer.SetIndexCount(numIndices);

		
		bisectorBuffer.SetUnsizedElementCount(maxBisectorCount);
		bisectorNeighbors.SetUnsizedElementCount(maxBisectorCount);
		vertexBitfieldBuffer.SetUnsizedElementCount(maxBisectorCount);
		bisectorNeighborsCopy.SetUnsizedElementCount(maxBisectorCount);
		bisectorIDs.SetUnsizedElementCount(maxBisectorCount);
		splitBisectors.SetUnsizedElementCount(maxBisectorCount);
		allocatingBisectors.SetUnsizedElementCount(maxBisectorCount);
		mergeBisectors.SetUnsizedElementCount(maxBisectorCount);
		simplifyingBisectors.SetUnsizedElementCount(maxBisectorCount);
		propagatingBisectors.SetUnsizedElementCount(maxBisectorCount);
		bisectorIndices.SetUnsizedElementCount(maxBisectorCount);
		LoadHalfEdgeMesh(halfEdgeBuffer, halfedgeVerts);
	}
	
	
	
	private void InitCBTrees() {
		bisectKernel = cbTreeFile.GetKernel("bisect");
		cbtGroup = cbTreeFile.GetKernelGroup();
		dispatchBuffer = cbtGroup.GetStorageBuffer("IndirectDispatchBuffer");
		bisectKernel.AssignResource(dispatchBuffer, "IndirectDispatchBuffer");
		vertexDispatchBuffer = cbtGroup.GetStorageBuffer("VertexDispatchBuffer");
		halfedgeDispatchBuffer = cbtGroup.GetStorageBuffer("LoadDispatchBuffer");
		
		finalVertBuffer = cbtGroup.GetVertexBuffer("VertexBuffer");
		localVertBuffer = cbtGroup.GetVertexBuffer("InternalVertexBuffer");
		maxBisectorCount = 1u << (int)CbtDepth;
		
		UpdateTriangleSizeBuffer();
		
		finalVertBuffer.SetVertexSizeBytes(12);
		localVertBuffer.SetVertexSizeBytes(12);
		cbtGroup.AssignResource(finalVertBuffer, "InternalVertexBuffer");
		indexBuffer = cbtGroup.GetIndexBuffer("IndexBuffer");
		indexBuffer.SetIndexFormat(RenderingDevice.IndexBufferFormat.Uint32);
		commandBuffer = cbtGroup.GetStorageBuffer("IndirectIndexedDrawCommandBuffer");
		
		finalVertBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			vertBufferId = new_rid;
		}));
		indexBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			indexBufferId = new_rid;
		}));
		commandBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			commandBufferId = new_rid;
		}));
		
		vertexIndices = cbtGroup.GetStorageBuffer("VertexCountBuffer");
		bisectorBuffer = cbtGroup.GetStorageBuffer("BisectorBuffer");
		bisectorNeighbors = cbtGroup.GetStorageBuffer("NeighborsBuffer");
		vertexBitfieldBuffer = cbtGroup.GetStorageBuffer("VertexBitfieldBuffer");
		bisectorNeighborsCopy = cbtGroup.GetStorageBuffer("NeighborsCopyBuffer");
		bisectorIDs = cbtGroup.GetStorageBuffer("BisectorIDBuffer");
		splitBisectors = cbtGroup.GetStorageBuffer("SplitBisectors");
		allocatingBisectors = cbtGroup.GetStorageBuffer("AllocatingBisectors");
		mergeBisectors = cbtGroup.GetStorageBuffer("MergeBisectors");
		simplifyingBisectors = cbtGroup.GetStorageBuffer("SimplifyingBisectors");
		propagatingBisectors = cbtGroup.GetStorageBuffer("PropagatingBisectors");
		bisectorIndices = cbtGroup.GetStorageBuffer("BisectorIndicesBuffer");
		cbTreeBuffer = cbtGroup.GetStorageBuffer("CBTreeBuffer");
		cbtDataBuffer = cbtGroup.GetStorageBuffer("CBTDataBuffer");
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		cameraInfoBuffer = cbtGroup.GetUniformBuffer("CameraBuffer");
		
		bisectKernel.AssignResource(bisectorBuffer, "BisectorBuffer");
		bisectKernel.AssignResource(bisectorNeighbors, "NeighborsCopyBuffer");
		bisectKernel.AssignResource(bisectorNeighborsCopy, "NeighborsBuffer");
		bisectKernel.AssignResource(bisectorIDs, "BisectorIDBuffer");
		bisectKernel.AssignResource(allocatingBisectors, "AllocatingBisectors");
		bisectKernel.AssignResource(propagatingBisectors, "PropagatingBisectors");
		bisectKernel.AssignResource(bisectorIndices, "BisectorIndicesBuffer");
		bisectKernel.AssignResource(cbTreeBuffer, "CBTreeBuffer");
		bisectKernel.AssignResource(cbtDataBuffer, "CBTDataBuffer");
		
		InitMeshLoadPlan();
		RebuildCBTGenPlan();
		if (renderCube) BuildHalfEdgeCubeMesh();
		else BuildHalfEdgePlaneMesh();
		UpdateCBTreeDepth();
		rebuildQueued = false;
	}

	private void RebuildCBTGenPlan() {
		cbtreeUpdateBase = ComputePlan.MakeNew();
		cbtreeUpdateBase.AddKernel(cbtGroup.GetKernel("prepPipeline"), 1, 1, 1)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("classify"), dispatchBuffer, 0)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("split"), dispatchBuffer, 12)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("allocate"), dispatchBuffer, 24)
			.AddKernelIndirect(cbtGroup.GetKernel("copy"), dispatchBuffer, 36)
			.AddBarrier()
			.AddKernelIndirect(bisectKernel, dispatchBuffer, 48)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("propagateBisect"), dispatchBuffer, 60)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("prepareSimplify"), dispatchBuffer, 72)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("simplify"), dispatchBuffer, 84)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("propagateSimplify"), dispatchBuffer, 96);
		for (uint i = 1; i <= CbtDepth; i++) {
			uint d = CbtDepth - i;
			var maxThreads = (uint)Math.Pow(2, d);
			cbtreeUpdateBase.AddBarrier().AddKernel(cbtGroup.GetKernel("sumReduction"), maxThreads, 1, 1, new Dictionary<StringName, Variant> {
				{"d", d},
				{"max_threads", maxThreads}
			});
		}

		cbtreeUpdateBase.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("prepDraw"), dispatchBuffer, 0);
		if (customVertexKernel) {
			cbtreeUpdate.AddBarrier().AddKernelIndirect(vertexKernel, vertexDispatchBuffer, 0);
		}
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddPlan(cbtreeUpdateBase);
	}

	private FSLVertexBuffer halfedgeVerts;

	private FSLStorageBuffer halfedgeFaceBuffer;
	private ComputePlan meshLoadInitPlan;

	private void InitMeshLoadPlan() {
		halfedgeFaceBuffer = cbtGroup.GetStorageBuffer("HalfEdgeFaceBuffer");
		meshLoadInitPlan = ComputePlan.MakeNew();
		meshLoadInitPlan.AddKernelWorkgroups(cbtGroup.GetKernel("prepHalfEdgeLoad"), 1, 1, 1)
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("indexHalfEdgeFaces"), halfedgeDispatchBuffer, 0);
	}

	private void IndexHalfEdgeFaces() {
		loadStage = MeshLoadStage.PENDING_NUM_FACES;
		meshLoadInitPlan.Dispatch();
		halfedgeFaceBuffer.GetBufferValuesBytesAsync(Callable.From((byte[] vals) => SetNumFaces(vals)), 0, 4);
		halfedgeDispatchBuffer.GetBufferValuesBytesAsync(Callable.From((byte[] vals) => { 
			var dispatch_data_out = new uint[3];
			Buffer.BlockCopy(vals, 0, dispatch_data_out, 0, 12);
			GD.Print($"dispatch size: {dispatch_data_out[0]}x, {dispatch_data_out[1]}y, {dispatch_data_out[2]}z");
		}));
	}
	private void SeedRootBisectors() {
		ComputePlan.MakeNew().AddKernel(cbtGroup.GetKernel("loadHalfEdgeMesh"), numFaces, 1, 1)
			.AddKernelIndirect(cbtGroup.GetKernel("makeRootBisectors"), halfedgeDispatchBuffer, 0)
			.Dispatch();
		loadStage = MeshLoadStage.READY;
		
		for (uint i = 1; i <= CbtDepth; i++) {
			uint d = CbtDepth - i;
			var maxThreads = (uint)Math.Pow(2, d);
			cbtGroup.Dispatch("sumReduction", maxThreads, 1, 1, new Dictionary<StringName, Variant> {
				{"d", d},
				{"max_threads", maxThreads}
			});
		}
		cbtGroup.DispatchIndirect("prepDraw", dispatchBuffer, 0);
		
		RelinkSurface();
	}

	private void SetNumFaces(byte[] halfedge_face_buffer_vals) {
		var buffer_data_out = new uint[1];
		Buffer.BlockCopy(halfedge_face_buffer_vals, 0, buffer_data_out, 0, 4);
		numFaces = buffer_data_out[0];
		numVertices = Math.Max(66537, maxBisectorCount + numFaces); 
		finalVertBuffer.SetVertexCount(numVertices);
		if (customVertexKernel) {
			localVertBuffer.SetVertexCount(numVertices);
		}
		vertexIndices.SetUnsizedElementCount(numVertices);
		if (useCustom0Buffer) custom0Buffer.SetVertexCount(numVertices);
		GD.Print($"num_faces: {numFaces}");
		loadStage = MeshLoadStage.NUM_FACES_READY;
	}
	
	private void SetNumHalfEdges(byte[] halfedge_buffer_vals) {
		var buffer_data_out = new uint[1];
		Buffer.BlockCopy(halfedge_buffer_vals, 0, buffer_data_out, 0, 4);
		numHalfEdges = buffer_data_out[0];
		GD.Print($"num_halfedges: {numHalfEdges}");
		halfedgeFaceBuffer.SetUnsizedElementCount(numHalfEdges);
		loadStage = MeshLoadStage.NUM_HALFEDGES_READY;
	}
	
	private void LoadHalfEdgeMesh(FSLStorageBuffer halfedge_buffer, FSLVertexBuffer he_vert_buffer) {
		loadStage = MeshLoadStage.PENDING_NUM_HALFEDGES;
		cbtGroup.AssignResource(halfedge_buffer, "HalfEdgeBuffer");
		cbtGroup.AssignResource(he_vert_buffer, "HalfEdgeVertexBuffer");
		halfedge_buffer.GetBufferValuesBytesAsync(Callable.From((byte[] halfedge_buffer_data) => SetNumHalfEdges(halfedge_buffer_data)), 0, 4);
	}
	
	
	private void BuildHalfEdgePlaneMesh() {
		uint num_faces = BaseSubdivisions * BaseSubdivisions;
		uint num_halfedges = num_faces * 4;
		halfedgeVerts = cbtGroup.GetVertexBuffer("HalfEdgeVertexBuffer");
		halfedgeVerts.SetVertexSizeBytes(12);
		halfedgeVerts.SetVertexCount(num_halfedges);
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		halfEdgeBuffer.SetUnsizedElementCount(num_halfedges);
		
		cbtGroup.Dispatch("planeMeshHalfEdge", _baseSubdivisions, _baseSubdivisions, 1, new Dictionary<StringName, Variant> {
			{"sizeX", PlaneSize.X},
			{"sizeY", PlaneSize.Y},
			{ "edges_per_side", _baseSubdivisions}
		});

		meshAabb = Engine.IsEditorHint()
			? new Aabb(new Vector3(-PlaneSize.X * 0.5f, -1f, -PlaneSize.Y * 0.5f),
				new Vector3(PlaneSize.X, 1f, PlaneSize.Y))
			: new Aabb(new Vector3(-PlaneSize.X * 0.5f, -1f, -PlaneSize.Y * 0.5f),
				new Vector3(PlaneSize.X, 100f, PlaneSize.Y));
	}
	
	private void BuildHalfEdgeCubeMesh() {
		uint num_halfedges = 24;
		halfedgeVerts = cbtGroup.GetVertexBuffer("HalfEdgeVertexBuffer");
		halfedgeVerts.SetVertexSizeBytes(12);
		halfedgeVerts.SetVertexCount(num_halfedges);
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		halfEdgeBuffer.SetUnsizedElementCount(num_halfedges);
		
		cbtGroup.Dispatch("cubeMeshHalfEdge", 6, 1, 1, new Dictionary<StringName, Variant> {
			{"size", SphereRadius * 2f}
		});

		meshAabb = new Aabb(new Vector3(-SphereRadius, -SphereRadius, -SphereRadius),
			new Vector3(SphereRadius * 2f, SphereRadius * 2f, SphereRadius * 2f));
	}
	
	private void BuildHalfEdgeIcosahedronMesh() {
		uint num_faces = BaseSubdivisions * BaseSubdivisions;
		uint num_halfedges = num_faces * 4;
		halfedgeVerts = cbtGroup.GetVertexBuffer("HalfEdgeVertexBuffer");
		halfedgeVerts.SetVertexSizeBytes(12);
		halfedgeVerts.SetVertexCount(num_halfedges);
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		halfEdgeBuffer.SetUnsizedElementCount(num_halfedges);
		
		cbtGroup.Dispatch("icosahedronMeshHalfEdge", _baseSubdivisions, _baseSubdivisions, 1, new Dictionary<StringName, Variant> {
			{"sizeX", PlaneSize.X},
			{"sizeY", PlaneSize.Y},
			{ "edges_per_side", _baseSubdivisions}
		});
	}
	
}