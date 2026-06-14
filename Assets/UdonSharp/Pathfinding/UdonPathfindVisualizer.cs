using UnityEngine;

public class UdonPathfindVisualizer : MonoBehaviour
{
    private static readonly Color WALL_COLOR = new Color(0.29f, 0.29f, 0.42f, 0.5f);
    private static readonly Color PATH_COLOR = new Color(1f, 0.82f, 0.4f, 1f);
    private static readonly Color START_MARKER_COLOR = new Color(0.02f, 0.84f, 0.63f, 1f);
    private static readonly Color GOAL_MARKER_COLOR = new Color(0.94f, 0.28f, 0.44f, 1f);
    private static readonly Color BORDER_COLOR = new Color(0.2f, 0.2f, 0.33f, 0.3f);

    public UdonPathfindingManager manager;

    private Mesh cubeMesh;
    private Mesh sphereMesh;
    private Material wallMat;
    private Material startMat;
    private Material goalMat;

    private Matrix4x4[] wallMatrices;
    private int wallCount;

    private GameObject startMarkerObj;
    private GameObject goalMarkerObj;
    private LineRenderer pathLineObj;

    private Vector3[] lastWaypoints;

    private const int MAX_INSTANCES_PER_DRAW = 1023;

    void Start()
    {
        if (manager == null)
        {
            Debug.LogWarning("UdonPathfindVisualizer: manager is not assigned");
            enabled = false;
            return;
        }

        CreateMeshes();
        CreateMaterials();
        CreateWallInstances();
        CreateMarkers();
        CreateBorderFrame();
    }

    private void CreateMeshes()
    {
        var cubeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeMesh = cubeGO.GetComponent<MeshFilter>().sharedMesh;
        Destroy(cubeGO);

        var sphereGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = sphereGO.GetComponent<MeshFilter>().sharedMesh;
        Destroy(sphereGO);
    }

    private Material CreateTransparentMaterial(Color color, int renderQueue = 3000)
    {
        var mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.enableInstancing = true;
        mat.SetFloat("_Glossiness", 0.1f);
        mat.SetFloat("_Metallic", 0f);
        if (color.a < 1f)
        {
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = renderQueue;
        }
        return mat;
    }

    private void CreateMaterials()
    {
        wallMat = CreateTransparentMaterial(WALL_COLOR, 2900);
        startMat = CreateTransparentMaterial(START_MARKER_COLOR, 3100);
        goalMat = CreateTransparentMaterial(GOAL_MARKER_COLOR, 3100);
    }

    private void CreateWallInstances()
    {
        int gs = manager.gridSize;
        int[] v2l = manager.voxelToLeaf;
        if (v2l == null) return;

        var positions = new System.Collections.Generic.List<Matrix4x4>();
        for (int z = 0; z < gs; z++)
        {
            for (int y = 0; y < gs; y++)
            {
                for (int x = 0; x < gs; x++)
                {
                    int vi = x + y * gs + z * gs * gs;
                    if (vi >= 0 && vi < v2l.Length && v2l[vi] == -1)
                    {
                        positions.Add(Matrix4x4.TRS(
                            new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                            Quaternion.identity,
                            Vector3.one
                        ));
                    }
                }
            }
        }
        wallCount = positions.Count;
        wallMatrices = positions.ToArray();
    }

    private void CreateMarkers()
    {
        startMarkerObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(startMarkerObj.GetComponent<Collider>());
        startMarkerObj.transform.localScale = Vector3.one * 0.5f;
        startMarkerObj.GetComponent<Renderer>().material = startMat;
        startMarkerObj.SetActive(false);

        goalMarkerObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(goalMarkerObj.GetComponent<Collider>());
        goalMarkerObj.transform.localScale = Vector3.one * 0.5f;
        goalMarkerObj.GetComponent<Renderer>().material = goalMat;
        goalMarkerObj.SetActive(false);
    }

