using System;
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

public class GpuPathfinder : IDisposable
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
    private int maxIterations;
    private SVOBuilder.SVOData svoData;

    private Action<PathNode[]> onComplete;

    private enum State { Idle, Running, GoalReadbackPending, PathReadbackPending }
    private State state = State.Idle;

    private AsyncGPUReadbackRequest goalReadbackRequest;
    private AsyncGPUReadbackRequest pathReadbackRequest;

    private static readonly int NODE_COUNT = Shader.PropertyToID("_NodeCount");
    private static readonly int START_INDEX = Shader.PropertyToID("_StartIndex");
    private static readonly int GOAL_INDEX = Shader.PropertyToID("_GoalIndex");
    private static readonly int PATH_DATA_IN = Shader.PropertyToID("_PathDataIn");
    private static readonly int NEIGHBORS = Shader.PropertyToID("_Neighbors");
    private static readonly int LEAF_COSTS = Shader.PropertyToID("_LeafCosts");
    private static readonly int LEAF_TEX_WIDTH = Shader.PropertyToID("_LeafTexWidth");

    private int texWidth;
    private int texHeight;

    public bool IsBusy => state != State.Idle;
    public int Iteration => iteration;
    public int LeafCount => nodeCount;
    public SVOBuilder.SVOData SVOData => svoData;
    public int ItersPerFrame { get; set; } = 8;

    public void Init(Material program, SVOBuilder.SVOData data)
    {
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

        texWidth = Mathf.CeilToInt(Mathf.Sqrt(nodeCount));
        texHeight = Mathf.CeilToInt((float)nodeCount / texWidth);
        int neighborHeight = texHeight * 2;

        neighborTex = new Texture2D(texWidth, neighborHeight, TextureFormat.RGBAFloat, false);
        neighborTex.filterMode = FilterMode.Point;
        neighborTex.wrapMode = TextureWrapMode.Clamp;

        var neighborArr = new Color[texWidth * neighborHeight];
        for (int i = 0; i < nodeCount; i++)
        {
            int tx = i % texWidth;
            int ty = i / texWidth;
            neighborArr[tx + (ty * 2 + 0) * texWidth] = new Color(
                data.neighbors[i * 6 + 0],
                data.neighbors[i * 6 + 1],
                data.neighbors[i * 6 + 2],
                data.neighbors[i * 6 + 3]);
            neighborArr[tx + (ty * 2 + 1) * texWidth] = new Color(
                data.neighbors[i * 6 + 4],
                data.neighbors[i * 6 + 5],
                0,
                0);
        }
        neighborTex.SetPixels(neighborArr);
        neighborTex.Apply(false);

        costTex = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false);
        costTex.filterMode = FilterMode.Point;
        costTex.wrapMode = TextureWrapMode.Clamp;
        var costArr = new Color[texWidth * texHeight];
        for (int i = 0; i < nodeCount; i++) costArr[i] = new Color(data.leafCosts[i], 0, 0, 0);
        costTex.SetPixels(costArr);
        costTex.Apply(false);

        pathDataA = Compeito.CreateRT("PathDataA", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
        pathDataB = Compeito.CreateRT("PathDataB", texWidth, texHeight, RenderTextureFormat.ARGBFloat);
        goalResultTex = Compeito.CreateRT("GoalResult", 1, 1, RenderTextureFormat.RFloat);

        program.SetTexture(NEIGHBORS, neighborTex);
        program.SetTexture(LEAF_COSTS, costTex);
        program.SetInt(NODE_COUNT, nodeCount);
        program.SetInt(LEAF_TEX_WIDTH, texWidth);

        state = State.Idle;
    }

    public void FindPath(int startIdx, int goalIdx, int maxIterations, Action<PathNode[]> onComplete)
    {
        if (IsBusy)
        {
            Debug.LogWarning("GpuPathfinder is already running. Request ignored.");
            onComplete?.Invoke(null);
            return;
        }

        if (startIdx < 0 || startIdx >= nodeCount || goalIdx < 0 || goalIdx >= nodeCount)
        {
            Debug.LogWarning($"Invalid start/goal index: start={startIdx}, goal={goalIdx}, leafCount={nodeCount}");
            onComplete?.Invoke(null);
            return;
        }

        this.startIdx = startIdx;
        this.goalIdx = goalIdx;
        this.maxIterations = Mathf.Max(1, maxIterations);
        this.onComplete = onComplete;

        iteration = 0;
        currentReadA = true;

        program.SetInt(START_INDEX, startIdx);
        program.SetInt(NODE_COUNT, nodeCount);
        program.SetInt(LEAF_TEX_WIDTH, texWidth);
        Compeito.Dispatch(program, kernelReset, pathDataA);
        Compeito.Dispatch(program, kernelReset, pathDataB);

        state = State.Running;
    }

    public void Cancel()
    {
        if (IsBusy)
        {
            Complete(null);
        }
    }

    public void Tick()
    {
        if (state == State.Idle) return;

        if (state == State.GoalReadbackPending)
        {
            if (!goalReadbackRequest.done) return;
            if (goalReadbackRequest.hasError)
            {
                Debug.LogError("GpuPathfinder: goal readback error");
                Complete(null);
                return;
            }

            float result = goalReadbackRequest.GetData<float>()[0];
            bool reached = result > 0.5f;

            if (reached)
            {
                RequestPathReadback();
                state = State.PathReadbackPending;
                return;
            }

            if (iteration >= maxIterations)
            {
                Complete(null);
                return;
            }

            state = State.Running;
        }
        else if (state == State.PathReadbackPending)
        {
            if (!pathReadbackRequest.done) return;
            if (pathReadbackRequest.hasError)
            {
                Debug.LogError("GpuPathfinder: path readback error");
                Complete(null);
                return;
            }

            Complete(GetPathData());
            return;
        }

        if (state == State.Running)
        {
            int remaining = Mathf.Max(0, maxIterations - iteration);
            int batch = Mathf.Min(ItersPerFrame, remaining);
            if (batch > 0)
            {
                DispatchWavefront(batch);
            }
            else
            {
                Complete(null);
            }
        }
    }

    private void DispatchWavefront(int count)
    {
        program.SetInt(NODE_COUNT, nodeCount);
        program.SetInt(GOAL_INDEX, goalIdx);
        program.SetInt(LEAF_TEX_WIDTH, texWidth);

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
        state = State.GoalReadbackPending;
    }

    private void RequestPathReadback()
    {
        RenderTexture currentBuf = currentReadA ? pathDataA : pathDataB;
        pathReadbackRequest = AsyncGPUReadback.Request(currentBuf, 0, TextureFormat.RGBAFloat);
        state = State.PathReadbackPending;
    }

    private PathNode[] GetPathData()
    {
        var colors = pathReadbackRequest.GetData<Color>();
        var result = new PathNode[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            Color c = colors[i];
            result[i].cost = c.r;
            result[i].state = (int)c.g;
            result[i].parent = (int)c.b;
            result[i].padding = 0;
        }
        return result;
    }

    private void Complete(PathNode[] data)
    {
        state = State.Idle;
        var callback = onComplete;
        onComplete = null;
        callback?.Invoke(data);
    }

    public void Dispose()
    {
        Cancel();

        if (neighborTex != null) UnityEngine.Object.Destroy(neighborTex);
        if (costTex != null) UnityEngine.Object.Destroy(costTex);
        if (pathDataA != null) { pathDataA.Release(); UnityEngine.Object.Destroy(pathDataA); }
        if (pathDataB != null) { pathDataB.Release(); UnityEngine.Object.Destroy(pathDataB); }
        if (goalResultTex != null) { goalResultTex.Release(); UnityEngine.Object.Destroy(goalResultTex); }
    }
}
