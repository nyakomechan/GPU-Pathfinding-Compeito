using System.Collections.Generic;
using UnityEngine;

public class PathResult
{
    public bool Success;
    public string ErrorMessage;
    public List<Vector3> Waypoints;
    public List<int> LeafIndices;
    public PathNode[] PathData;
    public float TotalCost;
    public int IterationCount;

    public static PathResult Failed(string message)
    {
        return new PathResult
        {
            Success = false,
            ErrorMessage = message,
            Waypoints = null,
            LeafIndices = null,
            PathData = null,
            TotalCost = float.PositiveInfinity,
            IterationCount = 0,
        };
    }
}
