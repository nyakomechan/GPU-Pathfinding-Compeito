using System.Collections.Generic;
using UnityEngine;

public class PathProcessor
{
    private SVOBuilder.SVOData svoData;
    private int wallLayerMask;

    public PathProcessor(SVOBuilder.SVOData svoData, int wallLayerMask = -1)
    {
        this.svoData = svoData;
        this.wallLayerMask = wallLayerMask;
    }

    public PathResult Process(PathNode[] pathData, int startIdx, int goalIdx, int iterationCount)
    {
        if (pathData == null || !svoData.IsValidLeaf(startIdx) || !svoData.IsValidLeaf(goalIdx))
        {
            return PathResult.Failed("Invalid path data or start/goal index.");
        }

        if (pathData[goalIdx].state < 2)
        {
            return PathResult.Failed("Goal was not reached.");
        }

        var leafPath = ReconstructPath(pathData, startIdx, goalIdx);
        if (leafPath == null || leafPath.Count == 0)
        {
            return PathResult.Failed("Failed to reconstruct path.");
        }

        var waypoints = ComputeWaypoints(leafPath);
        if (waypoints != null && waypoints.Count > 2)
        {
            waypoints = SmoothPath(waypoints);
        }

        return new PathResult
        {
            Success = true,
            ErrorMessage = null,
            LeafIndices = leafPath,
            Waypoints = waypoints,
            PathData = pathData,
            TotalCost = pathData[goalIdx].cost,
            IterationCount = iterationCount,
        };
    }

    public List<int> ReconstructPath(PathNode[] pathData, int startIdx, int goalIdx)
    {
        if (pathData == null || !svoData.IsValidLeaf(startIdx) || !svoData.IsValidLeaf(goalIdx))
            return null;

        var path = new List<int>();
        int current = goalIdx;
        var visited = new HashSet<int>();

        while (svoData.IsValidLeaf(current))
        {
            if (visited.Contains(current)) break;
            visited.Add(current);
            path.Add(current);
            if (current == startIdx) break;

            int parentIdx = pathData[current].parent;
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
}
