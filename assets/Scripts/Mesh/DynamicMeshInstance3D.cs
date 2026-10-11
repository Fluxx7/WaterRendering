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
	private FSLVertexBuffer finalVertBuffer, custom0Buffer;
	private FSLIndexBuffer indexBuffer;
	private FSLStorageBuffer dispatchBuffer, vertexDispatchBuffer, halfedgeDispatchBuffer;
	private FSLStorageBuffer commandBuffer;
	private FSLStorageBuffer localVertBuffer, bisectorBuffer, bisectorIDs, bisectorNeighbors, bisectorNeighborsCopy, bisectorIndices, vertexIndices;
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
	private uint maxBisectorCount;
	
	protected FSLUniformBuffer cameraInfoBuffer;
	public bool Update = true;
	private bool customVertexKernel = false;

	[Export] public HalfEdgeMesh MeshData;

	[Export(PropertyHint.Range, "1, 64,")] public uint UpdatesPerFrame = 1;
	
	[Export]
	public uint MaxDepth {
		get => maxDepth;
		set {
			_cbtSizeDirty = true;
			maxDepth = value;
			rebuildQueued = true;
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
		PENDING_NUM_FACES,
		NUM_FACES_READY,
		MESH_DATA_NULL
	}

	private MeshLoadStage loadStage = MeshLoadStage.READY;

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
		if (loadStage == MeshLoadStage.READY) custom0Buffer.SetVertexCount(numVertices);
		custom0Buffer.ConnectAndCall(Callable.From((Rid new_rid) => {
			custom0BufferId = new_rid;
		}));
		useCustom0Buffer = true;
		if (loadStage == MeshLoadStage.READY) RelinkSurface();
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
		vertexKernel.AssignResource(localVertBuffer, "VertexInputBuffer");
		vertexKernel.AssignResource(finalVertBuffer, "VertexOutputBuffer");
		vertexKernel.AssignResource(cameraInfoBuffer, "CameraBuffer");
		vertexKernel.AssignResource(vertexIndices, "VertexCountBuffer");
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddPlan(cbtreeUpdateBase);
		cbtreeUpdate.AddBarrier().AddKernelIndirect(vertexKernel, vertexDispatchBuffer, 0);
	}

	private void UnlinkVertexKernel() {
		customVertexKernel = false;
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddPlan(cbtreeUpdateBase);
		cbtreeUpdate.AddBarrier().AddKernelIndirect(cbtGroup.GetKernel("defaultVertex"), vertexDispatchBuffer, 0);
	}

	private void UpdateCBTreeDepth() {
		uint depth = MaxDepth;
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
		LoadHalfEdgeMesh();
	}
	
	
	
	private void InitCBTrees() {
		bisectKernel = cbTreeFile.GetKernel("bisect");
		cbtGroup = cbTreeFile.GetKernelGroup();
		dispatchBuffer = cbtGroup.GetStorageBuffer("IndirectDispatchBuffer");
		bisectKernel.AssignResource(dispatchBuffer, "IndirectDispatchBuffer");
		vertexDispatchBuffer = cbtGroup.GetStorageBuffer("VertexDispatchBuffer");
		halfedgeDispatchBuffer = cbtGroup.GetStorageBuffer("LoadDispatchBuffer");
		
		finalVertBuffer = cbtGroup.GetVertexBuffer("VertexBuffer");
		localVertBuffer = cbtGroup.GetStorageBuffer("InternalVertexBuffer");
		maxBisectorCount = 1u << (int)MaxDepth;
		
		UpdateTriangleSizeBuffer();
		
		finalVertBuffer.SetVertexSizeBytes(12);
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
		
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		halfedgeVerts = cbtGroup.GetVertexBuffer("HalfEdgeVertexBuffer");
		halfedgeVerts.SetVertexSizeBytes(12);
		
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
		UpdateCBTreeDepth();
		MeshData.Changed += LoadHalfEdgeMesh;
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
		for (uint i = 1; i <= MaxDepth; i++) {
			uint d = MaxDepth - i;
			var maxThreads = (uint)Math.Pow(2, d);
			cbtreeUpdateBase.AddBarrier().AddKernel(cbtGroup.GetKernel("sumReduction"), maxThreads, 1, 1, new Dictionary<StringName, Variant> {
				{"d", d},
				{"max_threads", maxThreads}
			});
		}

		cbtreeUpdateBase.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("prepDraw"), dispatchBuffer, 0);
		
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddPlan(cbtreeUpdateBase);
		cbtreeUpdate.AddBarrier()
			.AddKernelIndirect(customVertexKernel ? vertexKernel : cbtGroup.GetKernel("defaultVertex"),
				vertexDispatchBuffer, 0);
	}

	private FSLVertexBuffer halfedgeVerts;

	private FSLStorageBuffer halfedgeFaceBuffer;
	private ComputePlan meshLoadInitPlan;

	private void InitMeshLoadPlan() {
		if (MeshData == null) {
			loadStage = MeshLoadStage.MESH_DATA_NULL;
			return;
		}
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
	}

	private uint numFaces;
	
	private void SeedRootBisectors() {
		ComputePlan.MakeNew().AddKernel(cbtGroup.GetKernel("loadHalfEdgeMesh"), numFaces, 1, 1)
			.AddKernelIndirect(cbtGroup.GetKernel("makeRootBisectors"), halfedgeDispatchBuffer, 0)
			.Dispatch();
		loadStage = MeshLoadStage.READY;
		
		for (uint i = 1; i <= MaxDepth; i++) {
			uint d = MaxDepth - i;
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
		localVertBuffer.SetUnsizedElementCount(numVertices);
		vertexIndices.SetUnsizedElementCount(numVertices);
		if (useCustom0Buffer) custom0Buffer.SetVertexCount(numVertices);
		loadStage = MeshLoadStage.NUM_FACES_READY;
	}
	
	private void LoadHalfEdgeMesh() {
		halfEdgeBuffer.SetUnsizedElementCount(MeshData.NumHalfEdges);
		halfEdgeBuffer.SetBufferBytes(MeshData.HalfEdgeBytes);
		halfedgeVerts.SetVertexCount(MeshData.NumVerts);
		halfedgeVerts.SetBufferBytes(MeshData.VertexBytes);
		halfedgeFaceBuffer.SetUnsizedElementCount(MeshData.NumHalfEdges);
		meshAabb = MeshData.GetAABB();
		
		loadStage = MeshLoadStage.PENDING_NUM_FACES;
		meshLoadInitPlan.Dispatch();
		halfedgeFaceBuffer.GetBufferValuesBytesAsync(Callable.From((byte[] vals) => SetNumFaces(vals)), 0, 4);
	}
	
}