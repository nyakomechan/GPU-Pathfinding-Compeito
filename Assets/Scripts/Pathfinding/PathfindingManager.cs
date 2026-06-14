using System;
using System.Collections.Generic;
using UnityEngine;

public class PathfindingManager : MonoBehaviour
{
    [SerializeField] private Material pathfindMaterial;
    [SerializeField] private int gridSize = 16;
    [SerializeField] private float heightFactor = 0.2f;
    [SerializeField] private int wallLayer = 6;
    [SerializeField] private int itersPerFrame = 8;
    [SerializeField] private int maxIterations = 1024;

    private VoxelWorld voxelWorld;
    private GpuPathfinder gpuPathfinder;
    private PathProcessor pathProcessor;

    private Queue<PathRequest> requestQueue = new Queue<PathRequest>();
    private PathRequest currentRequest;

    public bool IsBusy => gpuPathfinder != null && gpuPathfinder.IsBusy;
    public int LeafCount => voxelWorld?.LeafCount ?? 0;
    public VoxelWorld VoxelWorld => voxelWorld;

    private void Awake()
    {
        EnsureMaterial();
        EnsureVoxelWorld();
        InitializeGpuPathfinder();
    }

    private void Update()
    {
        if (gpuPathfinder == null) return;

        gpuPathfinder.Tick();

        if (!gpuPathfinder.IsBusy && currentRequest == null && requestQueue.Count > 0)
        {
            StartNextRequest();
        }
    }

    private void OnDestroy()
    {
        gpuPathfinder?.Dispose();
    }

    public void RequestPath(PathRequest request)
    {
        if (request == null || request.OnComplete == null) return;
        requestQueue.Enqueue(request);
    }

    public void RequestPath(Vector3 start, Vector3 goal, Action<PathResult> onComplete)
    {
        RequestPath(new PathRequest(start, goal, onComplete));
    }

    public void RebuildWorld()
    {
        voxelWorld?.Rebuild();
        InitializeGpuPathfinder();
    }

    private void EnsureMaterial()
    {
        if (pathfindMaterial != null) return;

#if UNITY_EDITOR
        pathfindMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Shaders/PathfindCompeito.compeito");
#endif

        if (pathfindMaterial == null)
        {
            Shader shader = Shader.Find("Compeito/Generated/PathfindCompeito");
            if (shader != null)
            {
                pathfindMaterial = new Material(shader);
            }
        }

        if (pathfindMaterial == null)
        {
            Debug.LogError("PathfindingManager: PathfindCompeito material not assigned! Please assign the .compeito asset in the Inspector.");
            enabled = false;
        }
    }

    private void EnsureVoxelWorld()
    {
        voxelWorld = GetComponent<VoxelWorld>();
        if (voxelWorld == null)
        {
            voxelWorld = gameObject.AddComponent<VoxelWorld>();
        }

        voxelWorld.Rebuild(gridSize, heightFactor, wallLayer);
    }

    private void InitializeGpuPathfinder()
    {
        gpuPathfinder?.Dispose();
        gpuPathfinder = new GpuPathfinder();
        gpuPathfinder.Init(pathfindMaterial, voxelWorld.SVOData);
        gpuPathfinder.ItersPerFrame = itersPerFrame;

        pathProcessor = new PathProcessor(voxelWorld.SVOData, voxelWorld.WallLayerMask);
    }

    private void StartNextRequest()
    {
        if (requestQueue.Count == 0) return;

        currentRequest = requestQueue.Dequeue();

        int startLeaf = voxelWorld.FindNearestEmptyLeaf(currentRequest.StartWorld);
        int goalLeaf = voxelWorld.FindNearestEmptyLeaf(currentRequest.GoalWorld);

        if (startLeaf < 0 || goalLeaf < 0)
        {
            CompleteRequest(PathResult.Failed("Start or goal position is not on a traversable leaf."));
            return;
        }

        gpuPathfinder.FindPath(startLeaf, goalLeaf, maxIterations, OnGpuPathComplete);
    }

    private void OnGpuPathComplete(PathNode[] pathData)
    {
        if (currentRequest == null) return;

        if (pathData == null)
        {
            CompleteRequest(PathResult.Failed("Path not found within max iterations."));
            return;
        }

        int startLeaf = voxelWorld.WorldToLeaf(currentRequest.StartWorld);
        int goalLeaf = voxelWorld.WorldToLeaf(currentRequest.GoalWorld);
        var result = pathProcessor.Process(pathData, startLeaf, goalLeaf, gpuPathfinder.Iteration);
        CompleteRequest(result);
    }

    private void CompleteRequest(PathResult result)
    {
        var request = currentRequest;
        currentRequest = null;
        request?.OnComplete?.Invoke(result);
    }
}
