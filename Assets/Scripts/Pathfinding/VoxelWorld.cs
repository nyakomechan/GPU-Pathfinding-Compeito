using UnityEngine;

public class VoxelWorld : MonoBehaviour
{
    [SerializeField] public int gridSize = 16;
    [SerializeField] public float heightFactor = 0.2f;
    [SerializeField] public int wallLayer = 6;

    private SVOBuilder builder;
    private SVOBuilder.SVOData svoData;

    public int GridSize => gridSize;
    public float HeightFactor => heightFactor;
    public int WallLayer => wallLayer;
    public int WallLayerMask => 1 << wallLayer;
    public SVOBuilder.SVOData SVOData => svoData;
    public int LeafCount => svoData?.leafCount ?? 0;

    public void Rebuild()
    {
        Rebuild(gridSize, heightFactor, wallLayer);
    }

    public void Rebuild(int gridSize, float heightFactor, int wallLayer)
    {
        this.gridSize = gridSize;
        this.heightFactor = heightFactor;
        this.wallLayer = wallLayer;

        byte[] grid = BuildGrid();

        builder = new SVOBuilder(gridSize);
        for (int z = 0; z < gridSize; z++)
            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                    builder.SetVoxel(x, y, z, grid[x + y * gridSize + z * gridSize * gridSize] != 0);

        svoData = builder.Build(heightFactor);
        Debug.Log($"[VoxelWorld] SVO rebuilt: {svoData.leafCount} leaf nodes, {svoData.nodes.Count} total nodes");
    }

    private byte[] BuildGrid()
    {
        int gs = gridSize;
        byte[] g = new byte[gs * gs * gs];

        var colliders = FindObjectsOfType<BoxCollider>();
        foreach (var col in colliders)
        {
            if (col.transform == transform) continue;
            if (col.gameObject.layer != wallLayer) continue;

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
                        g[x + y * gs + z * gs * gs] = 1;
        }

        return g;
    }

    public int WorldToLeaf(Vector3 worldPos)
    {
        return svoData?.WorldToLeaf(worldPos) ?? -1;
    }

    public Vector3 LeafToWorld(int leafIdx, bool center = true)
    {
        return svoData != null ? svoData.LeafToWorld(leafIdx, center) : Vector3.zero;
    }

    public int FindNearestEmptyLeaf(Vector3 worldPos)
    {
        return svoData?.FindNearestEmptyLeaf(worldPos) ?? -1;
    }

    public bool IsValidLeaf(int leafIdx)
    {
        return svoData != null && svoData.IsValidLeaf(leafIdx);
    }
}
