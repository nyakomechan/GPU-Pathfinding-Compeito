using UnityEngine;

public class PathfindDemo : MonoBehaviour
{
    public PathfindingManager pathfindingManager;

    private PathfindVisualizer viz;
    private Camera cam;

    private int startLeafIdx = -1;
    private int goalLeafIdx = -1;
    private string clickMode = "start";
    private bool placeMode = false;
    private bool isDragging;
    private PathResult lastResult;

    private Vector3 camTarget;
    private float camDistance;
    private float camTheta;
    private float camPhi;

    private void Start()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var camObj = new GameObject("Main Camera");
            cam = camObj.AddComponent<Camera>();
            camObj.AddComponent<AudioListener>();
            camObj.tag = "MainCamera";
        }

        EnsurePathfindingManager();
        if (pathfindingManager == null || pathfindingManager.VoxelWorld == null)
        {
            Debug.LogError("PathfindDemo: PathfindingManager is not available.");
            enabled = false;
            return;
        }

        var svoData = pathfindingManager.VoxelWorld.SVOData;
        int gridSize = pathfindingManager.VoxelWorld.GridSize;

        camTarget = new Vector3(gridSize / 2f, gridSize / 2f, gridSize / 2f);
        camDistance = gridSize * 2.5f;
        camTheta = Mathf.PI / 4f;
        camPhi = Mathf.PI / 3f;
        UpdateCameraPosition();

        viz = gameObject.AddComponent<PathfindVisualizer>();
        viz.Init(svoData);

        startLeafIdx = FindDefaultLeaf(1, 1, 1, gridSize - 2, gridSize - 2, 1, 1, 1, 1);
        goalLeafIdx = FindDefaultLeaf(gridSize - 2, gridSize - 2, gridSize - 2, 1, 1, 1, -1, -1, -1);

        if (startLeafIdx < 0)
            startLeafIdx = FindDefaultLeaf(0, 0, 0, gridSize - 1, gridSize - 1, gridSize - 1, 1, 1, 1);
        if (goalLeafIdx < 0)
            goalLeafIdx = FindDefaultLeaf(gridSize - 1, gridSize - 1, gridSize - 1, 0, 0, 0, -1, -1, -1);

        viz.UpdateVisual(lastResult, startLeafIdx, goalLeafIdx);
    }

    private void EnsurePathfindingManager()
    {
        if (pathfindingManager != null) return;

        pathfindingManager = GetComponent<PathfindingManager>();
        if (pathfindingManager == null)
        {
            pathfindingManager = FindObjectOfType<PathfindingManager>();
        }

        if (pathfindingManager == null)
        {
            var managerObj = new GameObject("PathfindingManager");
            pathfindingManager = managerObj.AddComponent<PathfindingManager>();
        }
    }

    private void Update()
    {
        HandleCameraInput();
        HandleInput();
    }

    private void UpdateCameraPosition()
    {
        float x = camTarget.x + camDistance * Mathf.Sin(camPhi) * Mathf.Cos(camTheta);
        float y = camTarget.y + camDistance * Mathf.Cos(camPhi);
        float z = camTarget.z + camDistance * Mathf.Sin(camPhi) * Mathf.Sin(camTheta);
        cam.transform.position = new Vector3(x, y, z);
        cam.transform.LookAt(camTarget);
    }

    private int FindDefaultLeaf(int x0, int y0, int z0, int x1, int y1, int z1, int dx, int dy, int dz)
    {
        int gs = pathfindingManager.VoxelWorld.GridSize;
        int zs = dz > 0 ? z0 : z1;
        int ze = dz > 0 ? z1 : z0;
        int ys = dy > 0 ? y0 : y1;
        int ye = dy > 0 ? y1 : y0;
        int xs = dx > 0 ? x0 : x1;
        int xe = dx > 0 ? x1 : x0;

        for (int z = zs; z <= ze; z++)
            for (int y = ys; y <= ye; y++)
                for (int x = xs; x <= xe; x++)
                {
                    if (x < 0 || x >= gs || y < 0 || y >= gs || z < 0 || z >= gs) continue;
                    int vi = x + y * gs + z * gs * gs;
                    int leafIdx = pathfindingManager.VoxelWorld.SVOData.voxelToLeaf[vi];
                    if (leafIdx >= 0) return leafIdx;
                }

        return -1;
    }

    private void HandleCameraInput()
    {
        if (placeMode) return;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            camDistance *= 1f - scroll * 2f;
            camDistance = Mathf.Clamp(camDistance, 2f, pathfindingManager.VoxelWorld.GridSize * 10f);
            UpdateCameraPosition();
        }

        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(2))
        {
            if (!placeMode && Input.mousePosition.x > 230f)
            {
                isDragging = true;
            }
        }

        if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(2))
        {
            isDragging = false;
        }

        if (isDragging)
        {
            float dx = Input.GetAxis("Mouse X") * 0.01f;
            float dy = Input.GetAxis("Mouse Y") * 0.01f;
            camTheta -= dx * 3f;
            camPhi -= dy * 3f;
            camPhi = Mathf.Clamp(camPhi, 0.1f, Mathf.PI - 0.1f);
            UpdateCameraPosition();
        }
    }

    private void HandleInput()
    {
        if (placeMode && Input.GetMouseButtonDown(0))
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int leafIdx = viz.PickVoxel(ray);
            if (leafIdx >= 0)
            {
                if (clickMode == "start")
                    startLeafIdx = leafIdx;
                else
                    goalLeafIdx = leafIdx;

                lastResult = null;
                viz.UpdateVisual(lastResult, startLeafIdx, goalLeafIdx);
            }
        }

        if (Input.GetMouseButtonDown(1) && placeMode)
        {
            clickMode = clickMode == "start" ? "goal" : "start";
        }
    }

    private void RequestPath()
    {
        if (startLeafIdx < 0 || goalLeafIdx < 0) return;
        if (pathfindingManager.IsBusy) return;

        pathfindingManager.RequestPath(
            pathfindingManager.VoxelWorld.LeafToWorld(startLeafIdx),
            pathfindingManager.VoxelWorld.LeafToWorld(goalLeafIdx),
            OnPathComplete);
    }

    private void OnPathComplete(PathResult result)
    {
        lastResult = result;
        viz.UpdateVisual(result, startLeafIdx, goalLeafIdx);
    }

    private void OnGUI()
    {
        int x = 10, y = 10, w = 230, h = 300;
        GUI.Box(new Rect(x, y, w, h), "");

        GUILayout.BeginArea(new Rect(x + 10, y + 10, w - 20, h - 20));

        GUILayout.Label("<b>SVO GPU Pathfinding</b>", new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 });
        GUILayout.Space(5);

        int gridSize = pathfindingManager.VoxelWorld.GridSize;
        GUILayout.Label($"Grid: {gridSize}x{gridSize}x{gridSize}");
        GUILayout.Label($"Leaf nodes: {pathfindingManager.LeafCount}");

        if (lastResult != null)
        {
            int fc = 0, vc = 0;
            for (int i = 0; i < pathfindingManager.LeafCount; i++)
            {
                if (lastResult.PathData[i].state == 2) vc++;
                else if (lastResult.PathData[i].state == 1) fc++;
            }
            GUILayout.Label($"Frontier: {fc}");
            GUILayout.Label($"Visited: {vc}");
            GUILayout.Label($"Waypoints: {(lastResult.Waypoints != null ? lastResult.Waypoints.Count : 0)}");
        }

        string status;
        if (lastResult != null)
            status = lastResult.Success ? $"PATH FOUND ({(lastResult.Waypoints != null ? lastResult.Waypoints.Count : 0)} waypoints)" : "No path";
        else
            status = pathfindingManager.IsBusy ? "Running..." : "Idle";
        GUILayout.Label($"Status: {status}");

        GUILayout.Space(5);

        var svoData = pathfindingManager.VoxelWorld.SVOData;
        var sn = svoData.IsValidLeaf(startLeafIdx) ? svoData.leafNodes[startLeafIdx] : null;
        var gn = svoData.IsValidLeaf(goalLeafIdx) ? svoData.leafNodes[goalLeafIdx] : null;
        GUILayout.Label($"Start: {(sn != null ? $"({sn.leafX},{sn.leafY},{sn.leafZ})" : "not set")}");
        GUILayout.Label($"Goal: {(gn != null ? $"({gn.leafX},{gn.leafY},{gn.leafZ})" : "not set")}");

        GUILayout.Space(10);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Run", GUILayout.Width(70)))
        {
            RequestPath();
        }

        if (GUILayout.Button("Reset", GUILayout.Width(70)))
        {
            lastResult = null;
            viz.UpdateVisual(lastResult, startLeafIdx, goalLeafIdx);
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(5);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button(placeMode ? "Orbit" : "Place", GUILayout.Width(80)))
        {
            placeMode = !placeMode;
            if (placeMode) clickMode = "start";
            isDragging = false;
        }
        GUILayout.Label(placeMode ? $"Place {clickMode}" : "Orbit");
        GUILayout.EndHorizontal();

        if (placeMode)
        {
            GUILayout.Label("(Right-click: toggle start/goal)");
        }

        GUILayout.Space(5);
        GUILayout.Label($"Z-Slice: {viz.ZSlice}");
        float newSlice = GUILayout.HorizontalSlider(viz.ZSlice, 0, gridSize - 1);
        viz.SetZSlice(Mathf.RoundToInt(newSlice));

        bool newShowSlice = GUILayout.Toggle(viz.ShowSlice, "Show Slice");
        viz.SetShowSlice(newShowSlice);

        GUILayout.EndArea();
    }
}
