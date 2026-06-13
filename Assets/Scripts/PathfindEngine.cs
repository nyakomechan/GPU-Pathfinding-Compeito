using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using imaginantia.Compeito;

public struct PathNode
{
    public float cost;
    public int state;
    public int parent;
    public int padding;
}

public class PathfindEngine
{
    private Material program;
    private int kernelWavefront;
    private int kernelGoalDetect;
    private int kernelReset;

    private Texture2D neighborTex;
    private Texture2D costTex;
    private RenderTexture pathDataA;
    private RenderTexture pathDataB;
    private RenderTexture goalResultTex;

    private int nodeCount;
    private int startIdx = -1;
    private int goalIdx = -1;
    private bool currentReadA = true;
    private int iteration;
    private bool goalReached;

    private SVOBuilder.SVOData svoData;
    private int wallLayerMask;

    private enum EngineState { Idle, Dispatched, GoalReadbackPending, PathReadbackPending }
    private EngineState state = EngineState.Idle;

    private AsyncGPUReadbackRequest goalReadbackRequest;
    private AsyncGPUReadbackRequest pathReadbackRequest;

    private static readonly int NODE_COUNT = Shader.PropertyToID("_NodeCount");
    private static readonly int START_INDEX = Shader.PropertyToID("_StartIndex");
    private static readonly int GOAL_INDEX = Shader.PropertyToID("_GoalIndex");
    private static readonly int PATH_DATA_IN = Shader.PropertyToID("_PathDataIn");
    private static readonly int NEIGHBORS = Shader.PropertyToID("_Neighbors");
    private static readonly int LEAF_COSTS = Shader.PropertyToID("_LeafCosts");

    public int Iteration => iteration;
    public bool GoalReached => goalReached;
    public int LeafCount => nodeCount;
    public SVOBuilder.SVOData SVOData => svoData;
    public bool IsBusy => state != EngineState.Idle;

    public void Init(Material program, SVOBuilder.SVOData data, int wallLayerMask = -1)
    {
        this.wallLayerMask = wallLayerMask;
        this.program = program;
        svoData = data;
        nodeCount = data.leafCount;

        kernelWavefront = program.FindPass("WavefrontExpand");
        kernelGoalDetect = program.FindPass("GoalDetect");
        kernelReset = program.FindPass("Reset");

        if (kernelWavefront < 0 || kernelGoalDetect < 0 || kernelReset < 0)
        {
            Debug.LogError("PathfindCompeito kernel not found. Ensure the .compeito file is imported correctly.");
        }

        neighborTex = new Texture2D(nodeCount, 2, TextureFormat.RGBAFloat, false);
        neighborTex.filterMode = FilterMode.Point;
        neighborTex.wrapMode = TextureWrapMode.Clamp;

        var neighborArr = new float[nodeCount * 2 * 4];
        for (int i = 0; i < nodeCount; i++)
        {
            neighborArr[(i + nodeCount * 0) * 4 + 0] = data.neighbors[i * 6 + 0];
            neighborArr[(i + nodeCount * 0) * 4 + 1] = data.neighbors[i * 6 + 1];
            neighborArr[(i + nodeCount * 0) * 4 + 2] = data.neighbors[i * 6 + 2];
            neighborArr[(i + nodeCount * 0) * 4 + 3] = data.neighbors[i * 6 + 3];
            neighborArr[(i + nodeCount * 1) * 4 + 0] = data.neighbors[i * 6 + 4];
            neighborArr[(i + nodeCount * 1) * 4 + 1] = data.neighbors[i * 6 + 5];
        }
        neighborTex.SetPixelData(neighborArr, 0);
        neighborTex.Apply(false);

        costTex = new Texture2D(nodeCount, 1, TextureFormat.RFloat, false);
        costTex.filterMode = FilterMode.Point;
        costTex.wrapMode = TextureWrapMode.Clamp;
        costTex.SetPixelData(data.leafCosts, 0);
        costTex.Apply(false);

        pathDataA = Compeito.CreateRT("PathDataA", nodeCount, 1, RenderTextureFormat.ARGBFloat);
        pathDataB = Compeito.CreateRT("PathDataB", nodeCount, 1, RenderTextureFormat.ARGBFloat);
        goalResultTex = Compeito.CreateRT("GoalResult", 1, 1, RenderTextureFormat.RFloat);

        program.SetTexture(NEIGHBORS, neighborTex);
        program.SetTexture(LEAF_COSTS, costTex);
        program.SetInt(NODE_COUNT, nodeCount);

        state = EngineState.Idle;
    }

    public void SetStart(int idx)
    {
        startIdx = idx;
    }

    public void SetGoal(int idx)
    {
        goalIdx = idx;
    }

    public void Reset()
    {
        iteration = 0;
        goalReached = false;
        currentReadA = true;
        state = EngineState.Idle;

        program.SetInt(START_INDEX, startIdx);
        program.SetInt(NODE_COUNT, nodeCount);
        Compeito.Dispatch(program, kernelReset, pathDataA);
        Compeito.Dispatch(program, kernelReset, pathDataB);
    }

