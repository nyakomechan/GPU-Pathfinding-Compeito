using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;
using imaginantia.Compeito;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UdonPathfindingManager : UdonSharpBehaviour
{
    [Header("Settings")]
    public Material pathfindMaterial;
    public BoxCollider[] wallColliders;
    public int gridSize = 16;
    public int wallLayer = 6;
    public float heightFactor = 0.2f;
    public int itersPerFrame = 8;
    public int maxIterations = 1024;

    [Header("Results")]
    public bool isBusy;
    public bool pathFound;
    public string pathError;
    public Vector3[] waypoints;

    [Header("Events")]
    public UdonSharpBehaviour resultReceiver;
    public string foundEventName = "OnPathFound";
    public string failedEventName = "OnPathFailed";

    private const int STATE_IDLE = 0;
    private const int STATE_RUNNING = 1;
    private const int STATE_GOAL_PENDING = 2;
    private const int STATE_PATH_PENDING = 3;

    private byte[] grid;
    public int[] voxelToLeaf;
    private int[] leafVoxelIndex;
    private int[] neighbors;
    private float[] leafCosts;
    public int leafCount;

    private RenderTexture pathDataA;
    private RenderTexture pathDataB;
    private RenderTexture goalResultTex;
    private Texture2D neighborTex;
    private Texture2D costTex;

    private int kernelWavefront = -1;
    private int kernelGoalDetect = -1;
    private int kernelReset = -1;

    private int texWidth;
    private int texHeight;

    public int startLeafIdx = -1;
    public int goalLeafIdx = -1;
    public Vector3 startWorld;
    public Vector3 goalWorld;
    public int searchIterationCount;
    public int searchFrameCount;
    private int currentIteration;
    private int state;
    private bool readA = true;

    private Color[] pathDataBuffer;
    private Color[] goalResultBuffer;

    private Vector3 requestStart;
    private Vector3 requestGoal;

    private int[] reconstructBuffer;
    private Vector3[] smoothBuffer;

    private int[] dirX = new int[] { 1, -1, 0, 0, 0, 0 };
    private int[] dirY = new int[] { 0, 0, 1, -1, 0, 0 };
    private int[] dirZ = new int[] { 0, 0, 0, 0, 1, -1 };

    void Start()
    {
        EnsureMaterial();
        Rebuild();
    }

    private void EnsureMaterial()
    {
        if (pathfindMaterial != null) return;

        Debug.LogError("UdonPathfindingManager: PathfindCompeito material is not assigned. Please assign the generated material in the Inspector.");
        enabled = false;
    }

    public void Rebuild()
    {
        if (pathfindMaterial == null)
        {
            EnsureMaterial();
        }

        DisposeGpuResources();

        BuildGrid();
        BuildVoxelData();
        InitGpu();

        isBusy = false;
        pathFound = false;
        pathError = "";
        state = STATE_IDLE;
    }

    private void BuildGrid()
    {
        int gs = gridSize;
        int total = gs * gs * gs;
        grid = new byte[total];

        if (wallColliders == null) return;

        for (int c = 0; c < wallColliders.Length; c++)
        {
            BoxCollider col = wallColliders[c];
            if (col == null) continue;

            Bounds b = col.bounds;
            int minX = Mathf.Clamp(Mathf.RoundToInt(b.min.x - 0.5f), 0, gs - 1);
            int minY = Mathf.Clamp(Mathf.RoundToInt(b.min.y - 0.5f), 0, gs - 1);
            int minZ = Mathf.Clamp(Mathf.RoundToInt(b.min.z - 0.5f), 0, gs - 1);
            int maxX = Mathf.Clamp(Mathf.RoundToInt(b.max.x - 0.5f), 0, gs - 1);
            int maxY = Mathf.Clamp(Mathf.RoundToInt(b.max.y - 0.5f), 0, gs - 1);
            int maxZ = Mathf.Clamp(Mathf.RoundToInt(b.max.z - 0.5f), 0, gs - 1);

            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                        grid[x + y * gs + z * gs * gs] = 1;
        }
    }

    private void BuildVoxelData()
    {
        int gs = gridSize;
        int total = gs * gs * gs;

        voxelToLeaf = new int[total];
        for (int i = 0; i < total; i++) voxelToLeaf[i] = -1;

        leafCount = 0;
        for (int i = 0; i < total; i++)
        {
            if (grid[i] == 0) leafCount++;
        }

        leafVoxelIndex = new int[leafCount];
        neighbors = new int[leafCount * 6];
        leafCosts = new float[leafCount];

        for (int i = 0; i < leafCount * 6; i++) neighbors[i] = -1;

        int li = 0;
        for (int z = 0; z < gs; z++)
            for (int y = 0; y < gs; y++)
                for (int x = 0; x < gs; x++)
                {
                    int vi = x + y * gs + z * gs * gs;
                    if (grid[vi] == 0)
                    {
                        voxelToLeaf[vi] = li;
                        leafVoxelIndex[li] = vi;
                        leafCosts[li] = 1.0f + heightFactor * y;
                        li++;
                    }
                }

        for (int i = 0; i < leafCount; i++)
        {
            int vi = leafVoxelIndex[i];
            int x = vi % gs;
            int y = (vi / gs) % gs;
            int z = vi / (gs * gs);

            for (int d = 0; d < 6; d++)
            {
                int nx = x + dirX[d];
                int ny = y + dirY[d];
                int nz = z + dirZ[d];
                if (nx >= 0 && nx < gs && ny >= 0 && ny < gs && nz >= 0 && nz < gs)
                {
                    int nvi = nx + ny * gs + nz * gs * gs;
                    neighbors[i * 6 + d] = voxelToLeaf[nvi];
                }
            }
        }
    }

    private void InitGpu()
    {
        if (pathfindMaterial == null) return;

        kernelWavefront = pathfindMaterial.FindPass("WavefrontExpand");
        kernelGoalDetect = pathfindMaterial.FindPass("GoalDetect");
        kernelReset = pathfindMaterial.FindPass("Reset");

        if (kernelWavefront < 0 || kernelGoalDetect < 0 || kernelReset < 0)
        {
            Debug.LogError("UdonPathfindingManager: kernel not found");
            return;
        }

        texWidth = Mathf.CeilToInt(Mathf.Sqrt(leafCount));
        texHeight = Mathf.CeilToInt((float)leafCount / texWidth);
        int neighborHeight = texHeight * 2;

        neighborTex = new Texture2D(texWidth, neighborHeight, TextureFormat.RGBAFloat, false);
        neighborTex.filterMode = FilterMode.Point;
        neighborTex.wrapMode = TextureWrapMode.Clamp;

        Color[] neighborArr = new Color[texWidth * neighborHeight];
        for (int i = 0; i < leafCount; i++)
        {
            int tx = i % texWidth;
            int ty = i / texWidth;
            neighborArr[tx + (ty * 2 + 0) * texWidth] = new Color(
                neighbors[i * 6 + 0],
                neighbors[i * 6 + 1],
                neighbors[i * 6 + 2],
                neighbors[i * 6 + 3]);
            neighborArr[tx + (ty * 2 + 1) * texWidth] = new Color(
                neighbors[i * 6 + 4],
                neighbors[i * 6 + 5],
                0,
                0);
        }
        neighborTex.SetPixels(neighborArr);
        neighborTex.Apply(false);

        costTex = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false);
        costTex.filterMode = FilterMode.Point;
        costTex.wrapMode = TextureWrapMode.Clamp;
        Color[] costArr = new Color[texWidth * texHeight];
        for (int i = 0; i < leafCount; i++) costArr[i] = new Color(leafCosts[i], 0, 0, 0);
        costTex.SetPixels(costArr);
        costTex.Apply(false);

        pathDataA = Compeito.CreateRT("PathDataA", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
        pathDataB = Compeito.CreateRT("PathDataB", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
        goalResultTex = Compeito.CreateRT("GoalResult", 1, 1, RenderTextureFormat.ARGBFloat);

        pathfindMaterial.SetTexture("_Neighbors", neighborTex);
        pathfindMaterial.SetTexture("_LeafCosts", costTex);
        pathfindMaterial.SetInt("_NodeCount", leafCount);
        pathfindMaterial.SetInt("_LeafTexWidth", texWidth);

        pathDataBuffer = new Color[texWidth * texHeight];
        goalResultBuffer = new Color[1];
        reconstructBuffer = new int[leafCount];
        smoothBuffer = new Vector3[leafCount];
    }

    public void RequestPath(Vector3 start, Vector3 goal)
    {
        if (isBusy) return;
        if (leafCount == 0)
        {
            pathFound = false;
            pathError = "No walkable voxels";
            NotifyFailed();
            return;
        }

        requestStart = start;
        requestGoal = goal;
        StartPathSearch();
    }

    private void StartPathSearch()
    {
        startWorld = requestStart;
        goalWorld = requestGoal;

        startLeafIdx = FindNearestEmptyLeaf(requestStart);
        goalLeafIdx = FindNearestEmptyLeaf(requestGoal);

        if (startLeafIdx < 0 || goalLeafIdx < 0)
        {
            pathFound = false;
            pathError = "Start or goal is not reachable";
            NotifyFailed();
            return;
        }

        ResetGpu();

        currentIteration = 0;
        readA = true;
        state = STATE_RUNNING;
        isBusy = true;
        pathFound = false;
        pathError = "";
        searchIterationCount = 0;
        searchFrameCount = 0;
    }

    private void ResetGpu()
    {
        pathfindMaterial.SetInt("_StartIndex", startLeafIdx);
        pathfindMaterial.SetInt("_NodeCount", leafCount);
        pathfindMaterial.SetInt("_LeafTexWidth", texWidth);
        Compeito.Dispatch(pathfindMaterial, kernelReset, pathDataA);
        Compeito.Dispatch(pathfindMaterial, kernelReset, pathDataB);
    }

    private void Update()
    {
        if (state != STATE_IDLE)
        {
            searchFrameCount++;
        }

        if (state == STATE_RUNNING)
        {
            Tick();
        }
    }

    private void Tick()
    {
        int remaining = maxIterations - currentIteration;
        int batch = Mathf.Min(itersPerFrame, remaining);

        if (batch <= 0)
        {
            FailSearch("Max iterations reached");
            return;
        }

        pathfindMaterial.SetInt("_NodeCount", leafCount);
        pathfindMaterial.SetInt("_GoalIndex", goalLeafIdx);
        pathfindMaterial.SetInt("_LeafTexWidth", texWidth);

        searchIterationCount += batch;

        for (int i = 0; i < batch; i++)
        {
            RenderTexture readBuf = readA ? pathDataA : pathDataB;
            RenderTexture writeBuf = readA ? pathDataB : pathDataA;

            pathfindMaterial.SetTexture("_PathDataIn", readBuf);
            Compeito.Dispatch(pathfindMaterial, kernelWavefront, writeBuf);

            readA = !readA;
            currentIteration++;
        }

        RenderTexture currentBuf = readA ? pathDataA : pathDataB;
        pathfindMaterial.SetTexture("_PathDataIn", currentBuf);
        pathfindMaterial.SetInt("_GoalIndex", goalLeafIdx);
        pathfindMaterial.SetInt("_NodeCount", leafCount);
        Compeito.Dispatch(pathfindMaterial, kernelGoalDetect, goalResultTex);

        VRCAsyncGPUReadback.Request(goalResultTex, 0, (IUdonEventReceiver)this);
        state = STATE_GOAL_PENDING;
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            FailSearch("GPU readback error");
            return;
        }

        if (state == STATE_GOAL_PENDING)
        {
            request.TryGetData(goalResultBuffer);
            bool goalReached = goalResultBuffer[0].r > 0.5f;

            if (goalReached)
            {
                RenderTexture currentBuf = readA ? pathDataA : pathDataB;
                VRCAsyncGPUReadback.Request(currentBuf, 0, (IUdonEventReceiver)this);
                state = STATE_PATH_PENDING;
            }
            else if (currentIteration >= maxIterations)
            {
                FailSearch("No path found");
            }
            else
            {
                state = STATE_RUNNING;
            }
        }
        else if (state == STATE_PATH_PENDING)
        {
            request.TryGetData(pathDataBuffer);
            ProcessPath();
        }
    }

    private void ProcessPath()
    {
        int pathLength = 0;
        int current = goalLeafIdx;

        while (current >= 0 && current < leafCount && pathLength < leafCount)
        {
            reconstructBuffer[pathLength] = current;
            pathLength++;

            if (current == startLeafIdx) break;

            int parent = (int)pathDataBuffer[current].b;
            if (parent < 0 || parent == current) break;
            current = parent;
        }

        if (pathLength == 0 || reconstructBuffer[pathLength - 1] != startLeafIdx)
        {
            FailSearch("Path reconstruction failed");
            return;
        }

        Vector3[] rawWaypoints = new Vector3[pathLength];
        for (int i = 0; i < pathLength; i++)
        {
            int leafIdx = reconstructBuffer[pathLength - 1 - i];
            rawWaypoints[i] = LeafToWorld(leafIdx, true);
        }

        waypoints = SmoothPath(rawWaypoints);

        isBusy = false;
        state = STATE_IDLE;
        pathFound = true;
        pathError = "";

        Debug.Log(string.Format("[UdonPathfindingManager] Path found: iterations={0}, frames={1}, waypoints={2}",
            searchIterationCount, searchFrameCount, waypoints != null ? waypoints.Length : 0));

        NotifyFound();
    }

    private Vector3[] SmoothPath(Vector3[] rawPath)
    {
        if (rawPath == null || rawPath.Length == 0) return new Vector3[0];
        if (rawPath.Length == 1) return new Vector3[] { rawPath[0] };

        int wallLayerMask = 1 << wallLayer;
        float radius = 0.25f;

        int smoothCount = 1;
        smoothBuffer[0] = rawPath[0];

        int current = 0;
        for (int i = 1; i < rawPath.Length; i++)
        {
            Vector3 from = smoothBuffer[smoothCount - 1];
            Vector3 to = rawPath[i];
            Vector3 dir = to - from;
            float dist = dir.magnitude;

            if (dist < 0.001f) continue;

            if (Physics.SphereCast(from, radius, dir.normalized, out RaycastHit hit, dist, wallLayerMask))
            {
                smoothBuffer[smoothCount] = rawPath[i - 1];
                smoothCount++;
                current = i - 1;
            }
        }

        smoothBuffer[smoothCount] = rawPath[rawPath.Length - 1];
        smoothCount++;

        Vector3[] result = new Vector3[smoothCount];
        for (int i = 0; i < smoothCount; i++) result[i] = smoothBuffer[i];
        return result;
    }

    private void FailSearch(string error)
    {
        isBusy = false;
        state = STATE_IDLE;
        pathFound = false;
        pathError = error;
        waypoints = new Vector3[0];

        Debug.LogWarning(string.Format("[UdonPathfindingManager] Path failed: {0} (iterations={1}, frames={2})",
            error, searchIterationCount, searchFrameCount));

        NotifyFailed();
    }

    private void NotifyFound()
    {
        if (resultReceiver != null && !string.IsNullOrEmpty(foundEventName))
        {
            resultReceiver.SendCustomEvent(foundEventName);
        }
    }

    private void NotifyFailed()
    {
        if (resultReceiver != null && !string.IsNullOrEmpty(failedEventName))
        {
            resultReceiver.SendCustomEvent(failedEventName);
        }
    }

    public int WorldToLeaf(Vector3 worldPos)
    {
        if (voxelToLeaf == null) return -1;

        int gs = gridSize;
        int x = Mathf.RoundToInt(worldPos.x);
        int y = Mathf.RoundToInt(worldPos.y);
        int z = Mathf.RoundToInt(worldPos.z);

        if (x < 0 || x >= gs || y < 0 || y >= gs || z < 0 || z >= gs) return -1;

        int vi = x + y * gs + z * gs * gs;
        return voxelToLeaf[vi];
    }

    public Vector3 LeafToWorld(int leafIdx, bool center)
    {
        if (leafIdx < 0 || leafIdx >= leafCount) return Vector3.zero;

        int gs = gridSize;
        int vi = leafVoxelIndex[leafIdx];
        int x = vi % gs;
        int y = (vi / gs) % gs;
        int z = vi / (gs * gs);

        if (center)
        {
            return new Vector3(x, y, z);
        }
        return new Vector3(x, y, z);
    }

    public int FindNearestEmptyLeaf(Vector3 worldPos)
    {
        int idx = WorldToLeaf(worldPos);
        if (idx >= 0) return idx;

        int gs = gridSize;
        int cx = Mathf.RoundToInt(worldPos.x);
        int cy = Mathf.RoundToInt(worldPos.y);
        int cz = Mathf.RoundToInt(worldPos.z);

        int searchRange = Mathf.Max(gs, Mathf.Max(gs, gs));
        for (int r = 1; r <= searchRange; r++)
        {
            for (int z = cz - r; z <= cz + r; z++)
                for (int y = cy - r; y <= cy + r; y++)
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (x < 0 || x >= gs || y < 0 || y >= gs || z < 0 || z >= gs) continue;
                        if (Mathf.Abs(x - cx) != r && Mathf.Abs(y - cy) != r && Mathf.Abs(z - cz) != r) continue;
                        int vi = x + y * gs + z * gs * gs;
                        int li = voxelToLeaf[vi];
                        if (li >= 0) return li;
                    }
        }

        return -1;
    }

    private void DisposeGpuResources()
    {
        if (neighborTex != null)
        {
            Destroy(neighborTex);
            neighborTex = null;
        }
        if (costTex != null)
        {
            Destroy(costTex);
            costTex = null;
        }
        if (pathDataA != null)
        {
            pathDataA.Release();
            Destroy(pathDataA);
            pathDataA = null;
        }
        if (pathDataB != null)
        {
            pathDataB.Release();
            Destroy(pathDataB);
            pathDataB = null;
        }
        if (goalResultTex != null)
        {
            goalResultTex.Release();
            Destroy(goalResultTex);
            goalResultTex = null;
        }
    }

    public void OnDestroy()
    {
        DisposeGpuResources();
    }
}