    private void CreateBorderFrame()
    {
        int gs = manager.gridSize;

        var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(border.GetComponent<Collider>());
        border.transform.position = new Vector3(gs / 2f, gs / 2f, gs / 2f);
        border.transform.localScale = new Vector3(gs, gs, gs);
        border.GetComponent<Renderer>().material = CreateTransparentMaterial(BORDER_COLOR, 2500);

        var wireGO = new GameObject("Wireframe");
        var lr = wireGO.AddComponent<LineRenderer>();
        lr.positionCount = 24;
        lr.startWidth = 0.03f;
        lr.endWidth = 0.03f;
        lr.material = new Material(Shader.Find("Unlit/Color"));
        lr.material.color = new Color(0.2f, 0.2f, 0.33f, 0.6f);

        Vector3[] corners = new Vector3[]
        {
            new Vector3(0,0,0), new Vector3(gs,0,0), new Vector3(gs,0,gs), new Vector3(0,0,gs),
            new Vector3(0,gs,0), new Vector3(gs,gs,0), new Vector3(gs,gs,gs), new Vector3(0,gs,gs),
        };

        Vector3[] lines = new Vector3[]
        {
            corners[0], corners[1], corners[1], corners[2], corners[2], corners[3], corners[3], corners[0],
            corners[4], corners[5], corners[5], corners[6], corners[6], corners[7], corners[7], corners[4],
            corners[0], corners[4], corners[1], corners[5], corners[2], corners[6], corners[3], corners[7],
        };
        lr.SetPositions(lines);
    }

    private void LateUpdate()
    {
        RenderInstanced(wallMatrices, wallCount, wallMat, Vector3.one);
    }

    private void Update()
    {
        if (manager == null) return;

        UpdatePathLine(manager.waypoints);
        UpdateMarkers();
    }

    private void RenderInstanced(Matrix4x4[] matrices, int count, Material mat, Vector3 scale)
    {
        if (count == 0) return;
        int offset = 0;
        while (offset < count)
        {
            int batch = Mathf.Min(MAX_INSTANCES_PER_DRAW, count - offset);
            Matrix4x4[] batchMatrices = new Matrix4x4[batch];
            for (int i = 0; i < batch; i++)
            {
                Vector3 origPos = matrices[offset + i].GetColumn(3);
                batchMatrices[i] = Matrix4x4.TRS(origPos, Quaternion.identity, scale);
            }
            Graphics.DrawMeshInstanced(cubeMesh, 0, mat, batchMatrices, batch);
            offset += batch;
        }
    }

    private void UpdateMarkers()
    {
        if (manager.startLeafIdx >= 0)
        {
            startMarkerObj.transform.position = manager.startWorld;
            startMarkerObj.SetActive(true);
        }
        else
        {
            startMarkerObj.SetActive(false);
        }

        if (manager.goalLeafIdx >= 0)
        {
            goalMarkerObj.transform.position = manager.goalWorld;
            goalMarkerObj.SetActive(true);
        }
        else
        {
            goalMarkerObj.SetActive(false);
        }
    }

    private void UpdatePathLine(Vector3[] waypoints)
    {
        if (lastWaypoints == waypoints) return;
        lastWaypoints = waypoints;

        if (pathLineObj != null)
        {
            Destroy(pathLineObj.gameObject);
            pathLineObj = null;
        }

        if (waypoints == null || waypoints.Length < 2) return;

        GameObject go = new GameObject("PathLine");
        pathLineObj = go.AddComponent<LineRenderer>();
        pathLineObj.positionCount = waypoints.Length;
        pathLineObj.startWidth = 0.15f;
        pathLineObj.endWidth = 0.15f;
        pathLineObj.material = new Material(Shader.Find("Unlit/Color"));
        pathLineObj.material.color = PATH_COLOR;

        for (int i = 0; i < waypoints.Length; i++)
        {
            pathLineObj.SetPosition(i, waypoints[i]);
        }
    }

    private void OnDestroy()
    {
        if (wallMat != null) Destroy(wallMat);
        if (startMat != null) Destroy(startMat);
        if (goalMat != null) Destroy(goalMat);
    }
}
