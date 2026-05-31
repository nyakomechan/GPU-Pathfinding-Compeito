export class SVOBuilder {
    constructor(gridSize) {
        this.gridSize = gridSize;
        this.grid = new Uint8Array(gridSize * gridSize * gridSize);
    }

    idx3(x, y, z) {
        return x + y * this.gridSize + z * this.gridSize * this.gridSize;
    }

    setVoxel(x, y, z, occupied) {
        if (x < 0 || x >= this.gridSize || y < 0 || y >= this.gridSize || z < 0 || z >= this.gridSize) return;
        this.grid[this.idx3(x, y, z)] = occupied ? 1 : 0;
    }

    getVoxel(x, y, z) {
        if (x < 0 || x >= this.gridSize || y < 0 || y >= this.gridSize || z < 0 || z >= this.gridSize) return 1;
        return this.grid[this.idx3(x, y, z)];
    }

    build() {
        const gs = this.gridSize;
        const maxLevel = Math.round(Math.log2(gs));
        const nodes = [];

        const buildRecursive = (ox, oy, oz, size, level) => {
            let allOccupied = true;
            let allEmpty = true;
            for (let dz = 0; dz < size && (allOccupied || allEmpty); dz++) {
                for (let dy = 0; dy < size && (allOccupied || allEmpty); dy++) {
                    for (let dx = 0; dx < size && (allOccupied || allEmpty); dx++) {
                        const v = this.getVoxel(ox + dx, oy + dy, oz + dz);
                        if (v) allEmpty = false;
                        else allOccupied = false;
                    }
                }
            }

            const isLeaf = size === 1 || allEmpty || allOccupied;
            const nodeIdx = nodes.length;

            nodes.push({
                idx: nodeIdx,
                childBase: -1,
                parent: -1,
                level,
                occupied: allOccupied,
                isLeaf,
                ox, oy, oz, size,
            });

            if (!isLeaf) {
                nodes[nodeIdx].childBase = nodes.length;
                const half = size / 2;
                const childIndices = [];

                for (let dz = 0; dz < 2; dz++)
                    for (let dy = 0; dy < 2; dy++)
                        for (let dx = 0; dx < 2; dx++) {
                            const ci = buildRecursive(ox + dx * half, oy + dy * half, oz + dz * half, half, level - 1);
                            childIndices.push(ci);
                        }

                for (const ci of childIndices) nodes[ci].parent = nodeIdx;
            }

            return nodeIdx;
        };

        buildRecursive(0, 0, 0, gs, maxLevel);

        const leafNodes = [];
        const voxelToLeaf = new Int32Array(gs * gs * gs).fill(-1);

        for (const node of nodes) {
            if (node.size === 1 && !node.occupied) {
                const leafIdx = leafNodes.length;
                node.leafIdx = leafIdx;
                node.leafX = node.ox;
                node.leafY = node.oy;
                node.leafZ = node.oz;
                leafNodes.push(node);
                voxelToLeaf[this.idx3(node.ox, node.oy, node.oz)] = leafIdx;
            }
        }

        const DIRS = [
            [1, 0, 0], [-1, 0, 0],
            [0, 1, 0], [0, -1, 0],
            [0, 0, 1], [0, 0, -1],
        ];

        const neighbors = new Int32Array(leafNodes.length * 6).fill(-1);
        for (let i = 0; i < leafNodes.length; i++) {
            const ln = leafNodes[i];
            for (let d = 0; d < 6; d++) {
                const nx = ln.leafX + DIRS[d][0];
                const ny = ln.leafY + DIRS[d][1];
                const nz = ln.leafZ + DIRS[d][2];
                if (nx >= 0 && nx < gs && ny >= 0 && ny < gs && nz >= 0 && nz < gs) {
                    neighbors[i * 6 + d] = voxelToLeaf[this.idx3(nx, ny, nz)];
                }
            }
        }

        return {
            nodes,
            leafNodes,
            neighbors,
            voxelToLeaf,
            gridSize: gs,
            leafCount: leafNodes.length,
        };
    }
}
