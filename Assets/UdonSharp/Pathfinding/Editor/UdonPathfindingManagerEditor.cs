using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(UdonPathfindingManager))]
public class UdonPathfindingManagerEditor : Editor
{
    private static readonly Dictionary<string, string> FieldDescriptions = new Dictionary<string, string>
    {
        { "pathfindMaterial", "Compeito material generated from PathfindCompeito shader. / PathfindCompeito シェーダーから生成された Compeito マテリアル。" },
        { "wallColliders", "Colliders treated as obstacles. Any Collider type is supported. / 障害物として扱うコライダー。任意の Collider タイプに対応。" },
        { "gridSizeX", "Number of voxel cells along the X axis. / X 軸方向のボクセルセル数。" },
        { "gridSizeY", "Number of voxel cells along the Y axis. / Y 軸方向のボクセルセル数。" },
        { "gridSizeZ", "Number of voxel cells along the Z axis. / Z 軸方向のボクセルセル数。" },
        { "cellSize", "World-space size of one voxel cell. / 1 つのボクセルセルのワールド空間でのサイズ。" },
        { "gridOrigin", "World position of the grid's minimum corner (0,0,0). / グリッドの最小角 (0,0,0) に対応するワールド座標。" },
        { "wallLayer", "Layer used for MeshCollider overlap checks. / MeshCollider の重なり判定に使用するレイヤー。" },
        { "heightFactor", "Cost multiplier for vertical movement. / 垂直方向の移動コストに掛ける係数。" },
        { "itersPerFrame", "Number of wavefront iterations processed per frame. / 1 フレームあたりに処理する wavefront イテレーション数。" },
        { "maxIterations", "Upper limit of iterations before giving up. / 探索を諦めるまでの最大イテレーション数。" },
        { "resultReceiver", "UdonSharpBehaviour that receives the result event. / 結果イベントを受け取る UdonSharpBehaviour。" },
        { "foundEventName", "Event name called when a path is found. / 経路発見時に呼ばれるイベント名。" },
        { "failedEventName", "Event name called when a path fails. / 経路失敗時に呼ばれるイベント名。" }
    };

    private const string PrefShowWallVoxels = "UdonPathfindingManagerEditor.ShowWallVoxels";
    private const string PrefMaxPreviewVoxels = "UdonPathfindingManagerEditor.MaxPreviewVoxels";
    private const string PrefEditGridRange = "UdonPathfindingManagerEditor.EditGridRange";

    private bool showWallVoxels = true;
    private int maxPreviewVoxels = 10000;
    private bool editGridRange;
    private bool pendingRebuild;

    private void OnEnable()
    {
        showWallVoxels = EditorPrefs.GetBool(PrefShowWallVoxels, true);
        maxPreviewVoxels = EditorPrefs.GetInt(PrefMaxPreviewVoxels, 10000);
        editGridRange = EditorPrefs.GetBool(PrefEditGridRange, false);
    }

