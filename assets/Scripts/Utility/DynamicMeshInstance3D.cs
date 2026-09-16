using System;
using FluxxiShaderLang;
using Godot;
using Godot.Collections;
using Range = System.Range;

namespace GodotWaterRendering.assets.Scripts.Utility;

[GlobalClass]
public partial class DynamicMeshInstance3D : GeometryInstance3D {
	protected readonly MeshRD Mesh = new();

	private FSLFile cbTreeFile = FSLFile.FromFile("res://assets/Shaders/Compute/FSL/mesh/cbtree_kernels.fsl");
	private ComputeGroup cbtGroup;
	private ComputePlan cbtreeUpdate = new();
	private ComputeKernel bisectKernel;
	private FSLVertexBuffer vertBuffer;
	private FSLIndexBuffer indexBuffer;
	private FSLStorageBuffer dispatchBuffer;
	private FSLStorageBuffer commandBuffer;
	private FSLStorageBuffer bisectorBuffer, bisectorIDs, bisectorNeighbors, bisectorNeighborsCopy, bisectorIndices;
	private FSLStorageBuffer cbTreeBuffer, cbtDataBuffer, halfEdgeBuffer;
	private FSLStorageBuffer splitBisectors, allocatingBisectors, mergeBisectors, simplifyingBisectors, propagatingBisectors, modifiedBisectors;
	private uint numVertices, numIndices;
	private Rid vertBufferId, indexBufferId, commandBufferId;
	private bool rebuildQueued = false;

	private bool needsInit = true;
	private bool _cbtSizeDirty = false;

	private uint maxDepth = 16;
	private uint _baseSubdivisions = 16;
	public bool update = true;

	[Export(PropertyHint.Range, "1, 64,")] public uint UpdatesPerFrame = 1;

	[Export] public Vector2 Size = new(500f, 500f);
	
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
	
	private uint surfaceFormat = (uint) (Godot.Mesh.ArrayFormat.FormatVertex | Godot.Mesh.ArrayFormat.FormatIndex);
	private uint surfacePrimitiveType = (uint) Godot.Mesh.PrimitiveType.Triangles;
	
	public override void _EnterTree() {
		SetBase(Mesh.GetRid());
		RenderingServer.Singleton.CallOnRenderThread(new Callable(this, MethodName.RenderThreadInit));
	}

	private void RenderThreadInit() {
		if (!needsInit) return;
		needsInit = false;
		InitCBTrees();
		ConnectFrameDriver();
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

	private void OnFramePreDraw() {
		if (needsInit) return;
		if (rebuildQueued) UpdateParams();
		if (update) UpdateCBTrees();
	}
	
	private void UpdateParams() {
		RebuildRootMesh();
		if (_cbtSizeDirty) {
			RebuildCBTGenPlan();
			_cbtSizeDirty = false;
		}

		rebuildQueued = false;
	}
	
	private void RebuildRootMesh() {
		uint depth = CbtDepth;
		// 3 verts and indices per active bisector
		uint maxBisectorCount = 1u << (int)depth;
		uint num_faces = BaseSubdivisions * BaseSubdivisions;
		uint rootBisectorCount = 4 * num_faces;
		if (depth > maxDepth)
			GD.PushWarning(
				$"CBTreeMesh: {BaseSubdivisions} base subdivisions need {rootBisectorCount} root bisectors, " +
				$"raising the effective CBT depth from {maxDepth} to {depth}.");
		
		// minimum 65536 + 1 to ensure that indices are 32-bit, I don't feel like checking manually both here and in the shader right now
		// the root centroid vertex for each face is stored at maxBisectorCount + face_index, all other root vertices
		// are stored at the index corresponding to their vertex number
		numVertices = Math.Max(66537, maxBisectorCount + num_faces); 
		numIndices = Math.Max(66537, maxBisectorCount * 3);

		vertBuffer.SetVertexCount(numVertices);
		vertBuffer.SetVertexSizeBytes(12);
		indexBuffer.SetIndexCount(numIndices);
		indexBuffer.SetIndexFormat(RenderingDevice.IndexBufferFormat.Uint32);

		bisectorBuffer = cbtGroup.GetStorageBuffer("BisectorBuffer");
		bisectorBuffer.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(bisectorBuffer, "BisectorBuffer");
		
		bisectorNeighbors = cbtGroup.GetStorageBuffer("NeighborsBuffer");
		bisectorNeighbors.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(bisectorNeighbors, "NeighborsCopyBuffer");
		
		
		bisectorNeighborsCopy = cbtGroup.GetStorageBuffer("NeighborsCopyBuffer");
		bisectorNeighborsCopy.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(bisectorNeighborsCopy, "NeighborsBuffer");
		
		bisectorIDs = cbtGroup.GetStorageBuffer("BisectorIDBuffer");
		bisectorIDs.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(bisectorIDs, "BisectorIDBuffer");
		
		splitBisectors = cbtGroup.GetStorageBuffer("SplitBisectors");
		splitBisectors.SetUnsizedElementCount(maxBisectorCount);
		
		allocatingBisectors = cbtGroup.GetStorageBuffer("AllocatingBisectors");
		allocatingBisectors.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(allocatingBisectors, "AllocatingBisectors");
		
		mergeBisectors = cbtGroup.GetStorageBuffer("MergeBisectors");
		mergeBisectors.SetUnsizedElementCount(maxBisectorCount);
		
		simplifyingBisectors = cbtGroup.GetStorageBuffer("SimplifyingBisectors");
		simplifyingBisectors.SetUnsizedElementCount(maxBisectorCount);
		
		propagatingBisectors = cbtGroup.GetStorageBuffer("PropagatingBisectors");
		propagatingBisectors.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(propagatingBisectors, "PropagatingBisectors");
		
		modifiedBisectors = cbtGroup.GetStorageBuffer("ModifiedBisectors");
		modifiedBisectors.SetUnsizedElementCount(maxBisectorCount);
		
		bisectorIndices = cbtGroup.GetStorageBuffer("BisectorIndicesBuffer");
		bisectorIndices.SetUnsizedElementCount(maxBisectorCount);
		bisectKernel.AssignResource(bisectorIndices, "BisectorIndicesBuffer");
		
		cbTreeBuffer = cbtGroup.GetStorageBuffer("CBTreeBuffer");
		cbTreeBuffer.SetUnsizedElementCount(maxBisectorCount * 2);
		bisectKernel.AssignResource(cbTreeBuffer, "CBTreeBuffer");
		
		cbtDataBuffer = cbtGroup.GetStorageBuffer("CBTDataBuffer");
		bisectKernel.AssignResource(cbtDataBuffer, "CBTDataBuffer");
		
		halfEdgeBuffer = cbtGroup.GetStorageBuffer("HalfEdgeBuffer");
		halfEdgeBuffer.SetUnsizedElementCount(rootBisectorCount);
		
		cbtGroup.Dispatch("prepPipeline", 1,1 ,1);
		
		ComputePlan.MakeNew().AddKernel(cbtGroup.GetKernel("initCBTree"), maxBisectorCount * 2, 1, 1, new Dictionary<StringName, Variant> {
				{ "cbt_depth_in", depth }
			})
			.AddBarrier()
			.AddKernel(cbtGroup.GetKernel("initBuffers"), BaseSubdivisions, BaseSubdivisions, 1,
				new Dictionary<StringName, Variant> {
					{"sizeX", Size.X},
					{"sizeY", Size.Y},
					{ "edges_per_side", BaseSubdivisions }
				})
			.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("makeRootBisectors"), dispatchBuffer, 0)
			.Dispatch();
		for (uint i = 1; i <= CbtDepth; i++) {
			uint d = CbtDepth - i;
			var maxThreads = (uint)Math.Pow(2, d);
			cbtGroup.Dispatch("sumReduction", maxThreads, 1, 1, new Dictionary<StringName, Variant> {
				{"d", d},
				{"max_threads", maxThreads}
			});
		}
		
		Mesh.ClearSurfaces();
		
		Mesh.AddSurface(
			(Mesh.ArrayFormat) surfaceFormat,
			(Mesh.PrimitiveType) surfacePrimitiveType,
			(int)numVertices,
			vertBufferId,
			Engine.IsEditorHint() ? new Aabb(new Vector3(-Size.X * 0.5f, -1f, -Size.Y * 0.5f),
				new Vector3(Size.X, 1f, Size.Y)) : new Aabb(new Vector3(-Size.X * 0.5f, -1f, -Size.Y * 0.5f),
				new Vector3(Size.X, 100f, Size.Y)),
			indexCount: (int)numIndices,
			indexBuffer: indexBufferId,
			material: _surfaceMaterial,
			indirectBuffer: commandBufferId
		);
	}
	