    public void DispatchWavefront(int count)
    {
        if (startIdx < 0 || goalIdx < 0) return;
        if (goalReached) return;
        if (state != EngineState.Idle) return;

        program.SetInt(NODE_COUNT, nodeCount);
        program.SetInt(GOAL_INDEX, goalIdx);

        for (int i = 0; i < count; i++)
        {
            RenderTexture readBuf = currentReadA ? pathDataA : pathDataB;
            RenderTexture writeBuf = currentReadA ? pathDataB : pathDataA;

            program.SetTexture(PATH_DATA_IN, readBuf);
            Compeito.Dispatch(program, kernelWavefront, writeBuf);

            currentReadA = !currentReadA;
            iteration++;
        }

        RenderTexture currentBuf = currentReadA ? pathDataA : pathDataB;
        program.SetTexture(PATH_DATA_IN, currentBuf);
        program.SetInt(GOAL_INDEX, goalIdx);
        program.SetInt(NODE_COUNT, nodeCount);
        Compeito.Dispatch(program, kernelGoalDetect, goalResultTex);

        goalReadbackRequest = AsyncGPUReadback.Request(goalResultTex, 0, TextureFormat.RFloat);
        state = EngineState.GoalReadbackPending;
    }

    public bool UpdateReadback()
    {
        if (state == EngineState.GoalReadbackPending)
        {
            if (!goalReadbackRequest.done) return false;
            if (goalReadbackRequest.hasError)
            {
                state = EngineState.Idle;
                return false;
            }

            float result = goalReadbackRequest.GetData<float>()[0];
            goalReached = result > 0.5f;
            state = EngineState.Idle;
            return true;
        }

        if (state == EngineState.PathReadbackPending)
        {
            if (!pathReadbackRequest.done) return false;
            if (pathReadbackRequest.hasError)
            {
                state = EngineState.Idle;
                return true;
            }
            state = EngineState.Idle;
            return true;
        }

        return false;
    }

    public void RequestPathReadback()
    {
        RenderTexture currentBuf = currentReadA ? pathDataA : pathDataB;
        pathReadbackRequest = AsyncGPUReadback.Request(currentBuf, 0, TextureFormat.RGBAFloat);
        state = EngineState.PathReadbackPending;
    }

    public PathNode[] GetPathData()
    {
        if (state != EngineState.Idle) return null;
        if (!pathReadbackRequest.done || pathReadbackRequest.hasError) return null;

        var colors = pathReadbackRequest.GetData<Color>();
        var result = new PathNode[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            result[i].cost = colors[i].r;
            result[i].state = (int)colors[i].g;
            result[i].parent = (int)colors[i].b;
            result[i].padding = 0;
        }
        return result;
    }

    public List<int> ReconstructPath(PathNode[] data)
    {
        if (!goalReached || data == null) return null;

        var path = new List<int>();
        int current = goalIdx;
        var visited = new HashSet<int>();

        while (current >= 0 && current < nodeCount)
        {
            if (visited.Contains(current)) break;
            visited.Add(current);
            path.Add(current);
            if (current == startIdx) break;
            int parentIdx = data[current].parent;
            if (parentIdx < 0 || parentIdx == current) break;
            current = parentIdx;
        }

        path.Reverse();
        return path;
    }

    public List<Vector3> ComputeWaypoints(List<int> path)
    {
        if (path == null || path.Count == 0) return null;

        var waypoints = new List<Vector3>();
        var leafNodes = svoData.leafNodes;

        waypoints.Add(new Vector3(
            leafNodes[path[0]].leafX + 0.5f,
            leafNodes[path[0]].leafY + 0.5f,
            leafNodes[path[0]].leafZ + 0.5f));

        for (int i = 0; i < path.Count - 1; i++)
        {
            var a = leafNodes[path[i]];
            var b = leafNodes[path[i + 1]];
            waypoints.Add(new Vector3(
                (a.leafX + b.leafX) / 2f + 0.5f,
                (a.leafY + b.leafY) / 2f + 0.5f,
                (a.leafZ + b.leafZ) / 2f + 0.5f));
        }

        waypoints.Add(new Vector3(
            leafNodes[path[path.Count - 1]].leafX + 0.5f,
            leafNodes[path[path.Count - 1]].leafY + 0.5f,
            leafNodes[path[path.Count - 1]].leafZ + 0.5f));

        return waypoints;
    }

    public List<Vector3> SmoothPath(List<Vector3> waypoints)
    {
        if (waypoints == null || waypoints.Count <= 2) return waypoints;

        var result = new List<Vector3> { waypoints[0] };
        int current = 0;

        while (current < waypoints.Count - 1)
        {
            int farthest = current + 1;
            for (int candidate = waypoints.Count - 1; candidate > current + 1; candidate--)
            {
                if (IsLineClear(waypoints[current], waypoints[candidate]))
                {
                    farthest = candidate;
                    break;
                }
            }
            result.Add(waypoints[farthest]);
            current = farthest;
        }

        return result;
    }

    private bool IsLineClear(Vector3 from, Vector3 to)
    {
        const float radius = 0.5f;

        if (Physics.CheckSphere(from, radius, wallLayerMask)) return false;
        if (Physics.CheckSphere(to, radius, wallLayerMask)) return false;

        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.001f) return true;

        return !Physics.SphereCast(new Ray(from, dir.normalized), radius, dist, wallLayerMask);
    }

    public void Dispose()
    {
        Object.Destroy(neighborTex);
        Object.Destroy(costTex);
        if (pathDataA != null) pathDataA.Release();
        if (pathDataB != null) pathDataB.Release();
        if (goalResultTex != null) goalResultTex.Release();
        Object.Destroy(pathDataA);
        Object.Destroy(pathDataB);
        Object.Destroy(goalResultTex);
    }
}
