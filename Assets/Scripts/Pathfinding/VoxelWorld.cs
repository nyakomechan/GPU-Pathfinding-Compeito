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

        var colliders = FindObjectsOfType<Collider>();
        foreach (Collider col in colliders)
        {
            if (col.transform == transform) continue;
            if (col.gameObject.layer != wallLayer) continue;

            BoxCollider box = col as BoxCollider;
            if (box != null)
            {
                FillBoxCollider(g, box);
                continue;
            }

            SphereCollider sphere = col as SphereCollider;
            if (sphere != null)
            {
                FillSphereCollider(g, sphere);
                continue;
            }

            CapsuleCollider capsule = col as CapsuleCollider;
            if (capsule != null)
            {
                FillCapsuleCollider(g, capsule);
                continue;
            }

            FillColliderBounds(g, col.bounds);
        }

        return g;
    }

    private void FillColliderBounds(byte[] g, Bounds b)
    {
        int gs = gridSize;
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

    private void BoundsToVoxelRange(Bounds b, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        int gs = gridSize;
        minX = Mathf.Clamp(Mathf.RoundToInt(b.min.x - 0.5f), 0, gs - 1);
        minY = Mathf.Clamp(Mathf.RoundToInt(b.min.y - 0.5f), 0, gs - 1);
        minZ = Mathf.Clamp(Mathf.RoundToInt(b.min.z - 0.5f), 0, gs - 1);
        maxX = Mathf.Clamp(Mathf.RoundToInt(b.max.x - 0.5f), 0, gs - 1);
        maxY = Mathf.Clamp(Mathf.RoundToInt(b.max.y - 0.5f), 0, gs - 1);
        maxZ = Mathf.Clamp(Mathf.RoundToInt(b.max.z - 0.5f), 0, gs - 1);
    }

    private static Vector3Int VoxelCenterToLocal(int x, int y, int z)
    {
        return new Vector3Int(x, y, z);
    }

    private void FillBoxCollider(byte[] g, BoxCollider box)
    {
        Vector3 center = box.center;
        Vector3 halfSize = box.size * 0.5f;
        Transform t = box.transform;

        int minX, minY, minZ, maxX, maxY, maxZ;
        BoundsToVoxelRange(box.bounds, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

        int gs = gridSize;
        for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 voxelCenterWorld = new Vector3(x, y, z);
                    Vector3 local = t.InverseTransformPoint(voxelCenterWorld);
                    if (Mathf.Abs(local.x - center.x) <= halfSize.x &&
                        Mathf.Abs(local.y - center.y) <= halfSize.y &&
                        Mathf.Abs(local.z - center.z) <= halfSize.z)
                    {
                        g[x + y * gs + z * gs * gs] = 1;
                    }
                }
    }

    private void FillSphereCollider(byte[] g, SphereCollider sphere)
    {
        Vector3 center = sphere.center;
        float radius = sphere.radius;
        Transform t = sphere.transform;

        int minX, minY, minZ, maxX, maxY, maxZ;
        BoundsToVoxelRange(sphere.bounds, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

        int gs = gridSize;
        for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 voxelCenterWorld = new Vector3(x, y, z);
                    Vector3 local = t.InverseTransformPoint(voxelCenterWorld);
                    if (Vector3.Distance(local, center) <= radius)
                    {
                        g[x + y * gs + z * gs * gs] = 1;
                    }
                }
    }

    private void FillCapsuleCollider(byte[] g, CapsuleCollider capsule)
    {
        Vector3 center = capsule.center;
        float radius = capsule.radius;
        float height = capsule.height;
        int direction = capsule.direction;
        Transform t = capsule.transform;

        float lineHeight = Mathf.Max(0f, height - radius * 2f);
        Vector3 axis = Vector3.zero;
        if (direction == 0) axis = Vector3.right;
        else if (direction == 1) axis = Vector3.up;
        else axis = Vector3.forward;

        Vector3 p0 = center - axis * (lineHeight * 0.5f);
        Vector3 p1 = center + axis * (lineHeight * 0.5f);

        int minX, minY, minZ, maxX, maxY, maxZ;
        BoundsToVoxelRange(capsule.bounds, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

        int gs = gridSize;
        for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 voxelCenterWorld = new Vector3(x, y, z);
                    Vector3 local = t.InverseTransformPoint(voxelCenterWorld);
                    if (DistancePointToLineSegment(local, p0, p1) <= radius)
                    {
                        g[x + y * gs + z * gs * gs] = 1;
                    }
                }
    }

    private static float DistancePointToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float denom = Vector3.Dot(ab, ab);
        if (denom < 0.0001f) return Vector3.Distance(p, a);
        float t = Vector3.Dot(p - a, ab) / denom;
        t = Mathf.Clamp01(t);
        Vector3 closest = a + ab * t;
        return Vector3.Distance(p, closest);
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