	private void UpdateCBTrees() {
		Camera3D cam = GetViewport()?.GetCamera3D();
		if (cam is null) return;
		Vector3 localCam = GlobalTransform.AffineInverse() * cam.GlobalPosition;
		float viewportHeight = cam.GetViewport().GetVisibleRect().Size.Y;
		float projScale = 0.5f * viewportHeight * cam.GetCameraProjection().Y.Y;
		
		cbtGroup.DispatchWorkgroups("updateCamera", 1, 1, 1, new Dictionary<StringName, Variant> {
			{"cam_x", localCam.X},
			{"cam_y", localCam.Y},
			{"cam_z", localCam.Z},
			{"proj_scale", projScale}
		});
		for (var i = 0; i < UpdatesPerFrame; i++) {
			cbtreeUpdate.Dispatch();
		}
	}
	
	private void InitCBTrees() {
		bisectKernel = cbTreeFile.GetKernel("bisect");
		cbtGroup = cbTreeFile.GetKernelGroup();
		dispatchBuffer = cbtGroup.GetStorageBuffer("IndirectDispatchBuffer");
		cbtGroup.AssignResource(dispatchBuffer, "IndirectDispatchBuffer");
		bisectKernel.AssignResource(dispatchBuffer, "IndirectDispatchBuffer");
		
		vertBuffer = cbtGroup.GetVertexBuffer("VertexBuffer");
		indexBuffer = cbtGroup.GetIndexBuffer("IndexBuffer");
		commandBuffer = cbtGroup.GetStorageBuffer("IndirectIndexedDrawCommandBuffer");
		
		vertBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			vertBufferId = new_rid;
			rebuildQueued = true;
		}));
		indexBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			indexBufferId = new_rid;
			rebuildQueued = true;
		}));
		commandBuffer.ConnectAndCall(Callable.From((Rid new_rid) => { 
			commandBufferId = new_rid;
			rebuildQueued = true;
		}));
		
		
		RebuildRootMesh();
		RebuildCBTGenPlan();
		rebuildQueued = false;
	}

	private void RebuildCBTGenPlan() {
		cbtreeUpdate = ComputePlan.MakeNew();
		cbtreeUpdate.AddKernel(cbtGroup.GetKernel("prepPipeline"), 1, 1, 1)
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
			cbtreeUpdate.AddBarrier().AddKernel(cbtGroup.GetKernel("sumReduction"), maxThreads, 1, 1, new Dictionary<StringName, Variant> {
				{"d", d},
				{"max_threads", maxThreads}
			});
		}

		cbtreeUpdate.AddBarrier()
			.AddKernelIndirect(cbtGroup.GetKernel("prepDraw"), dispatchBuffer, 0);
	}
}