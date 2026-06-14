using UnityEngine;

public static class SVODataExtensions
{
    public static int WorldToLeaf(this SVOBuilder.SVOData data, Vector3 worldPos)
    {
        int x = Mathf.FloorToInt(worldPos.x);
        int y = Mathf.FloorToInt(worldPos.y);
        int z = Mathf.FloorToInt(worldPos.z);

        if (x < 0 || x >= data.gridSize || y < 0 || y >= data.gridSize || z < 0 || z >= data.gridSize)
            return -1;

        int vi = x + y * data.gridSize + z * data.gridSize * data.gridSize;
        return data.voxelToLeaf[vi];
    }

    public static Vector3 LeafToWorld(this SVOBuilder.SVOData data, int leafIdx, bool center = true)
    {
        if (leafIdx < 0 || leafIdx >= data.leafCount)
            return Vector3.zero;

        var leaf = data.leafNodes[leafIdx];
        float offset = center ? 0.5f : 0f;
        return new Vector3(leaf.leafX + offset, leaf.leafY + offset, leaf.leafZ + offset);
    }

    public static int FindNearestEmptyLeaf(this SVOBuilder.SVOData data, Vector3 worldPos)
    {
        int centerX = Mathf.RoundToInt(worldPos.x);
        int centerY = Mathf.RoundToInt(worldPos.y);
        int centerZ = Mathf.RoundToInt(worldPos.z);

        int gs = data.gridSize;
        int searchRadius = 1;
        int maxRadius = Mathf.Max(gs, Mathf.Max(gs, gs));

        while (searchRadius <= maxRadius)
        {
            for (int z = centerZ - searchRadius; z <= centerZ + searchRadius; z++)
            {
                for (int y = centerY - searchRadius; y <= centerY + searchRadius; y++)
                {
                    for (int x = centerX - searchRadius; x <= centerX + searchRadius; x++)
                    {
                        if (x < 0 || x >= gs || y < 0 || y >= gs || z < 0 || z >= gs)
                            continue;

                        if (Mathf.Abs(x - centerX) != searchRadius &&
                            Mathf.Abs(y - centerY) != searchRadius &&
                            Mathf.Abs(z - centerZ) != searchRadius)
                            continue;

                        int vi = x + y * gs + z * gs * gs;
                        int leafIdx = data.voxelToLeaf[vi];
                        if (leafIdx >= 0)
                            return leafIdx;
                    }
                }
            }
            searchRadius++;
        }

        return -1;
    }

    public static bool IsValidLeaf(this SVOBuilder.SVOData data, int leafIdx)
    {
        return leafIdx >= 0 && leafIdx < data.leafCount;
    }
}
