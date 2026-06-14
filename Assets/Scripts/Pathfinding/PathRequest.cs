using System;
using UnityEngine;

public class PathRequest
{
    public Vector3 StartWorld;
    public Vector3 GoalWorld;
    public Action<PathResult> OnComplete;

    public PathRequest() { }

    public PathRequest(Vector3 start, Vector3 goal, Action<PathResult> onComplete)
    {
        StartWorld = start;
        GoalWorld = goal;
        OnComplete = onComplete;
    }
}