    private void OnDisable()
    {
        Tools.hidden = false;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty prop = serializedObject.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (prop.name == "m_Script") continue;

            if (prop.name == "pathfindMaterial")
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            }
            else if (prop.name == "resultReceiver")
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);
            }

            if (FieldDescriptions.TryGetValue(prop.name, out string desc))
            {
                EditorGUILayout.HelpBox(desc, MessageType.Info);
            }

            EditorGUILayout.PropertyField(prop, true);
        }

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scene View", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        editGridRange = GUILayout.Toggle(editGridRange,"Edit Grid Range", GUI.skin.button);
        showWallVoxels =  GUILayout.Toggle(showWallVoxels,"Show Wall Voxels",GUI.skin.button);
        maxPreviewVoxels = EditorGUILayout.IntSlider("Max Preview Voxels", maxPreviewVoxels, 100, 50000);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(PrefEditGridRange, editGridRange);
            EditorPrefs.SetBool(PrefShowWallVoxels, showWallVoxels);
            EditorPrefs.SetInt(PrefMaxPreviewVoxels, maxPreviewVoxels);
            if (!editGridRange)
                Tools.hidden = false;
            SceneView.RepaintAll();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Rebuild"))
        {
            UdonPathfindingManager manager = (UdonPathfindingManager)target;
            Undo.RecordObject(manager, "Rebuild Pathfinding Grid");
            manager.Rebuild();
            EditorUtility.SetDirty(manager);
            SceneView.RepaintAll();
        }
    }

    private void OnSceneGUI()
    {
        UdonPathfindingManager manager = (UdonPathfindingManager)target;
        if (manager == null) return;

        Tools.hidden = editGridRange;

        float cellSize = manager.GetCellSize();
        if (cellSize <= 0f) return;

        Vector3 origin = manager.GetGridOrigin();
        int gsX = manager.GetGridSizeX();
        int gsY = manager.GetGridSizeY();
        int gsZ = manager.GetGridSizeZ();

        if (gsX <= 0 || gsY <= 0 || gsZ <= 0) return;

        Vector3 min = origin;
        Vector3 max = origin + new Vector3(gsX * cellSize, gsY * cellSize, gsZ * cellSize);
        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;

        Handles.color = Color.yellow;
        Handles.DrawWireCube(center, size);

        if (editGridRange)
        {
            EditorGUI.BeginChangeCheck();
            Handles.color = Color.blue;
            Vector3 newMin = Handles.PositionHandle(min, Quaternion.identity);
            Handles.color = Color.red;
            Vector3 newMax = Handles.PositionHandle(max, Quaternion.identity);

            if (EditorGUI.EndChangeCheck())
            {
                newMin = SnapToCell(newMin, cellSize);
                newMax = SnapToCell(newMax, cellSize);

                if (newMax.x < newMin.x + cellSize) newMax.x = newMin.x + cellSize;
                if (newMax.y < newMin.y + cellSize) newMax.y = newMin.y + cellSize;
                if (newMax.z < newMin.z + cellSize) newMax.z = newMin.z + cellSize;

                Vector3 newOrigin = newMin;
                int newSizeX = Mathf.Max(1, Mathf.RoundToInt((newMax.x - newMin.x) / cellSize));
                int newSizeY = Mathf.Max(1, Mathf.RoundToInt((newMax.y - newMin.y) / cellSize));
                int newSizeZ = Mathf.Max(1, Mathf.RoundToInt((newMax.z - newMin.z) / cellSize));

                Undo.RecordObject(manager, "Change Pathfinding Grid");
                manager.gridOrigin = newOrigin;
                manager.gridSizeX = newSizeX;
                manager.gridSizeY = newSizeY;
                manager.gridSizeZ = newSizeZ;
                EditorUtility.SetDirty(manager);
                pendingRebuild = true;
            }

            if (pendingRebuild && Event.current.type == EventType.MouseUp && Event.current.button == 0)
            {
                manager.Rebuild();
                pendingRebuild = false;
                Event.current.Use();
                SceneView.RepaintAll();
            }

            float capSize = HandleUtility.GetHandleSize(min) * 0.12f;
            Handles.color = Color.blue;
            Handles.CubeHandleCap(0, min, Quaternion.identity, capSize, EventType.Repaint);
            capSize = HandleUtility.GetHandleSize(max) * 0.12f;
            Handles.color = Color.red;
            Handles.CubeHandleCap(0, max, Quaternion.identity, capSize, EventType.Repaint);
        }

        if (showWallVoxels)
        {
            DrawWallVoxels(manager);
        }
    }

    private Vector3 SnapToCell(Vector3 worldPos, float cellSize)
    {
        return new Vector3(
            Mathf.Round(worldPos.x / cellSize) * cellSize,
            Mathf.Round(worldPos.y / cellSize) * cellSize,
            Mathf.Round(worldPos.z / cellSize) * cellSize
        );
    }

    private void DrawWallVoxels(UdonPathfindingManager manager)
    {
        byte[] grid = manager.GetGrid();
        if (grid == null) return;

        int gsX = manager.GetGridSizeX();
        int gsY = manager.GetGridSizeY();
        int gsZ = manager.GetGridSizeZ();
        float cellSize = manager.GetCellSize();
        Vector3 origin = manager.GetGridOrigin();

        if (gsX <= 0 || gsY <= 0 || gsZ <= 0 || cellSize <= 0f) return;
        if (grid.Length != gsX * gsY * gsZ) return;

        Handles.color = new Color(1f, 0.2f, 0.2f, 0.6f);

        int drawn = 0;
        bool exceeded = false;
        for (int z = 0; z < gsZ; z++)
        {
            for (int y = 0; y < gsY; y++)
            {
                for (int x = 0; x < gsX; x++)
                {
                    int idx = x + y * gsX + z * gsX * gsY;
                    if (grid[idx] == 0) continue;

                    if (drawn >= maxPreviewVoxels)
                    {
                        exceeded = true;
                        break;
                    }

                    Vector3 worldPos = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize;
                    float dotSize = HandleUtility.GetHandleSize(worldPos) * 0.04f;
                    Handles.DotHandleCap(0, worldPos, Quaternion.identity, dotSize, EventType.Repaint);
                    drawn++;
                }
                if (exceeded) break;
            }
            if (exceeded) break;
        }

        if (exceeded)
        {
            Handles.color = Color.red;
            Vector3 labelPos = origin + new Vector3(0f, (gsY + 1) * cellSize, 0f);
            Handles.Label(labelPos, $"Wall voxels exceed preview limit ({maxPreviewVoxels}+)");
        }
    }
}
