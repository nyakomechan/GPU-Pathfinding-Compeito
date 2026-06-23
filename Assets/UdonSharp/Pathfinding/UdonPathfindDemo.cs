using UdonSharp;
using UnityEngine;
using TMPro;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UdonPathfindDemo : UdonSharpBehaviour
{
    [Header("Pathfinding")]
    public UdonPathfindingManager manager;
    public Vector3 start = new Vector3(1f, 1f, 1f);
    public Vector3 goal = new Vector3(14f, 14f, 14f);

    [Header("Auto Trigger")]
    public bool triggerOnStart = true;
    public int startDelayFrames = 60;

    [Header("Manual Trigger")]
    public KeyCode manualTriggerKey = KeyCode.Space;

    [Header("Queue Test")]
    public bool testQueue = false;

    private int frameCount;
    private bool triggered;

    [SerializeField]
    TMPro.TMP_Text debugText;
    void Start()
    {
        if (manager != null)
        {
            manager.resultReceiver = this;
        }
        else
        {
            Debug.LogWarning("[UdonPathfindDemo] manager is not assigned");
        }
    }

    void Update()
    {
        if (manager == null) return;

        if (triggerOnStart && !triggered)
        {
            frameCount++;
            if (frameCount >= startDelayFrames)
            {
                triggered = true;
                RequestDemoPath();
            }
        }

    }

    public void RequestDemoPath()
    {
        if (manager == null)
        {
            Debug.LogWarning("[UdonPathfindDemo] manager is not assigned");
            return;
        }

        if (testQueue)
        {
            Debug.Log("[UdonPathfindDemo] Requesting 3 queued paths");
            manager.RequestPath(start, goal);
            manager.RequestPath(start, new Vector3(goal.x, goal.y, 1f));
            manager.RequestPath(new Vector3(goal.x, 1f, goal.z), goal);
        }
        else
        {
            Debug.Log(string.Format("[UdonPathfindDemo] Requesting path: {0} -> {1}", start, goal));
            debugText.text = string.Format("[UdonPathfindDemo] Requesting path: {0} -> {1}", start, goal);
            manager.RequestPath(start, goal);
        }
    }

    public void OnPathFound()
    {
        if (manager == null) return;

        Debug.Log(string.Format("[UdonPathfindDemo] Path found! waypoints={0}", manager.waypoints != null ? manager.waypoints.Length : 0));
        if (manager.waypoints != null)
        {
            for (int i = 0; i < manager.waypoints.Length; i++)
            {
                Debug.Log(string.Format("[UdonPathfindDemo] waypoint[{0}] = {1}", i, manager.waypoints[i]));
                debugText.text = string.Format("waypoint[{0}] = {1}\n", i, manager.waypoints[i]);
            }
        }
    }

    public void OnPathFailed()
    {
        if (manager == null) return;

        Debug.LogWarning(string.Format("[UdonPathfindDemo] Path failed: {0}", manager.pathError));
        debugText.text = string.Format("[UdonPathfindDemo] Path failed: {0}", manager.pathError);
    }

    public override void Interact()
    {
        Debug.Log("[UdonPathfindDemo] Interact called, requesting path");
        RequestDemoPath();
    }
}
